using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosslight.Core.Services.Engine;

// ============================================================================
//  Modrinth 数据层：搜索 / 项目 / 版本
//  （移植自 1.0.0 的 core/modrinth_api.py）
//
//  ## 关于镜像
//
//  Modrinth 的 API 和 CDN **目前没有 BMCLAPI 镜像**（BMCLAPI 只镜像游戏本体：
//  清单 / 库 / 资源）。这里走 <see cref="DownloadManager.MirrorUrl"/> 只是为了
//  **统一**：映射表里没有前缀时它原样返回，将来真有镜像了往映射表加一行就行。
//
//  ## 返回的是**原始 JSON**，不是自定义模型
//
//  跟 1.0.0 保持一致。界面那边（卡片、详情页）全是照 Modrinth 的字段名写的
//  （`hits` / `slug` / `icon_url` / `game_versions` …），包一层自定义对象会让
//  每个取值都要改一遍，收益却只是"类型好看"。**防呆取值放在 UI 那一层**，
//  数据层只负责"把 HTTP 和 JSON 弄干净"。
//
//  ## 搜索接口的一个坑
//
//  `index="relevance"` 配**空查询**是没有意义的 —— Modrinth 会按自己的默认
//  顺序返回一串看不出规律的东西（没有"最热门"可言）。所以空查询时改成
//  `downloads`。
// ============================================================================

/// <summary>Modrinth 那边出问题了（网络 / HTTP 状态码 / 返回的不是 JSON）。</summary>
public sealed class ModrinthException : Exception
{
    /// <summary>HTTP 状态码；网络层面挂掉时是 0。</summary>
    public int Code { get; }

    public ModrinthException(string message, int code = 0) : base(message)
    {
        Code = code;
    }

    public ModrinthException(string message, int code, Exception inner) : base(message, inner)
    {
        Code = code;
    }
}

/// <summary>Modrinth API。</summary>
public static class ModrinthApi
{
    public const string Api = "https://api.modrinth.com/v2";

    /// <summary>JSON 取不到时的重试次数与退避。</summary>
    private const int Retry = 2;

    private const double Backoff = 0.6;

    /// <summary>搜索的排序方式（Modrinth 的 `index` 参数）。值是接口原文，**不翻译**。</summary>
    public static readonly string[] Sorts = { "relevance", "downloads", "follows", "newest", "updated" };

    public const string DefaultSort = "relevance";

    /// <summary>Modrinth 单页上限。</summary>
    public const int MaxLimit = 100;

