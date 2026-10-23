using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosslight.Core.Services.Engine;

// Modrinth 数据层：搜索 / 项目 / 版本，返回原始 JSON，防呆取值放在界面层。
// 目前没有 BMCLAPI 镜像，走 MirrorUrl 只是为了统一；空查询时把 relevance 改成 downloads。

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

    // JSON 取不到时的重试次数与退避。
    private const int Retry = 2;

    private const double Backoff = 0.6;

    /// <summary>搜索的排序方式（Modrinth 的 `index` 参数）。</summary>
    public static readonly string[] Sorts = { "relevance", "downloads", "follows", "newest", "updated" };

    public const string DefaultSort = "relevance";

    /// <summary>Modrinth 单页上限。</summary>
    public const int MaxLimit = 100;

    /// <summary>"全部"这个筛选值。</summary>
    public const string AnyOption = "全部";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(DownloadManager.UserAgent);
        return client;
    }

    // HTTP

    /// <summary>
    /// 发一次 GET 并解 JSON；失败抛 ModrinthException；⚠️ 返回值必须是对象或数组：Modrinth 偶尔会
    /// 在 200 里返回别的东西 （维护页 / 纯文本），解出来是字符串的话，后面取值会炸在很远的地方，
    /// 报错还看不出是网络问题。
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

    /// <summary>镜像优先 + 重试。</summary>
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

    // 搜索

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

    // 游戏版本和分类的候选要问 Modrinth 接口，不要硬编码；界面层只补中文显示名。

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
    /// 正式版的游戏版本列表（`/v2/tag/game_version`，**新的在前**）；⚠️ 只要
    /// `version_type == "release"`：快照 / 预发布 / 远古版本塞进下拉里会很长，而且
    /// **模组基本都不支持**（拿一个快照去筛几乎必然 0 条，用户只会以为是坏的）。
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

    // 项目

    /// <summary>项目详情（projectId 可以是 id，也可以是 slug 比如 "sodium"）。</summary>
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
    /// 项目的所有版本（**新的在前**）；⚠️ 不在这里按游戏版本 / 加载器筛：详情页是
    /// **按分组展示全部版本**的 （用户可能想找旧版本的），筛了反而看不到。
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

        return array
            .OfType<JsonObject>()
            .OrderByDescending(v => v["date_published"]?.GetValue<string>() ?? string.Empty,
                StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 项目的版本里挑出**匹配游戏版本 + 加载器**的（顺序不变 = 新的在前）；⚠️ 按 `game_versions`
    /// / `loaders` 两个字段筛，**不在服务端筛**：`/project/&lt;id&gt;/version` 一次给全部版本，本
    /// 地筛省一次往返。
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

    /// <summary>版本里该下的那个文件（优先 primary，没有就第一个）。</summary>
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
