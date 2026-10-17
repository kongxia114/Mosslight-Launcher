using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace Mosslight.Core.Services.Engine;

// ============================================================================
//  整合包（`.mrpack`）安装
//  （移植自 1.0.0 的 core/modpack.py）
//
//  ## 路径安全是这一层最要紧的事
//
//  整合包是**用户从网上下回来的 ZIP**，里面 `files[].path` 和 `overrides/`
//  的条目名**完全由打包的人说了算**。一个 `../../../Windows/System32/x.dll`
//  或者 `C:\autoexec.bat` 就能把文件写到实例目录外面去 —— 这是典型的
//  Zip Slip。所以：
//
//    · <see cref="IsSafeRelPath"/> 挡绝对路径 / 盘符 / `..` / NUL
//    · <see cref="JoinUnder"/> 拼完**再比一次**绝对路径前缀（双保险：
//      写文件的位置不能只靠一处判断）
//
//  ## 允许外面套一层文件夹
//
//  `modrinth.index.json` 可能在根上，也可能在 `<某个文件夹>/` 底下
//  （用户自己重打包、或者解压之后再压一遍就会这样）。PCL 也是这么认的
//  （`archiveBaseFolder`），只认根目录的话这类包会直接报「这不是整合包」。
//
//  ## 覆盖层的顺序有意义
//
//  `overrides/` → `client-overrides/`，**后面的盖前面的**。
//  所以 <see cref="FindOverrides"/> 返回的是**列表且顺序固定**，
//  调用方按顺序展开就行，不要自己去排序 —— 排序会打乱覆盖优先级。
// ============================================================================

/// <summary>整合包坏了 / 格式不认 / 不安全 —— 消息可以直接给用户看。</summary>
public sealed class ModpackException : Exception
{
    public ModpackException(string message) : base(message)
    {
    }
}

/// <summary>`files[]` 里的一条：要下的一个文件。</summary>
public sealed record ModpackFile(
    /// <summary>相对实例目录（`mods/xxx.jar`）。</summary>
    string Path = "",
    string Sha1 = "",
    string Sha512 = "",
    long Size = 0,
    string Url = "",
    IReadOnlyList<string>? Urls = null,
    string Client = ModpackInstaller.EnvRequired,
    string Server = ModpackInstaller.EnvRequired)
{
    /// <summary>
    /// 客户端要不要装这个文件。
    ///
    /// `unsupported` = 明确"服务端专用"，客户端不该装
    /// （规格里写得很清楚：`server` 指**专用服务端**）。
    /// `optional` 我们**当成要装** —— 整合包作者标 optional 通常是
    /// "可选但推荐"，没装可能少功能；真要给用户勾选是后续的事。
    /// </summary>
    public bool WantedOnClient => Client != ModpackInstaller.EnvUnsupported;
}

/// <summary>一个整合包的安装计划。</summary>
public sealed class ModpackPlan
{
    public string Name { get; set; } = string.Empty;

