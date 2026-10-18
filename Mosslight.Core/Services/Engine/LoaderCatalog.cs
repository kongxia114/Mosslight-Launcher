using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosslight.Core.Services.Engine;

// ============================================================================
//  模组加载器数据（下载页第二页用）
//  （移植自 1.0.0 的 core/loaders.py）
//
//  五家：Forge / NeoForge / Fabric / Quilt / OptiFine。
//  每一行有三种状态，界面必须照实显示：
//
//      Ok             能列出并安装
//      NotImplemented 我们还没做
//      Error          有接口但这次拉不到（网络问题）—— 这个**不能**说成"还没做"，
//                     否则用户以为功能不存在，而其实只是网络抖了一下
//
//  ## 接口（都实测过）
//
//  | 加载器    | 接口                                        |
//  |-----------|---------------------------------------------|
//  | Forge     | BMCLAPI `/forge/minecraft/<mc>`             |
//  | NeoForge  | BMCLAPI `/neoforge/list/<mc>`               |
//  | Fabric    | BMCLAPI `/fabric-meta/v2/versions/loader/<mc>` |
//  | Quilt     | 官方 `meta.quiltmc.org/v3/versions/loader/<mc>` |
//  | OptiFine  | BMCLAPI `/optifine/<mc>`                    |
//
//  ⚠️ **Quilt 没有 BMCLAPI 镜像**：`/quilt-meta/...` 和 `/quilt/...` 都是 **404**
//     （`COMMON_NO_SUCH_OBJECT`），所以走官方 meta。
//  ⚠️ **Fabric 有 BMCLAPI 镜像**（`/fabric-meta/...`），比官方在国内稳。
// ============================================================================

/// <summary>
/// 一行加载器版本。
///
/// ⚠️ 这几个模型（<see cref="LoaderVersion"/> / <see cref="LoaderRow"/> /
///    <see cref="LoaderState"/>）**定义在 Services/Models.cs 里**，因为它们
///    是服务契约的一部分（<see cref="ILoaderService"/> 的返回类型）。
///    这个文件只放"怎么解析 / 怎么拉"的逻辑。
/// </summary>
public sealed class LoaderCatalog
{
    public const string UserAgent = "Mosslight/0.5 (+https://github.com/kongxia114/Mosslight-Launcher)";

    public const string Bmclapi = "https://bmclapi2.bangbang93.com";

    /// <summary>
    /// ⚠️ Fabric / Quilt 的 meta 是两套不同的服务：Fabric 有 BMCLAPI 镜像
    /// （国内稳），Quilt **没有**（实测 404），只能走官方。
    /// </summary>
    public const string FabricMeta = Bmclapi + "/fabric-meta";

    public const string QuiltMeta = "https://meta.quiltmc.org";

    /// <summary>
    /// Legacy Fabric（Fabric 团队维护的"老版本"分支，1.8 ~ 1.14 那批游戏）。
    /// 它的 meta 跟 Fabric 是**同一个 schema**（实测 `/v2/versions/loader/1.12.2`
    /// 与 Fabric 官方返回的字段、`profile/json` 的 `inheritsFrom` / `mainClass`
    /// 都对得上），所以列表和 profile 两条路都能复用 Fabric 的代码。
    /// ⚠️ BMCLAPI 没有镜像它（`/legacyfabric-meta` 是 404），只能走官方。
    /// </summary>
    public const string LegacyFabricMeta = "https://meta.legacyfabric.net";

    /// <summary>
    /// 第二页从上到下显示哪些加载器。顺序照 PCL（常用的在前）。
    ///
    /// ⚠️ 这张表跟界面上「安装包」那一段是**同一套 key**（那边由它生成左栏
    ///    和安装选项页的分组，见 `DownloadPage.BuildNavTable` / `BuildLoaderGroups`），
    ///    自检里对着两边是否一致（`CheckLoaderKeyParity`）。
    /// ⚠️ 表里有一家、<see cref="Supported"/> 里没有它 = 界面上老实显示"安装还没接"。
    ///    别为了"看着干净"把没做的从这张表里删掉：用户会以为启动器根本不支持。
    /// </summary>
    public static readonly string[] LoaderOrder =
    {
        "forge", "neoforge", "cleanroom", "fabric", "legacy_fabric", "quilt",
        "optifine", "liteloader", "labymod",
    };