    /// <summary>"全部"这个筛选值（跟 1.0.0 的字符串常量一致，界面直接比它）。</summary>
    public const string AnyOption = "全部";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(DownloadManager.UserAgent);
        return client;
    }

    // ---------- HTTP ----------

    /// <summary>
    /// 发一次 GET 并解 JSON；失败抛 <see cref="ModrinthException"/>。
    ///
    /// ⚠️ 返回值必须是对象或数组：Modrinth 偶尔会在 200 里返回别的东西
    ///    （维护页 / 纯文本），解出来是字符串的话，后面取值会炸在很远的地方，
    ///    报错还看不出是网络问题。在这里拦住。
    /// </summary>
    private static async Task<JsonNode> GetJsonAsync(
        string url, string? query = null, CancellationToken token = default)
    {
        var full = url;
        if (!string.IsNullOrEmpty(query))
        {
            full += (url.Contains('?') ? "&" : "?") + query;
        }

        try
        {
            using var response = await Http.GetAsync(full, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ModrinthException(
                    Localization.Localizer.Tr("Modrinth 返回 {code}", ("code", (int)response.StatusCode)),
                    (int)response.StatusCode);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            var node = JsonIO.ParseBytes(bytes);
            if (node is not JsonObject and not JsonArray)
            {
                throw new ModrinthException(Localization.Localizer.Tr("Modrinth 返回了看不懂的内容"));
            }
            return node;
        }
        catch (ModrinthException)
        {
            throw;
        }
        catch (TaskCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new ModrinthException(Localization.Localizer.Tr("连接 Modrinth 超时"), 0, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ModrinthException(
                Localization.Localizer.Tr("连不上 Modrinth（网络或代理）"), 0, ex);
        }
        catch (JsonException ex)
        {
            throw new ModrinthException(Localization.Localizer.Tr("Modrinth 返回的不是 JSON"), 0, ex);
        }
    }

    /// <summary>
    /// 镜像优先 + 重试。**404 不重试**（那是"没有这个 mod"，重试没用）。
    /// </summary>
    public static Task<JsonNode> HttpJsonAsync(
        string url, string? query = null, CancellationToken token = default)
        => HttpJsonCoreAsync(url, query, token);

    private static async Task<JsonNode> HttpJsonCoreAsync(
        string url, string? query, CancellationToken token)
    {
        var mirrored = DownloadManager.MirrorUrl(url);
        var candidates = mirrored == url ? new[] { url } : new[] { mirrored, url };

        ModrinthException? last = null;
        for (var attempt = 0; attempt <= Retry; attempt++)
        {
            foreach (var candidate in candidates)
            {
                try
                {
                    return await GetJsonAsync(candidate, query, token).ConfigureAwait(false);
                }
                catch (ModrinthException ex)
                {
                    last = ex;
                    if (ex.Code == 404)
                    {
                        throw;      // 换源 / 重试都救不了
                    }
                }
            }
            if (attempt < Retry)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Backoff * (attempt + 1)), token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }
        }
        throw last ?? new ModrinthException(Localization.Localizer.Tr("取不到 Modrinth 数据"));
    }

    private static int ClampLimit(int limit, int fallback = 20)
        => Math.Max(1, Math.Min(MaxLimit, limit > 0 ? limit : fallback));

    // ---------- 搜索 ----------

    /// <summary>设计说明见 docs/design-notes.md#mosslight-core-services-engine-modrinthapi-cs</summary>
    public static async Task<JsonObject> SearchProjectsAsync(
        string query = "",
        string mcVersion = AnyOption,
        string loader = AnyOption,
        string category = AnyOption,
        string projectType = "mod",
        string sort = DefaultSort,
        int limit = 20,
        int offset = 0,
        CancellationToken token = default)
    {
        var type = string.IsNullOrEmpty(projectType) ? "mod" : projectType;
        var facets = new JsonArray { new JsonArray($"project_type:{type}") };

        if (!string.IsNullOrEmpty(mcVersion) && mcVersion != AnyOption)
        {
            facets.Add(new JsonArray($"versions:{mcVersion}"));
        }
        if (!string.IsNullOrEmpty(loader) && loader != AnyOption && type is "mod" or "modpack")
        {
            facets.Add(new JsonArray($"categories:{loader.ToLowerInvariant()}"));
        }
        if (!string.IsNullOrEmpty(category) && category != AnyOption)
        {
            facets.Add(new JsonArray($"categories:{category}"));
        }

        var index = Sorts.Contains(sort) ? sort : DefaultSort;
        var trimmed = (query ?? string.Empty).Trim();
        // 空查询按下载量排（理由见类注释）
        if (trimmed.Length == 0 && index == "relevance")
        {
            index = "downloads";
        }

        var clampedLimit = ClampLimit(limit);
        // 紧凑 JSON：facets 里带空格会被转义成 %20，日志里没法看
        var facetsJson = facets.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

        var queryString =
            $"query={Uri.EscapeDataString(trimmed)}" +
            $"&facets={Uri.EscapeDataString(facetsJson)}" +
            $"&index={Uri.EscapeDataString(index)}" +
            $"&limit={clampedLimit}" +
            $"&offset={Math.Max(0, offset)}";

        var data = await HttpJsonAsync($"{Api}/search", queryString, token).ConfigureAwait(false);
        if (data is not JsonObject obj)
        {
            return new JsonObject
            {
                ["hits"] = new JsonArray(),
                ["total_hits"] = 0,
                ["offset"] = 0,
                ["limit"] = clampedLimit,
            };
        }
        obj["hits"] ??= new JsonArray();
        obj["total_hits"] ??= 0;
        return obj;
    }

    // ---------- 筛选项（tag） ----------
    //
    // 搜索卡上那四个下拉（游戏版本 / 加载器 / 分类 / 排序）里，加载器和排序是
    // 固定的（产品名 + 接口常量，见 Sorts），另外两个**会跟着 Modrinth 的数据变**：
    // 新版本出了、分类改名了，硬编码的表就会慢慢变成错的。所以这两个问接口要，
    // 界面层拿一张小表补中文名（`ModFilters`），表里没有的就把接口原文显示出来
    // —— 显示英文名总比"这一项不存在"好。

    /// <summary>设计说明见 docs/design-notes.md#mosslight-core-services-engine-modrinthapi-cs</summary>
    public static async Task<IReadOnlyList<string>> CategoriesAsync(
        string projectType, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(projectType))
        {
            return Array.Empty<string>();
        }

        var data = await HttpJsonAsync($"{Api}/tag/category", null, token).ConfigureAwait(false);
        if (data is not JsonArray rows)
        {
            return Array.Empty<string>();
        }

        return rows.OfType<JsonObject>()
            .Where(o => string.Equals(
                            o["project_type"]?.GetValue<string>(), projectType, StringComparison.Ordinal)
                        && string.Equals(
                            o["header"]?.GetValue<string>(), "categories", StringComparison.Ordinal))
            .Select(o => o["name"]?.GetValue<string>() ?? string.Empty)
            .Where(name => name.Length > 0)
            .ToList();
    }

    /// <summary>
    /// 正式版的游戏版本列表（`/v2/tag/game_version`，**新的在前**）。
    ///
    /// ⚠️ 只要 `version_type == "release"`：快照 / 预发布 / 远古版本塞进下拉里会很长，
    ///    而且**模组基本都不支持**（拿一个快照去筛几乎必然 0 条，用户只会以为是坏的）。
    /// ⚠️ 排序**不靠接口返回的顺序**，按 <see cref="LoaderCatalog.VersionKey"/> 自己排：
    ///    接口这次是从旧到新，下次换个镜像可能就不是了 —— 顺序是"猜的"那种地方，
    ///    出了问题（最新版排在倒数第一）很难看出来是顺序错了。
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReleaseVersionsAsync(CancellationToken token = default)
    {
        var data = await HttpJsonAsync($"{Api}/tag/game_version", null, token).ConfigureAwait(false);
        if (data is not JsonArray rows)
        {
            return Array.Empty<string>();
        }

        var versions = rows.OfType<JsonObject>()
            .Where(o => string.Equals(
                o["version_type"]?.GetValue<string>(), "release", StringComparison.Ordinal))
            .Select(o => o["version"]?.GetValue<string>() ?? string.Empty)
            .Where(v => v.Length > 0)
            .OrderByDescending(LoaderCatalog.VersionKey, Comparer<int[]>.Create(LoaderCatalog.CompareVersionKeys))
            .ToList();

        return versions;
    }

    // ---------- 项目 ----------

    /// <summary>
    /// 项目详情（<paramref name="projectId"/> 可以是 id，也可以是 slug 比如 "sodium"）。
    ///
    /// 没有这个项目（404）返回 null；网络不通抛 <see cref="ModrinthException"/>。
    /// </summary>
    public static async Task<JsonObject?> GetProjectAsync(string projectId, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(projectId))
        {
            return null;
        }
        try
        {
            var data = await HttpJsonAsync($"{Api}/project/{projectId}", null, token).ConfigureAwait(false);
            return data as JsonObject;
        }
        catch (ModrinthException ex) when (ex.Code == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// 项目的所有版本（**新的在前**）。
    ///
    /// 没有这个项目返回空表；网络不通抛 <see cref="ModrinthException"/>。
    ///
    /// ⚠️ 不在这里按游戏版本 / 加载器筛：详情页是**按分组展示全部版本**的
    ///    （用户可能想找旧版本的），筛了反而看不到。要筛就用
    ///    <see cref="FilterVersions"/>。
    /// </summary>
    public static async Task<List<JsonObject>> GetProjectVersionsAsync(
        string projectId, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(projectId))
        {
            return new List<JsonObject>();
        }

        JsonNode data;
        try
        {
            data = await HttpJsonAsync($"{Api}/project/{projectId}/version", null, token)
                .ConfigureAwait(false);
        }
        catch (ModrinthException ex) when (ex.Code == 404)
        {
            return new List<JsonObject>();
        }

        if (data is not JsonArray array)
        {
            return new List<JsonObject>();
        }

        // 设计说明见 docs/design-notes.md#mosslight-core-services-engine-modrinthapi-cs
        return array
            .OfType<JsonObject>()
            .OrderByDescending(v => v["date_published"]?.GetValue<string>() ?? string.Empty,
                StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 项目的版本里挑出**匹配游戏版本 + 加载器**的（顺序不变 = 新的在前）。
    ///
    /// ⚠️ 按 `game_versions` / `loaders` 两个字段筛，**不在服务端筛**：
    ///    `/project/&lt;id&gt;/version` 一次给全部版本，本地筛省一次往返。
    ///
    /// ⚠️ 筛不出东西就**返回空**，别"退而求其次"随便给一个 —— 装错加载器的
    ///    mod 只会让游戏起不来，比"没得选"更糟。
    /// </summary>
    public static List<JsonObject> FilterVersions(
        IEnumerable<JsonObject>? versions, string mcVersion = "", string loader = "")
    {
        var mc = (mcVersion ?? string.Empty).Trim();
        var want = (loader ?? string.Empty).Trim().ToLowerInvariant();
        var result = new List<JsonObject>();

        foreach (var v in versions ?? Enumerable.Empty<JsonObject>())
        {
            if (mc.Length > 0)
            {
                var gameVersions = (v["game_versions"] as JsonArray ?? new JsonArray())
                    .Select(x => x?.GetValue<string>() ?? string.Empty)
                    .ToList();
                if (!gameVersions.Contains(mc, StringComparer.Ordinal))
                {
                    continue;
                }
            }
            if (want.Length > 0)
            {
                var loaders = (v["loaders"] as JsonArray ?? new JsonArray())
                    .Select(x => (x?.GetValue<string>() ?? string.Empty).ToLowerInvariant())
                    .ToList();
                if (!loaders.Contains(want, StringComparer.Ordinal))
                {
                    continue;
                }
            }
            result.Add(v);
        }
        return result;
    }

    /// <summary>
    /// 版本里该下的那个文件（优先 primary，没有就第一个）。
    ///
    /// 一个版本可能带多个文件（sources / javadoc 之类），那些装进 `mods\`
    /// 只会让游戏报错。拿不到返回 null。
    /// </summary>
    public static JsonObject? PrimaryFile(JsonObject? version)
    {
        var files = (version?["files"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .ToList();
        if (files.Count == 0)
        {
            return null;
        }

        foreach (var f in files)
        {
            var primary = f["primary"] is JsonValue v && v.GetValueKind() == JsonValueKind.True;
            if (primary && !string.IsNullOrEmpty(f["url"]?.GetValue<string>()))
            {
                return f;
            }
        }
        foreach (var f in files)
        {
            if (!string.IsNullOrEmpty(f["url"]?.GetValue<string>()))
            {
                return f;
            }
        }
        return null;
    }

    /// <summary>一个版本 → 一条下载任务（**纯函数**）。</summary>
    /// <param name="kind">
    /// 下载任务上那个"这是什么"的标签（进度行会显示）。不给就按 <paramref name="projectType"/>
    /// 猜（见 <see cref="TypeLabel"/>）—— 2026-10 之前这里写死"模组"，
    /// 资源包的进度行会显示成"正在下载 xxx（模组）"，那是假的。
    /// </param>
    public static InstallTask TaskFromVersion(
        JsonObject version, string destDir, string projectType = "mod", string? kind = null)
    {
        var file = PrimaryFile(version)
            ?? throw new LoaderInstallException(Localization.Localizer.Tr("这个 mod 版本没有可下载的文件"));

        var filename = file["filename"]?.GetValue<string>() ?? string.Empty;
        if (filename.Length == 0)
        {
            throw new LoaderInstallException(Localization.Localizer.Tr("这个 mod 版本没有文件名"));
        }

        return new InstallTask(
            Url: file["url"]?.GetValue<string>() ?? string.Empty,
            Path: Path.Combine(destDir, filename),
            Sha1: (file["hashes"] as JsonObject)?["sha1"]?.GetValue<string>() ?? string.Empty,
            Size: file["size"]?.GetValue<long>() ?? 0,
            Label: filename,
            Kind: kind ?? TypeLabel(projectType));
    }

    /// <summary>
    /// Modrinth 的 `project_type` → 界面上那个类型名（"模组 / 资源包 / 光影包 …"）。
    ///
    /// 认不出来的类型当 mod（**不能返回空串**：下载任务那一栏会显示成一片空白）。
    /// ⚠️ 界面那边（`ModsPage.TypeLabels`）另有一份带"原样显示未知类型"的表 ——
    ///    那份是给卡片徽章用的，认不出来时要显示接口原文；这份只用于下载任务的标签，
    ///    两边的兜底不一样是**故意的**。
    /// </summary>
    public static string TypeLabel(string? projectType) => (projectType ?? string.Empty).ToLowerInvariant() switch
    {
        "modpack" => Localization.Localizer.Tr("整合包"),
        "datapack" => Localization.Localizer.Tr("数据包"),
        "resourcepack" => Localization.Localizer.Tr("资源包"),
        "shader" => Localization.Localizer.Tr("光影包"),
        _ => Localization.Localizer.Tr("模组"),
    };

    /// <summary>MC 百科搜索页（**直达词条**的解析不在这里，这一步只是兜底）。</summary>
    public static string McmodSearchUrl(string name)
        => $"https://search.mcmod.cn/s?key={Uri.EscapeDataString(name ?? string.Empty)}";
}