    public string VersionId { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public int FormatVersion { get; set; }

    /// <summary>依赖：`{"minecraft": "1.21.1", "fabric-loader": "0.19.5"}`。</summary>
    public Dictionary<string, string> Dependencies { get; } = new(StringComparer.Ordinal);

    /// <summary>**客户端该装的那些**（已按 env 过滤）。</summary>
    public List<ModpackFile> Files { get; } = new();

    /// <summary>被 env 过滤掉的（服务端专用），留个计数给界面说明。</summary>
    public int Skipped { get; set; }

    /// <summary>覆盖层：`[(zip 里的前缀, 实例目录下的相对路径), ...]` 按应用顺序。</summary>
    public List<(string Prefix, string Relative)> Overrides { get; set; } = new();

    public List<string> Warnings { get; } = new();

    private (string Loader, string Version)? _loader;

    /// <summary>要装哪个原版版本。</summary>
    public string Minecraft => Dependencies.TryGetValue(ModpackInstaller.GameKey, out var v) ? v : string.Empty;

    /// <summary>
    /// `(加载器 key, 版本号)`；没有加载器时 `("", "")`。
    ///
    /// ⚠️ 一个整合包**正常只有一个加载器**（Forge 和 Fabric 不会同时出现）。
    ///    真的出现多个时取第一个并记一条 warning —— 静默挑一个更难查。
    ///
    /// ⚠️ 结果**缓存**：Python 那边每次调都往 warnings 里塞一条，
    ///    而 `modpack_meta()` 和 `version_json()` 都会调到它 ——
    ///    多调几次就会看到同一条警告重复三遍。这里算一次就记住。
    /// </summary>
    public (string Loader, string Version) Loader
    {
        get
        {
            if (_loader is { } cached)
            {
                return cached;
            }

            var found = Dependencies
                .Where(kv => ModpackInstaller.DependencyLoaders.ContainsKey(kv.Key) && kv.Value.Length > 0)
                .Select(kv => (Loader: ModpackInstaller.DependencyLoaders[kv.Key], Version: kv.Value))
                .ToList();

            if (found.Count == 0)
            {
                _loader = ("", "");
                return _loader.Value;
            }
            if (found.Count > 1)
            {
                Warnings.Add(Localization.Localizer.Tr(
                    "这个整合包声明了多个加载器，只装第一个：{names}",
                    ("names", string.Join(", ", found.Select(f => f.Loader)))));
            }
            _loader = found[0];
            return _loader.Value;
        }
    }

    public long TotalBytes => Files.Sum(f => f.Size);

    /// <summary>版本 JSON 里我们自己那段 `modpack` 扩展字段。</summary>
    public JsonObject ModpackMeta(int overridesWritten = 0)
    {
        var (loaderKey, loaderVersion) = Loader;
        return new JsonObject
        {
            ["name"] = Name,
            ["versionId"] = VersionId,
            ["minecraft"] = Minecraft,
            ["loader"] = loaderKey,
            ["loaderVersion"] = loaderVersion,
            ["summary"] = Summary,
            ["overridesWritten"] = overridesWritten,
        };
    }

    /// <summary>
    /// 实例的版本 JSON：**只写 `inheritsFrom`**，主类 / 库 / jar 全从父版本继承。
    ///
    /// 这是 PCL2 的做法，也是唯一不会出错的：整合包自己**没有**客户端 jar，
    /// 客户端 jar 在"加载器版本 → 原版"那条链上（`versions/&lt;原版&gt;/&lt;原版&gt;.jar`）。
    /// 以前是把加载器直接装进实例目录，然后往这份 JSON 里写 `mainClass: ""` ——
    /// 结果启动器在 `versions/&lt;实例名&gt;/&lt;实例名&gt;.jar` 找不到 jar，
    /// 报「找不到客户端 jar」（用户 2026-10-18 报的就是这个）。
    ///
    /// `inheritsFrom` 指加载器版本时，合并出来的链是：
    ///   实例 → `1.20.1-forge-47.4.10` → `1.20.1`（jar 在这一层）
    /// 所以 `<see cref="VersionJsonLoader"/>` 合完就有 mainClass、库和 jar。
    /// </summary>
    public JsonObject InstanceJson(string instanceName, string inheritsFrom, int overridesWritten = 0)
        => new()
        {
            ["id"] = instanceName,
            ["inheritsFrom"] = inheritsFrom,
            ["type"] = "release",
            ["time"] = "1970-01-01T00:00:00+00:00",
            ["releaseTime"] = "1970-01-01T00:00:00+00:00",
            ["modpack"] = ModpackMeta(overridesWritten),
        };

    /// <summary>
    /// **兜底**用的版本 JSON：只有整合包元数据，没有 mainClass / 库 / 继承。
    ///
    /// 什么时候用它：连一个能继承的父版本都没有（加载器装不上、原版又没装）——
    /// 那时实例里只有整合包的文件，`inheritsFrom` 无人可指，
    /// 只能"能列出来、点启动会缺 mainClass"。
    /// 正常情况下走 <see cref="InstanceJson"/>（加载器版本）或继承原版。
    ///
    /// ⚠️ 这份 JSON 是**如实反映现状**，不是 bug：装完界面会明确说
    ///    「加载器没装上，这个实例还不能直接启动」。
    /// </summary>
    public JsonObject VersionJson(string instanceName, int overridesWritten = 0)
        => new()
        {
            ["id"] = instanceName,
            ["type"] = "release",
            ["releaseTime"] = "1970-01-01T00:00:00+00:00",
            ["time"] = "1970-01-01T00:00:00+00:00",
            ["mainClass"] = "",
            ["libraries"] = new JsonArray(),
            ["modpack"] = ModpackMeta(overridesWritten),
        };

    /// <summary>变成下载引擎能直接吃的任务列表。</summary>
    public List<InstallTask> DownloadTasks(string instanceDir)
    {
        var result = new List<InstallTask>();
        foreach (var f in Files)
        {
            if (f.Url.Length == 0)
            {
                continue;       // BuildPlan 已经记过 warning
            }
            var name = f.Path.Contains('/') ? f.Path[(f.Path.LastIndexOf('/') + 1)..] : f.Path;
            result.Add(new InstallTask(
                Url: f.Url,
                Path: ModpackInstaller.JoinUnder(instanceDir, f.Path),
                Sha1: f.Sha1,
                Size: f.Size,
                Label: name.Length > 0 ? name : f.Path,
                Kind: Localization.Localizer.Tr("模组")));
        }
        return result;
    }
}

/// <summary>整合包的解析与展开。</summary>
public static class ModpackInstaller
{
    public const string IndexName = "modrinth.index.json";