    /// <summary>显示名（键是内部 key；这些是产品名，不翻译）。</summary>
    public static readonly IReadOnlyDictionary<string, string> LoaderNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["forge"] = "Forge",
            ["neoforge"] = "NeoForge",
            ["fabric"] = "Fabric",
            ["legacy_fabric"] = "Legacy Fabric",
            ["quilt"] = "Quilt",
            ["optifine"] = "OptiFine",
            ["liteloader"] = "LiteLoader",
            ["labymod"] = "LabyMod",
            ["cleanroom"] = "Cleanroom",
        };

    /// <summary>
    /// 有接口的（其余是 <see cref="LoaderState.NotImplemented"/>）。
    /// ⚠️ 加 key 的**同时**得有 <see cref="UrlFor"/> 和 <see cref="LoaderVersionsAsync"/> 里的
    ///    parser，否则会走成"网络错误"那种说法（用户会一直重试）。
    /// </summary>
    public static readonly string[] Supported =
    {
        "forge", "neoforge", "cleanroom", "fabric", "legacy_fabric", "quilt",
        "optifine", "liteloader",
    };

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    // ---------- 版本号比较 ----------

    /// <summary>
    /// 把版本号转成可比较的键（`0.19.5` → `[0, 19, 5]`）。
    ///
    /// ⚠️ 用**分段数字**比较，不要用字符串比：字符串比会把 `"0.9"` 排在 `"0.19"`
    ///    前面（`"9" > "1"`），于是"最新版"变成 0.9 —— 这是最容易埋进去的一个错。
    ///    预发布后缀（`0.20.0-beta.9`）拿不到正式号时按 0 处理，靠稳定标记决定顺序。
    /// </summary>
    public static int[] VersionKey(string? text)
    {
        var normalized = (text ?? string.Empty).Replace('-', '.');
        var parts = normalized.Split('.');
        var outKeys = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            var digits = new string(part.Where(char.IsDigit).ToArray());
            outKeys.Add(digits.Length > 0 && int.TryParse(digits, out var n) ? n : 0);
        }
        return outKeys.Count > 0 ? outKeys.ToArray() : new[] { 0 };
    }

    /// <summary>按 <see cref="VersionKey"/> 的规则比大小（逐段比，短的排前面）。</summary>
    public static int CompareVersionKeys(int[] a, int[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        for (var i = 0; i < len; i++)
        {
            var cmp = a[i].CompareTo(b[i]);
            if (cmp != 0)
            {
                return cmp;
            }
        }
        return a.Length.CompareTo(b.Length);
    }

    // ---------- 解析 ----------

    /// <summary>
    /// 把"接口回包"变成统一的行列表。
    ///
    /// ⚠️ `data` 的形状**不由解析器负责**：网络层只保证"是个 JSON"，
    ///    回包可能是 dict（某些镜像会把列表包一层）、可能是别的奇怪东西。
    ///    所以这里统一挡一道 —— 形状不对就当空列表，**不要**让
    ///    "遍历一个数字"这种东西抛出来（那会一路冒到界面上变成一个看不懂的报错，
    ///    而实际上"这家没数据"是正常情况）。
    /// </summary>
    private static List<LoaderVersion> RowsFrom(JsonNode? data, Func<JsonObject, LoaderVersion?> parse)
    {
        if (data is JsonObject obj)
        {
            // 有些镜像把列表包一层，比如 {"versions": [...]}
            JsonNode? unwrapped = null;
            foreach (var key in new[] { "versions", "list", "data", "result" })
            {
                if (obj[key] is JsonArray)
                {
                    unwrapped = obj[key];
                    break;
                }
            }
            if (unwrapped is null)
            {
                return new List<LoaderVersion>();
            }
            data = unwrapped;
        }

        if (data is not JsonArray array)
        {
            return new List<LoaderVersion>();
        }

        var rows = new List<(LoaderVersion Row, int[] Sort)>();
        foreach (var item in array)
        {
            if (item is not JsonObject o)
            {
                continue;
            }
            var row = parse(o);
            if (row is not null)
            {
                rows.Add((row, VersionKey(SortKeyOf(row))));
            }
        }

        // 版本号从新到旧
        rows.Sort((x, y) => CompareVersionKeys(y.Sort, x.Sort));
        return rows.Select(r => r.Row).ToList();
    }

    /// <summary>排序用的那个"版本号"跟显示的 version 不一定一样（Forge 要切掉前面的 mc 版本）。</summary>
    private static string SortKeyOf(LoaderVersion row) => row.Version;

    private static string? Str(JsonObject obj, string key)
        => obj[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String
            ? v.GetValue<string>()
            : null;

    private static int Int(JsonObject obj, string key)
    {
        if (obj[key] is not JsonValue v)
        {
            return 0;
        }
        return v.GetValueKind() switch
        {
            JsonValueKind.Number => v.GetValue<int>(),
            JsonValueKind.String => int.TryParse(v.GetValue<string>(), out var n) ? n : 0,
            _ => 0,
        };
    }

    /// <summary>BMCLAPI `/forge/minecraft/&lt;mc&gt;` → 统一行（版本号大的在前）。</summary>
    public static List<LoaderVersion> ParseForge(JsonNode? data)
    {
        var rows = new List<(LoaderVersion Row, int[] Sort)>();

        void Collect(JsonNode? node)
        {
            if (node is JsonObject wrapped)
            {
                node = wrapped["versions"] ?? wrapped["list"] ?? wrapped["data"] ?? wrapped["result"];
            }
            if (node is not JsonArray array)
            {
                return;
            }

            foreach (var item in array)
            {
                if (item is not JsonObject o)
                {
                    continue;
                }
                var version = Str(o, "version");
                if (string.IsNullOrEmpty(version))
                {
                    continue;
                }

                // 安装器那一条（category=installer && format=jar）
                string? installerHash = null;
                if (o["files"] is JsonArray files)
                {
                    foreach (var f in files)
                    {
                        if (f is JsonObject file &&
                            Str(file, "category") == "installer" &&
                            Str(file, "format") == "jar")
                        {
                            installerHash = Str(file, "hash");
                            break;
                        }
                    }
                }

                // 排序：Forge 的 version 形如 `1.20.1-47.2.0`，取横线后面那段
                var tail = version.Contains('-') ? version[(version.LastIndexOf('-') + 1)..] : version;
                rows.Add((new LoaderVersion(
                    Version: version,
                    Modified: Str(o, "modified") ?? string.Empty,
                    Stable: true,                   // Forge 不分 release/beta
                    Build: Int(o, "build"),
                    InstallerHash: installerHash), VersionKey(tail)));
            }
        }

        Collect(data);
        rows.Sort((x, y) => CompareVersionKeys(y.Sort, x.Sort));
        return rows.Select(r => r.Row).ToList();
    }

    /// <summary>
    /// BMCLAPI `/neoforge/list/&lt;mc&gt;` → 统一行。
    ///
    /// 字段很短：`{"mcversion": "1.20.1", "version": "47.1.5", "rawVersion": "1.20.1-47.1.5"}`。
    /// ⚠️ **版本号用 `version` 而不是 `rawVersion`** —— 界面上显示 `47.1.5`
    ///    比 `1.20.1-47.1.5` 干净（游戏版本已经显示在别处了）。
    /// </summary>
    public static List<LoaderVersion> ParseNeoForge(JsonNode? data)
        => RowsFrom(data, o =>
        {
            var version = Str(o, "version");
            return string.IsNullOrEmpty(version)
                ? null
                : new LoaderVersion(Version: version, Stable: true);
        });

    /// <summary>
    /// Fabric / Quilt 的 `/v2|v3/versions/loader/&lt;mc&gt;` → 统一行。
    ///
    /// 两家形状一样：`[{"loader": {"version": "0.19.5", "stable": true}, ...}]`。
    /// ⚠️ `stable` **要留下来**：界面上"Forge 那种不分稳定/测试"和"Fabric 分"
    ///    是不同信息，丢了用户就不知道哪个是稳定版。
    /// </summary>
    public static List<LoaderVersion> ParseFabric(JsonNode? data)
        => RowsFrom(data, o =>
        {
            var loader = o["loader"] as JsonObject;
            var version = loader is null ? null : Str(loader, "version");
            if (string.IsNullOrEmpty(version))
            {
                return null;
            }
            var stable = loader?["stable"] is JsonValue v && v.GetValueKind() == JsonValueKind.True;
            return new LoaderVersion(Version: version, Stable: stable);
        });

    /// <summary>
    /// OptiFine 里**不算正式发布**的类型 —— 这些列在列表里会把"最新版"顶掉，
    /// 而用户要装的多半是正式版（实测 1.21.1：不滤的话 latest 变成
    /// `preview_OptiFine_1.21.1_HD_U_J1_pre15`）。
    /// </summary>
    private static readonly string[] OptifinePreviewTypes = { "preview", "beta", "alpha", "snapshot" };

    /// <summary>
    /// BMCLAPI `/optifine/&lt;mc&gt;` → 统一行。
    ///
    /// 字段：`{"filename": "OptiFine_1.20.1_HD_U_I5.jar", "type": "HD_U",
    ///        "patch": "I5", "forge": "Forge 47.0.35", "mcversion": "1.20.1"}`。
    ///
    /// 版本号从 **filename** 里切（`OptiFine_1.20.1_HD_U_I5.jar` → `HD_U_I5`）：
    ///   1. 去掉 `.jar`
    ///   2. 去掉 `OptiFine_&lt;mcversion&gt;_` 前缀
    ///   3. **再取最后两段**（下划线分段）→ `HD_U_I5`
    /// 第 3 步不能省：预览版的文件名是
    /// `preview_OptiFine_1.20.1_HD_U_I6_pre6.jar`，只做前两步会得到
    /// `OptiFine_1.20.1_HD_U_I6_pre6` 这种怪东西（踩过）。
    /// </summary>
    public static List<LoaderVersion> ParseOptiFine(JsonNode? data)
    {
        var rows = new List<(LoaderVersion Row, int[] Sort)>();
        if (data is not JsonArray array)
        {
            return new List<LoaderVersion>();
        }

        foreach (var item in array)
        {
            if (item is not JsonObject o)
            {
                continue;
            }

            var type = (Str(o, "type") ?? string.Empty).ToLowerInvariant();
            var filename = Str(o, "filename") ?? string.Empty;
            if (OptifinePreviewTypes.Contains(type)
                || filename.ToLowerInvariant().StartsWith("preview_", StringComparison.Ordinal))
            {
                continue;
            }

            var name = filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                ? filename[..^4]
                : filename;
            var mc = Str(o, "mcversion") ?? string.Empty;
            var prefix = $"optifine_{mc.ToLowerInvariant()}_";
            if (mc.Length > 0 && name.ToLowerInvariant().StartsWith(prefix, StringComparison.Ordinal))
            {
                name = name[prefix.Length..];
            }

            var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
            var version = parts.Length >= 2
                ? string.Join("_", parts[^2..])
                : (parts.Length == 1 ? parts[0] : string.Empty);
            if (version.Length == 0)
            {
                version = Str(o, "patch") ?? string.Empty;
            }
            if (version.Length == 0)
            {
                continue;
            }

            rows.Add((new LoaderVersion(
                Version: version,
                Stable: true,
                FileName: filename,
                Forge: Str(o, "forge") ?? string.Empty,
                // ⚠️ type / patch 要**带出去**：OptiFine 的安装器下载地址是
                //    `/optifine/<mc>/<type>/<patch>`，光有拼出来的 version
                //    反推不出这两段（预览版那种更是拼不回来）。
                Type: type,
                Patch: Str(o, "patch") ?? string.Empty), VersionKey(version)));
        }

        rows.Sort((x, y) => CompareVersionKeys(y.Sort, x.Sort));
        return rows.Select(r => r.Row).ToList();
    }

    // ---------- 各加载器的接口地址 ----------

    /// <summary>这个加载器在哪个地址（都实测过，见类注释的表）。</summary>
    public static string UrlFor(string key, string mcVersion)
        => key switch
        {
            "forge" => $"{Bmclapi}/forge/minecraft/{mcVersion}",
            "neoforge" => $"{Bmclapi}/neoforge/list/{mcVersion}",
            "fabric" => $"{FabricMeta}/v2/versions/loader/{mcVersion}",
            // Legacy Fabric 是 Fabric 的「老版本分支」，meta 结构一模一样
            // （实测 `/v2/versions/loader/1.12.2` 返回 75 条，字段 loader.version /
            //  loader.stable / intermediary 全在）—— 解析器直接复用 ParseFabric。
            "legacy_fabric" => $"{LegacyFabricMeta}/v2/versions/loader/{mcVersion}",
            "quilt" => $"{QuiltMeta}/v3/versions/loader/{mcVersion}",
            "optifine" => $"{Bmclapi}/optifine/{mcVersion}",
            _ => throw new KeyNotFoundException(key),
        };

    /// <summary>
    /// 拉某一家的版本列表（**会联网**）；解析成统一的行。
    ///
    /// 拉不到会抛异常（由 <see cref="LoaderRowsAsync"/> 接住并标成 Error）——
    /// 这样"网络抖动"和"我们还没做"在界面上是两种说法。
    ///
    /// ⚠️ **404 / 400 不算错误，算"这个游戏版本没有它"**：
    ///    · Fabric / Quilt 对不支持的 MC 版本直接回 404（1.12.2 就是）；
    ///    · Legacy Fabric 对 1.20.1 这种"太新"的版本回 **400**（不是 404）。
    ///    当成错误处理的话界面会说"取不到版本列表（网络或接口问题）"，
    ///    用户会以为是网络问题一直重试 —— 而事实是"这一版确实没有"。
    /// </summary>
    public static async Task<List<LoaderVersion>> LoaderVersionsAsync(
        string key, string mcVersion, CancellationToken token = default)
    {
        // 这两家不是"按 MC 版本查 JSON"：Cleanroom 是 maven-metadata.xml（XML），
        // LiteLoader 是**一个文件管所有版本**（要从里面挑这个 MC 版本那一段）。
        // 单独走，别塞进下面那张 parser 表（那张表的输入都是 JSON）。
        if (key == "cleanroom")
        {
            if (!CleanroomCatalog.Supports(mcVersion))
            {
                return new List<LoaderVersion>();   // 只有 1.12.2 有，别的版本就是"没有可用的"
            }
            var xml = await GetTextOrEmptyAsync(CleanroomCatalog.MetadataUrl, token).ConfigureAwait(false);
            return xml.Length == 0 ? new List<LoaderVersion>() : CleanroomCatalog.ParseVersions(xml);
        }

        if (key == "liteloader")
        {
            var bytes = await GetBytesOrEmptyAsync(LiteLoaderCatalog.VersionsUrl, token).ConfigureAwait(false);
            return bytes.Length == 0
                ? new List<LoaderVersion>()
                : LiteLoaderCatalog.ParseVersions(JsonIO.ParseBytes(bytes), mcVersion);
        }

        var parser = key switch
        {
            "forge" => (Func<JsonNode?, List<LoaderVersion>>)ParseForge,
            "neoforge" => ParseNeoForge,
            "fabric" or "quilt" or "legacy_fabric" => ParseFabric,
            "optifine" => ParseOptiFine,
            // 开发期错误（往 LoaderOrder 加了 key 但没写 parser），
            // 会被 LoaderRowsAsync 接住变成那条 Error
            _ => throw new KeyNotFoundException($"no parser for loader: {key}"),
        };

        var body = await GetBytesOrEmptyAsync(UrlFor(key, mcVersion), token).ConfigureAwait(false);
        return body.Length == 0 ? new List<LoaderVersion>() : parser(JsonIO.ParseBytes(body));
    }

    // ---------- 汇总成"第二页那几行" ----------

    /// <summary>
    /// 取一份二进制；**404 / 400 / 204 当成"没有"返回空数组**（不抛）。
    ///
    /// 为什么只有这三个状态码：它们是"这个地址没有这个版本"的语言
    /// （Fabric/Quilt 用 404、Legacy Fabric 用 400、有些 CDN 用 204）。
    /// 别的错（超时、403、5xx）照旧抛 —— 那些才是真出事了。
    /// </summary>
    private static async Task<byte[]> GetBytesOrEmptyAsync(string url, CancellationToken token)
    {
        try
        {
            return await Http.GetByteArrayAsync(url, token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound
                                                 or HttpStatusCode.BadRequest
                                                 or HttpStatusCode.NoContent)
        {
            return Array.Empty<byte>();
        }
    }

    private static async Task<string> GetTextOrEmptyAsync(string url, CancellationToken token)
    {
        try
        {
            return await Http.GetStringAsync(url, token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound
                                                 or HttpStatusCode.BadRequest
                                                 or HttpStatusCode.NoContent)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 第二页要显示的行，顺序照 <see cref="LoaderOrder"/>。
    ///
    /// - `State == Ok` → Count 是版本数，Latest 是最新的那个版本号。
    ///   ⚠️ `Count == 0` 是**正常情况**（这个游戏版本确实还没出这个加载器），
    ///   界面显示成「这个版本没有可用的 X」，不是错误、也不该说"还没做"。
    /// - `State == NotImplemented` → 界面显示"还没做"
    /// - `State == Error` → 界面显示"拉不到"，带 error 文本；**别写成"还没做"**
    ///
    /// ⚠️ 每一家单独 try：一家的接口挂了不能让其它四家都不显示。
    /// </summary>
    public static async Task<List<LoaderRow>> LoaderRowsAsync(
        string mcVersion, CancellationToken token = default)
    {
        var rows = new List<LoaderRow>();
        foreach (var key in LoaderOrder)
        {
            var name = LoaderNames.TryGetValue(key, out var n) ? n : key;
            var row = new LoaderRow(key, name, LoaderState.NotImplemented, 0,
                string.Empty, Array.Empty<LoaderVersion>(), string.Empty);

            if (Supported.Contains(key))
            {
                try
                {
                    var versions = await LoaderVersionsAsync(key, mcVersion, token).ConfigureAwait(false);
                    row = row with
                    {
                        State = LoaderState.Ok,
                        Versions = versions,
                        Count = versions.Count,
                        Latest = versions.Count > 0 ? versions[0].Version : string.Empty,
                    };
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                              or JsonException or KeyNotFoundException
                                              or NotSupportedException or InvalidOperationException)
                {
                    row = row with
                    {
                        State = LoaderState.Error,
                        Error = $"{ex.GetType().Name}: {ex.Message}",
                    };
                }
            }
            rows.Add(row);
        }
        return rows;
    }

    // ---------- 版本文件夹名 ----------

    /// <summary>
    /// 各加载器在文件夹名里**怎么拼**（照 PCL 的约定，实测过的目录名）：
    ///
    ///     1.20.4-Fabric 0.19.5          ← Fabric 用**空格**
    ///     1.12.2-Forge_14.23.5.2864     ← Forge 用**下划线**
    ///     1.20.1-OptiFine_HD_U_I5       ← OptiFine 自己装出来就是**下划线**
    ///     NeoForge 跟着 Forge 用下划线（它是 Forge 的分叉，PCL 也那么拼）
    ///     Cleanroom 也是下划线（同样是 Forge 的分叉）
    ///     LiteLoader 跟着它自己的加载器名用下划线
    ///
    /// ⚠️ 用下划线还有个实在好处：**跟安装器自己起的名字一致就不用改名了**
    ///    （OptiFine 那条路上，"改名"还牵扯到目录里那个 23 MB 的补丁 jar）。
    /// </summary>
    private static readonly string[] SeparatorUnderscore =
        { "forge", "neoforge", "optifine", "cleanroom", "liteloader", "labymod" };

    /// <summary>文件夹名里用的名字（不要首字母大写化：那会把 NeoForge 变成 Neoforge）。</summary>
    public static readonly IReadOnlyDictionary<string, string> FolderNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["forge"] = "Forge",
            ["neoforge"] = "NeoForge",
            ["fabric"] = "Fabric",
            // ⚠️ 名字里**有空格**，而 key 里是下划线：`ParseVersionFolderName` 比的是
            //    这个名字不是 key（以前比 key，`1.12.2-Legacy Fabric …` 会解析不出加载器）
            ["legacy_fabric"] = "Legacy Fabric",
            ["quilt"] = "Quilt",
            ["optifine"] = "OptiFine",
            ["cleanroom"] = "Cleanroom",
            ["liteloader"] = "LiteLoader",
            // LabyMod 的安装还没接（接口要 key、安装器是 GUI），但**名字得先有**：
            // 表里既然列了它，`VersionFolderName` 就不能退化成 `labymod` 这种 key 拼法。
            ["labymod"] = "LabyMod",
        };

    /// <summary>
    /// 按 PCL 的约定拼版本文件夹名：`26.3-Fabric 0.19.5` / `1.12.2-Forge_14.23.5.2864`。
    ///
    /// ⚠️ **这个名字不只是好看**：`&lt;游戏目录&gt;/versions/&lt;名字&gt;/` 就是安装落点，
    ///    而且 <see cref="ParseVersionFolderName"/> 靠它反查"这个版本的 mod 该装到哪"。
    ///    所以格式必须跟那边的规则对得上（比的是"`-` 后面以加载器名开头"）。
    /// </summary>
    public static string VersionFolderName(string mcVersion, string? loaderKey, string? loaderVersion)
    {
        var mc = (mcVersion ?? string.Empty).Trim();
        var key = (loaderKey ?? string.Empty).Trim().ToLowerInvariant();
        var lv = (loaderVersion ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            return mc;
        }

        var name = FolderNames.TryGetValue(key, out var n) ? n : key;
        if (lv.Length == 0)
        {
            return mc.Length > 0 ? $"{mc}-{name}" : name;
        }

        var sep = SeparatorUnderscore.Contains(key) ? "_" : " ";
        return mc.Length > 0 ? $"{mc}-{name}{sep}{lv}" : $"{name}{sep}{lv}";
    }

    /// <summary>
    /// `26.3-Fabric 0.19.5` → `("26.3", "fabric", "0.19.5")`。
    ///
    /// 反着解一份，给"从已有版本目录猜它是什么加载器"用。
    /// 认不出来时加载器和版本号给空串 —— **不要**瞎猜（猜错比空着糟）。
    /// </summary>
    public static (string Minecraft, string Loader, string LoaderVersion) ParseVersionFolderName(string? folder)
    {
        var text = (folder ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        var dash = text.IndexOf('-');
        if (dash < 0)
        {
            return (text, string.Empty, string.Empty);   // 纯版本号 = 原版
        }

        var head = text[..dash];
        var tail = text[(dash + 1)..];
        var low = tail.ToLowerInvariant();

        // ⚠️ 这里比的是**文件夹名**（`FolderNames` 的值），不是 key：
        //    `legacy_fabric` 在目录里写的是 `Legacy Fabric`（有空格），
        //    拿 key 去 StartsWith 永远匹配不上。
        //    长的名字得先试（`Legacy Fabric` 不能被 `Fabric` 抢走 —— 现在两者首字母
        //    不同所以不冲突，但按长度降序就不会以后再踩）。
        foreach (var (key, name) in FolderNames.OrderByDescending(p => p.Value.Length))
        {
            var prefix = name.ToLowerInvariant();
            if (low.StartsWith(prefix, StringComparison.Ordinal))
            {
                // 名字后面跟空格 / 下划线 / **连字符**，再后面是版本号
                // ⚠️ 连字符那种是 **Forge / NeoForge 安装器自己起的名字**
                //    （`1.20.1-forge-47.4.10`）。少了这一条，反查出来的版本号会是
                //    `-47.4.10` —— 而"本地有没有装过这个加载器版本"正靠它比。
                var rest = tail[prefix.Length..].TrimStart(' ', '_', '-');
                return (head, key, rest);
            }
        }
        return (head, string.Empty, tail);
    }
}
