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

    /// <summary>设计说明见 docs/design-notes.md#mosslight-core-services-engine-loadercatalog-cs</summary>
    public const string FabricMeta = Bmclapi + "/fabric-meta";

    public const string QuiltMeta = "https://meta.quiltmc.org";

    /// <summary>设计说明见 docs/design-notes.md#mosslight-core-services-engine-loadercatalog-cs</summary>
    public const string LegacyFabricMeta = "https://meta.legacyfabric.net";

    /// <summary>设计说明见 docs/design-notes.md#mosslight-core-services-engine-loadercatalog-cs</summary>
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
        "optifine", "liteloader", "labymod",
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

    /// <summary>设计说明见 docs/design-notes.md#mosslight-core-services-engine-loadercatalog-cs</summary>
    private static readonly string[] OptifinePreviewTypes = { "preview", "beta", "alpha", "snapshot" };

    /// <summary>设计说明见 docs/design-notes.md#mosslight-core-services-engine-loadercatalog-cs</summary>
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
            // 设计说明见 docs/design-notes.md#mosslight-core-services-engine-loadercatalog-cs
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

        // LabyMod 也一样：**两份全局 manifest**（production / snapshot），不是按 MC 版本查。
        // 两份都拉、按游戏版本筛；production 在前（它才是"稳定版"）。
        // ⚠️ 拉不到某一份就当那份没有（别让整家变成"网络错误"）。
        if (key == "labymod")
        {
            var rows = new List<LoaderVersion>();
            foreach (var (channel, url) in new[]
                     {
                         (LabyModCatalog.ProductionChannel, LabyModCatalog.ProductionManifest),
                         (LabyModCatalog.SnapshotChannel, LabyModCatalog.SnapshotManifest),
                     })
            {
                var bytes = await GetBytesOrEmptyAsync(url, token).ConfigureAwait(false);
                if (bytes.Length == 0)
                {
                    continue;
                }
                rows.AddRange(LabyModCatalog.ParseManifest(JsonIO.ParseBytes(bytes), channel, mcVersion));
            }
            return rows;
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
    /// 所有加载器在文件夹名里都用**下划线**（照 PCL2 CE 的 `GetSelectName()`：
    /// 它拼出来的是 `26.3-Fabric_0.20.0` / `26.3-Quilt_0.31.0-beta.4` /
    /// `26.3-NeoForge_26.3.0.90` / `26.3-LabyMod_47.3_Production`）。
    ///
    /// ⚠️ 1.0.0 那份旧约定是 Fabric / Quilt 用**空格**（`1.20.4-Fabric 0.19.5`）。
    ///    现在跟着 PCL2 CE 改成下划线 —— 反解那边两种都认（见
    ///    <see cref="ParseVersionFolderName"/> 的 `TrimStart`），所以老目录照样读得出来。
    /// ⚠️ 用下划线还有个实在好处：**跟安装器自己起的名字一致就不用改名了**
    ///    （OptiFine 那条路上，"改名"还牵扯到目录里那个 23 MB 的补丁 jar）。
    /// </summary>
    public const char FolderNameSeparator = '_';

    /// <summary>文件夹名里用的名字（不要首字母大写化：那会把 NeoForge 变成 Neoforge）。</summary>
    public static readonly IReadOnlyDictionary<string, string> FolderNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["forge"] = "Forge",
            ["neoforge"] = "NeoForge",
            ["fabric"] = "Fabric",
            // ⚠️ PCL 拼的是 `16.3-LegacyFabric_0.16.10`（**没有空格**）：
            //    `ParseVersionFolderName` 比的就是这个名字，写成 "Legacy Fabric" 的话
            //    反解永远匹配不上（以前就是拿 key 去比，`legacy_fabric` 更匹配不上）
            ["legacy_fabric"] = "LegacyFabric",
            ["quilt"] = "Quilt",
            ["optifine"] = "OptiFine",
            ["cleanroom"] = "Cleanroom",
            ["liteloader"] = "LiteLoader",
            // LabyMod 的安装还没接，但**名字得先有**：
            // 表里既然列了它，`VersionFolderName` 就不能退化成 `labymod` 这种 key 拼法。
            ["labymod"] = "LabyMod",
        };

    /// <summary>
    /// 哪些加载器在文件夹名里**带版本号**。
    ///
    /// ⚠️ PCL 的 LiteLoader 那段是 `name += "-LiteLoader"` —— **不带版本号**
    ///    （LiteLoader 一个游戏版本只有一份正式版，没必要占位置）。
    ///    照抄的话 `<mc>-LiteLoader` 就是最终名字，反解回来版本号是空串。
    /// </summary>
    private static readonly string[] NoVersionInName = { "liteloader" };

    /// <summary>
    /// 按 PCL2 CE 的约定拼版本文件夹名：`26.3-Fabric_0.20.0` / `1.12.2-Forge_14.23.5.2864`。
    ///
    /// ⚠️ **这个名字不只是好看**：`&lt;游戏目录&gt;/versions/&lt;名字&gt;/` 就是安装落点，
    ///    而且 <see cref="ParseVersionFolderName"/> 靠它反查"这个版本的 mod 该装到哪"。
    ///    所以格式必须跟那边的规则对得上（比的是"`-` 后面以加载器名开头"）。
    /// ⚠️ 组合安装（Fabric + OptiFine 这种）的名字不是这个函数拼的，
    ///    见 <see cref="ComposeVersionName"/>。
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
        var segment = NoVersionInName.Contains(key) || lv.Length == 0
            ? name
            : $"{name}{FolderNameSeparator}{lv}";
        return mc.Length > 0 ? $"{mc}-{segment}" : segment;
    }

    /// <summary>
    /// 组合安装的版本名 —— 逐字照抄 PCL2 CE 的 `GetSelectName()`：
    ///
    ///     <原版>[-Fabric_<ver>][-LegacyFabric_<ver>][-LabyMod_<ver>_Production|_Snapshot]
    ///           [-Forge_<ver>][-NeoForge_<ver>][-Cleanroom_<ver>][-LiteLoader]
    ///           [-OptiFine_<ver>]
    ///
    /// 顺序是**固定的**（PCL 就这么拼的），所以同样一组选择总是同一个名字。
    /// ⚠️ LiteLoader 不带版本号（见 <see cref="NoVersionInName"/>）。
    /// ⚠️ OptiFine 的版本号在 PCL 那边还要去掉游戏版本前缀、空格换下划线
    ///    （`OptiFine_1.20.1_HD_U_I5` → `HD_U_I5`）—— 我们传进来之前就处理好了
    ///    （见 `LoaderCatalog.OptiFineNamePart`）。
    /// </summary>
    /// <param name="mcVersion">原版版本号（`26.3`）。</param>
    /// <param name="parts">(`加载器 key`, 版本号) 列表；版本号为空表示不带版本号。</param>
    public static string ComposeVersionName(string mcVersion, IEnumerable<(string Key, string Version)> parts)
    {
        var mc = (mcVersion ?? string.Empty).Trim();
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, version) in parts)
        {
            byKey[(key ?? string.Empty).Trim().ToLowerInvariant()] = (version ?? string.Empty).Trim();
        }

        var name = mc;
        foreach (var key in ComposeOrder)
        {
            if (!byKey.TryGetValue(key, out var version))
            {
                continue;
            }
            var display = FolderNames.TryGetValue(key, out var n) ? n : key;
            name += "-" + display;
            if (!NoVersionInName.Contains(key) && version.Length > 0)
            {
                name += FolderNameSeparator + version;
            }
        }
        return name;
    }

    /// <summary>
    /// 组合名字里的**拼接顺序**（照 PCL 的 `GetSelectName()`：Fabric/LegacyFabric/LabyMod
    /// 在前，Forge/NeoForge/Cleanroom/LiteLoader 居中，OptiFine 永远最后）。
    /// </summary>
    public static readonly string[] ComposeOrder =
    {
        "fabric", "legacy_fabric", "labymod", "forge", "neoforge", "cleanroom",
        "liteloader", "quilt", "optifine",
    };

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