    /// <summary>
    /// 覆盖层目录，**按这个顺序依次应用**（后面的盖前面的）。
    /// 我们是客户端，所以不含 server-overrides。
    /// </summary>
    public static readonly string[] OverrideDirs = { "overrides", "client-overrides" };

    public const string EnvRequired = "required";

    public const string EnvOptional = "optional";

    public const string EnvUnsupported = "unsupported";

    /// <summary>
    /// 依赖键 → 内部加载器 key。
    /// ⚠️ Fabric 在 mrpack 里叫 `fabric-loader`，别写成 `fabric`。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DependencyLoaders =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["forge"] = "forge",
            ["neoforge"] = "neoforge",
            ["fabric-loader"] = "fabric",
            ["quilt-loader"] = "quilt",
        };

    /// <summary>缺依赖键时用来判断"这个整合包要不要加载器"。</summary>
    public const string GameKey = "minecraft";

    /// <summary>支持到哪个 formatVersion（更高版本可能有我们没实现的字段）。</summary>
    public const int SupportedFormat = 1;

    /// <summary>单个 `.mrpack` 的大小上限（防呆：正常整合包几十到几百 MB）。</summary>
    public const long MaxMrpackBytes = 2L * 1024 * 1024 * 1024;    // 2 GB

    /// <summary>
    /// Windows 保留设备名（大小写不敏感）。
    ///
    /// 用 `CON` 当目录名会**直接建不出来** —— 而且报的错很难懂。
    /// </summary>
    private static readonly HashSet<string> ReservedNames = BuildReservedNames();

    private static HashSet<string> BuildReservedNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL" };
        for (var i = 1; i <= 9; i++)
        {
            set.Add($"COM{i}");
            set.Add($"LPT{i}");
        }
        return set;
    }

    /// <summary>
    /// 实例目录名长度上限。留足余量：`&lt;游戏目录&gt;/versions/&lt;名字&gt;/mods/&lt;很长的模组文件名&gt;`
    /// 整体有 MAX_PATH 限制，名字这层别吃掉太多。
    /// </summary>
    public const int MaxInstanceName = 64;

    // ============================================================
    //  名字 / 路径安全
    // ============================================================

    /// <summary>
    /// 把整合包名字变成一个**能当目录名**的名字。
    ///
    /// 真实整合包的名字里什么都有（实测 `Cobblemon Official Modpack [Fabric]`、
    /// `Better MC [FABRIC] - BMC2`、带 `/` 和 `:` 的也有），直接拿去建目录在
    /// Windows 上会失败（`&lt; &gt; : " / \ | ? *`）+ 结尾的点和空格也不合法。
    ///
    /// 做法：
    ///   · 非法字符换成 `-`
    ///   · 去掉控制字符
    ///   · 收掉连续空格 / 连字符、去掉首尾的点和空格
    ///   · 挡住 Windows 保留名（CON / PRN / AUX / NUL / COM1-9 / LPT1-9）
    ///   · 太长的截断
    ///
    /// 返回空串时用 <paramref name="fallback"/>，**保证返回的一定能当目录名**。
    /// </summary>
    public static string SanitizeInstanceName(string? name, string fallback = "modpack")
    {
        var text = (name ?? string.Empty).Trim();

        // 控制字符 + Windows 非法字符
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            sb.Append(ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
                      || ch < 32
                ? '-'
                : ch);
        }
        text = sb.ToString();

        // ⚠️ 只收**连续重复**的空白 / 连字符，别把单个 `-` 也吃掉 ——
        //    `Better MC [FABRIC] - BMC2` 里的 `- BMC2` 是名字的一部分，
        //    收掉就变成 `Better MC [FABRIC] BMC2`（用户看得出变了样）。
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s{2,}", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"-{2,}", "-");
        text = text.Trim(' ', '.', '-');

        if (text.Length == 0)
        {
            return fallback;
        }

        // 保留名（`CON.txt` 也不行，所以比前缀）
        var stem = text.Split('.')[0].ToUpperInvariant();
        if (ReservedNames.Contains(stem))
        {
            text = "_" + text;
        }

        return text.Length > MaxInstanceName ? text[..MaxInstanceName] : text;
    }

    /// <summary>
    /// 这个相对路径能不能安全地拼到实例目录下。
    ///
    /// 挡的是（规格里点名的几类）：
    ///   · 绝对路径（`/x`、`\x`）
    ///   · 盘符（`C:/x`、`C:\x`）
    ///   · 目录穿越（任何一段是 `..`）
    ///   · 空路径、含 NUL 的路径、`.` 段
    ///
    /// ⚠️ 按 `/` 切：zip 里的路径**永远是 `/` 分隔**
    ///    （Windows 上反斜杠也要当分隔符处理，所以先统一换成 `/`）。
    /// </summary>
    public static bool IsSafeRelPath(string? rel)
    {
        var text = (rel ?? string.Empty).Trim().Replace('\\', '/');
        if (text.Length == 0 || text.Contains('\0'))
        {
            return false;
        }
        if (text.StartsWith('/'))
        {
            return false;
        }
        // 盘符：`C:` / `c:` 开头
        if (text.Length >= 2 && text[1] == ':')
        {
            return false;
        }

        // ⚠️ 切段要照 **PurePosixPath.parts 的语义**来：`.` 段和空段（`//`）
        //    会被**规范化掉**，不是"不安全"。
        //    一开始按"split('/') 后见到 `.` 或空段就拒"写，结果 `a/./b`、
        //    `a//b`、`./a` 全被判成不安全 —— 而它们其实是正常的相对路径
        //    （规格里也允许）。金标准里那三条就是这么抓出来的。
        var parts = text.Split('/').Where(p => p.Length > 0 && p != ".").ToArray();
        if (parts.Length == 0)
        {
            return false;
        }
        foreach (var part in parts)
        {
            if (part == "..")
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 把相对路径拼到 root 下，并**再确认一次**结果没跑出 root。
    ///
    /// 双保险：<see cref="IsSafeRelPath"/> 已经挡了 `..`，这里再比一次绝对路径
    /// 前缀 —— 万一以后有人改了上面那个函数，这一层还能兜住
    /// （写文件的位置不能只靠一处判断）。
    /// </summary>
    public static string JoinUnder(string root, string rel)
    {
        if (!IsSafeRelPath(rel))
        {
            throw new ModpackException(Localization.Localizer.Tr(
                "整合包里的路径不安全，已中止：{path}", ("path", rel)));
        }

        var fullRoot = Path.GetFullPath(root);
        var target = Path.GetFullPath(Path.Combine(fullRoot,
            rel.Replace('/', Path.DirectorySeparatorChar)));

        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ModpackException(Localization.Localizer.Tr(
                "整合包里的路径不安全，已中止：{path}", ("path", rel)));
        }
        return target;
    }

    // ============================================================
    //  解析
    // ============================================================

    /// <summary>
    /// 找到 `modrinth.index.json` 并返回 `(条目名, 一级目录前缀)`。
    ///
    /// ⚠️ **允许外面套一层文件夹**：用户自己重打包、或者解压之后再压一遍就会
    ///    变成 `<某个文件夹>/modrinth.index.json`。前缀同时决定 `overrides/`
    ///    在哪儿，所以必须一路带下去。
    ///
    /// ⚠️ 只看**一级**：再深的嵌套不认 —— 那更像"压缩包里塞了别的东西"，
    ///    猜下去不如老实报错。多个候选时取名字最短的那个（最浅）。
    /// </summary>
    public static (string Entry, string Base) FindIndex(IReadOnlyList<string> names)
    {
        var candidates = new List<string>();
        foreach (var name in names)
        {
            if (name == IndexName)
            {
                candidates.Add(string.Empty);
            }
            else if (name.EndsWith("/" + IndexName, StringComparison.Ordinal))
            {
                var head = name[..^IndexName.Length];
                if (head.Count(c => c == '/') == 1 && head.EndsWith('/'))
                {
                    candidates.Add(head);
                }
            }
        }

        if (candidates.Count == 0)
        {
            return (string.Empty, string.Empty);
        }
        var baseDir = candidates.OrderBy(c => c.Length).First();
        return (baseDir + IndexName, baseDir);
    }

    /// <summary>读出 `modrinth.index.json` 并做基本校验。</summary>
    private static JsonObject ReadIndex(IReadOnlyList<string> names, Func<string, byte[]> read)
    {
        var (entry, _) = FindIndex(names);
        if (entry.Length == 0)
        {
            throw new ModpackException(Localization.Localizer.Tr(
                "这不是整合包：ZIP 里没有 {name}", ("name", IndexName)));
        }

        byte[] raw;
        try
        {
            raw = read(entry);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new ModpackException(Localization.Localizer.Tr(
                "整合包读不了：{err}", ("err", ex.Message)));
        }

        JsonNode? parsed;
        try
        {
            // 带 BOM 的也认 —— 但**不是** JsonNode.Parse 自己会认，
            // 它内部的 Utf8JsonReader 明确不支持 BOM。走 JsonIO，它会把 BOM 跳掉。
            parsed = JsonIO.ParseBytes(raw);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
        {
            throw new ModpackException(Localization.Localizer.Tr(
                "{name} 不是合法的 JSON：{err}", ("name", IndexName), ("err", ex.Message)));
        }

        if (parsed is not JsonObject data)
        {
            throw new ModpackException(Localization.Localizer.Tr(
                "{name} 的内容不是对象", ("name", IndexName)));
        }

        var fmtNode = data["formatVersion"];
        int fmt;
        try
        {
            fmt = fmtNode switch
            {
                JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.Number
                    => v.GetValue<int>(),
                JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.String
                    => int.Parse(v.GetValue<string>()),
                _ => throw new FormatException("missing"),
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
        {
            throw new ModpackException(Localization.Localizer.Tr(
                "整合包没有 formatVersion（看不懂的格式）"));
        }

        if (fmt > SupportedFormat)
        {
            // 不硬拒：更高版本的已知字段还是能用的，只提醒一句
            if (data["_warn"] is not JsonArray warn)
            {
                warn = new JsonArray();
                data["_warn"] = warn;
            }
            warn.Add(Localization.Localizer.Tr(
                "整合包格式版本 {fmt} 比本启动器支持的（{max}）新，可能装不全",
                ("fmt", fmt), ("max", SupportedFormat)));
        }

        var game = data["game"]?.GetValue<string>() ?? "minecraft";
        if (game != "minecraft")
        {
            throw new ModpackException(Localization.Localizer.Tr(
                "这个整合包不是给 Minecraft 的：{game}", ("game", game)));
        }
        return data;
    }

    /// <summary>
    /// 把 `modrinth.index.json` 变成一个 <see cref="ModpackPlan"/>。
    ///
    /// <paramref name="zipNames"/> 传进来时顺带扫一遍覆盖层目录
    /// （不传就只出 files 那部分清单）。
    /// </summary>
    public static ModpackPlan BuildPlan(
        JsonObject index, IReadOnlyList<string>? zipNames = null, string baseDir = "")
    {
        var plan = new ModpackPlan
        {
            Name = index["name"]?.GetValue<string>() ?? string.Empty,
            VersionId = index["versionId"]?.GetValue<string>() ?? string.Empty,
            Summary = index["summary"]?.GetValue<string>() ?? string.Empty,
        };

        if (index["dependencies"] is JsonObject deps)
        {
            foreach (var (key, value) in deps)
            {
                var text = value?.GetValue<string>() ?? string.Empty;
                if (text.Length > 0)
                {
                    plan.Dependencies[key] = text;
                }
            }
        }

        plan.FormatVersion = index["formatVersion"] switch
        {
            JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.Number
                => v.GetValue<int>(),
            JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.String
                && int.TryParse(v.GetValue<string>(), out var n) => n,
            _ => 0,
        };

        if (index["_warn"] is JsonArray warn)
        {
            foreach (var w in warn)
            {
                if (w?.GetValue<string>() is { Length: > 0 } text)
                {
                    plan.Warnings.Add(text);
                }
            }
        }

        if (index["files"] is not JsonArray rawFiles)
        {
            throw new ModpackException(Localization.Localizer.Tr("整合包没有 files 列表"));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in rawFiles)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }
            var f = FileFromJson(obj);
            if (f.Path.Length == 0)
            {
                plan.Warnings.Add(Localization.Localizer.Tr("整合包里有一条没有 path 的文件，已跳过"));
                continue;
            }
            // 规格要求这里挡住；**不静默跳过** —— 这是安全事件，要能看见
            if (!IsSafeRelPath(f.Path))
            {
                plan.Warnings.Add(Localization.Localizer.Tr(
                    "整合包里有不安全的路径，已跳过：{path}", ("path", f.Path)));
                continue;
            }
            // 同一个落点两条 → 后下的覆盖先下的，结果不确定，直接跳过后面那条
            if (!seen.Add(f.Path))
            {
                plan.Warnings.Add(Localization.Localizer.Tr(
                    "整合包里有重复的落点，只装第一个：{path}", ("path", f.Path)));
                continue;
            }

            if (!f.WantedOnClient)
            {
                plan.Skipped++;
                continue;
            }
            if (f.Url.Length == 0)
            {
                plan.Warnings.Add(Localization.Localizer.Tr(
                    "这个文件没有可用的下载地址：{path}", ("path", f.Path)));
                continue;
            }
            plan.Files.Add(f);
        }

        if (plan.Minecraft.Length == 0)
        {
            plan.Warnings.Add(Localization.Localizer.Tr("整合包没说要用哪个 Minecraft 版本"));
        }

        if (zipNames is not null)
        {
            plan.Overrides = FindOverrides(zipNames, baseDir);
        }
        return plan;
    }

    private static ModpackFile FileFromJson(JsonObject data)
    {
        var hashes = data["hashes"] as JsonObject;
        var urls = (data["downloads"] as JsonArray ?? new JsonArray())
            .Select(x => x?.GetValue<string>() ?? string.Empty)
            .Where(u => u.Length > 0)
            .ToList();
        var env = data["env"] as JsonObject;

        return new ModpackFile(
            Path: data["path"]?.GetValue<string>() ?? string.Empty,
            // sha1 一律小写：引擎比对的是小写（这里统一，免得两处各转一次）
            Sha1: (hashes?["sha1"]?.GetValue<string>() ?? string.Empty).ToLowerInvariant(),
            Sha512: (hashes?["sha512"]?.GetValue<string>() ?? string.Empty).ToLowerInvariant(),
            Size: data["fileSize"] switch
            {
                JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.Number
                    => v.GetValue<long>(),
                JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.String
                    && long.TryParse(v.GetValue<string>(), out var n) => n,
                _ => 0,
            },
            Url: urls.Count > 0 ? urls[0] : string.Empty,
            Urls: urls,
            Client: env?["client"]?.GetValue<string>() ?? EnvRequired,
            Server: env?["server"]?.GetValue<string>() ?? EnvRequired);
    }

    /// <summary>
    /// 从 zip 条目名里找出覆盖层 → `[(前缀, 相对路径), ...]`。
    ///
    /// ⚠️ 返回值是**列表且按 <see cref="OverrideDirs"/> 的顺序**（后面的要盖前面的），
    ///    调用方按顺序展开就行，**不要自己去排序** —— 排序会打乱覆盖优先级。
    /// ⚠️ 不在这里判断"文件还是目录"，也不读内容：调用方直接按条目名展开。
    /// </summary>
    public static List<(string Prefix, string Relative)> FindOverrides(
        IReadOnlyList<string>? names, string baseDir = "")
    {
        var result = new List<(string, string)>();
        foreach (var prefix in OverrideDirs)
        {
            var head = baseDir + prefix + "/";
            foreach (var name in names ?? Array.Empty<string>())
            {
                if (!name.StartsWith(head, StringComparison.Ordinal))
                {
                    continue;
                }
                if (name.EndsWith('/'))
                {
                    continue;       // 目录条目，跳过
                }
                var rel = name[head.Length..];
                if (rel.Length == 0 || !IsSafeRelPath(rel))
                {
                    continue;
                }
                result.Add((baseDir + prefix, rel));
            }
        }
        return result;
    }

    // ============================================================
    //  从字节流读（`.mrpack` 就是一个 zip）
    // ============================================================

    /// <summary>从 `.mrpack` 的字节流里读出 index（不解压别的）。</summary>
    public static JsonObject LoadIndexFromBytes(byte[] blob)
    {
        GuardBlob(blob);
        try
        {
            using var stream = new MemoryStream(blob, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            return ReadIndex(names, entry =>
            {
                var e = zip.GetEntry(entry)
                    ?? throw new FileNotFoundException(entry);
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            });
        }
        catch (InvalidDataException ex)
        {
            // 常是下载到一半 / 下串了（HTML 错误页也会走到这）
            throw new ModpackException(Localization.Localizer.Tr(
                "整合包不是有效的 ZIP（下载坏了？）：{err}", ("err", ex.Message)));
        }
    }

    /// <summary>一行拿到完整计划（含覆盖层清单）。</summary>
    public static ModpackPlan PlanFromBytes(byte[] blob)
    {
        GuardBlob(blob);
        try
        {
            using var stream = new MemoryStream(blob, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            var index = ReadIndex(names, entry =>
            {
                var e = zip.GetEntry(entry) ?? throw new FileNotFoundException(entry);
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            });
            var (_, baseDir) = FindIndex(names);
            return BuildPlan(index, names, baseDir);
        }
        catch (InvalidDataException ex)
        {
            throw new ModpackException(Localization.Localizer.Tr(
                "整合包不是有效的 ZIP（下载坏了？）：{err}", ("err", ex.Message)));
        }
    }

    private static void GuardBlob(byte[]? blob)
    {
        if (blob is null || blob.Length == 0)
        {
            throw new ModpackException(Localization.Localizer.Tr("整合包是空的"));
        }
        // ⚠️ `Length` 是 int，而 MaxMrpackBytes 是 2 GB（超出 int 范围）——
        //    不转成 long 的话编译器会直接报"与整数常量比较无意义"
        if ((long)blob.Length > MaxMrpackBytes)
        {
            throw new ModpackException(Localization.Localizer.Tr(
                "这个整合包太大了（超过 {mb} MB），不敢装",
                ("mb", MaxMrpackBytes / 1048576)));
        }
    }

    // ============================================================
    //  覆盖层展开（纯文件操作，不联网）
    // ============================================================

    /// <summary>展开的结果。</summary>
    public sealed record OverrideResult(int Written, List<string> Errors);

    /// <summary>
    /// 把 `overrides/` + `client-overrides/` 展开到实例目录。
    ///
    /// ⚠️ **按 <see cref="ModpackPlan.Overrides"/> 的顺序依次写**
    ///    （后面的覆盖层要盖上前面的）—— 所以这里不重新扫 zip、不自己排序。
    /// ⚠️ 单个文件失败只记一笔、不影响其它（配置里有个坏文件不该让整包失败）。
    /// ⚠️ 写的时候**保留原有的修改时间**（zip 里有）—— 有些整合包靠时间戳判断
    ///    "这份配置是不是用户改过"，全刷成当前时间会破坏那个判断。
    /// </summary>
    public static OverrideResult ApplyOverrides(
        byte[] blob, ModpackPlan plan, string instanceDir, Action<int, int>? progress = null)
    {
        var written = 0;
        var errors = new List<string>();
        if (plan.Overrides.Count == 0)
        {
            return new OverrideResult(0, errors);
        }

        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(blob, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            return new OverrideResult(0, new List<string>
            {
                Localization.Localizer.Tr("整合包不是有效的 ZIP：{err}", ("err", ex.Message)),
            });
        }

        using (zip)
        {
            var total = plan.Overrides.Count;
            for (var i = 0; i < plan.Overrides.Count; i++)
            {
                var (prefix, rel) = plan.Overrides[i];
                string target;
                try
                {
                    target = JoinUnder(instanceDir, rel);
                }
                catch (ModpackException ex)
                {
                    errors.Add($"{rel}: {ex.Message}");
                    continue;
                }

                try
                {
                    // ⚠️ `prefix` 是**包的根**，目录层的情况可以是空串（覆盖包从 zip 根上取，
                    //    见 PackArchive）。空串时不能拼成 "/mods/x.jar" —— 那样永远找不到条目。
                    var entryName = prefix.Length == 0 ? rel : $"{prefix}/{rel}";
                    var entry = zip.GetEntry(entryName);
                    if (entry is null)
                    {
                        errors.Add($"{rel}: 条目不在包里");
                        continue;
                    }

                    byte[] data;
                    using (var s = entry.Open())
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        data = ms.ToArray();
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, data);

                    // 保留 zip 里的修改时间（别刷成"现在"）
                    try
                    {
                        File.SetLastWriteTime(target, entry.LastWriteTime.LocalDateTime);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                                  or ArgumentOutOfRangeException)
                    {
                        // 时间戳设不上不算失败 —— 文件本身已经写好了
                    }

                    written++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                              or InvalidDataException)
                {
                    errors.Add($"{rel}: {ex.Message}");
                }

                // 回调坏了不该中止安装
                try
                {
                    progress?.Invoke(i + 1, total);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                    // 忽略
                }
            }
        }

        return new OverrideResult(written, errors);
    }
}
