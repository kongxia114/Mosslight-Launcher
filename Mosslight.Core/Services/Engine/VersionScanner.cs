using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mosslight.Core.Localization;

namespace Mosslight.Core.Services.Engine;

// 设计说明见 docs/design-notes.md#mosslight-core-services-engine-versionscanner-cs

/// <summary>扫描出来的一个版本（比 <see cref="GameVersion"/> 多带一些扫描中间产物）。</summary>
public sealed record ScannedVersion(
    GameVersion Version,
    string? JsonPath,
    long ReleaseTimestamp,
    int KindRank,
    int TypeRank,
    bool HasLoader);

/// <summary>扫描本地已安装的 Minecraft 版本。</summary>
public sealed class VersionScanner
{
    /// <summary>
    /// 版本隔离的判定特征。这些文件/目录出现在版本文件夹里，说明玩家把这个版本
    /// 文件夹当成了独立的游戏目录（PCL / HMCL 的"版本隔离"）。
    /// </summary>
    private static readonly string[] IsolatedMarkers =
    {
        "saves", "options.txt", "servers.dat", "logs",
        "mods", "resourcepacks", "shaderpacks",
    };

    /// <summary>
    /// 加载器识别。顺序即优先级：neoforge 必须排在 forge 前面，否则会被 forge 抢先命中。
    /// 第三个字段是给界面显示的品牌名 —— 不要用首字母大写去凑，
    /// 那会把 NeoForge 变成 "Neoforge"、OptiFine 变成 "Optifine"。
    /// </summary>
    private static readonly (string Key, string Label, Regex Pattern)[] LoaderPatterns =
    {
        ("neoforge", "NeoForge", new Regex("neoforge", RegexOptions.Compiled)),
        ("forge", "Forge", new Regex("forge", RegexOptions.Compiled)),
        ("fabric", "Fabric", new Regex("fabric", RegexOptions.Compiled)),
        ("quilt", "Quilt", new Regex("quilt", RegexOptions.Compiled)),
        ("optifine", "OptiFine", new Regex("optifine", RegexOptions.Compiled)),
        ("labymod", "LabyMod", new Regex("labymod", RegexOptions.Compiled)),
        ("liteloader", "LiteLoader", new Regex("liteloader", RegexOptions.Compiled)),
    };

    private static readonly Dictionary<string, string> LoaderLabels =
        LoaderPatterns.ToDictionary(p => p.Key, p => p.Label, StringComparer.Ordinal);

    /// <summary>纯版本号文件夹名，如 1.20.1 / 1.7.10。</summary>
    private static readonly Regex PlainVersionRe = new(@"^\d+\.\d+(\.\d+)?$", RegexOptions.Compiled);

    /// <summary>从任意字符串里挑出第一个像版本号的东西（`fabric-loader-0.15.7-1.20.1` → `1.20.1`）。</summary>
    private static readonly Regex VersionTokenRe = new(@"\d+\.\d+(\.\d+)?", RegexOptions.Compiled);

    /// <summary>官方版本里"不算正式版"的类型，它们也归入第一段。</summary>
    private static readonly string[] NonReleaseOfficial = { "snapshot", "old_beta", "old_alpha" };

    /// <summary>分类优先级：官方版本 → 加载器版本 → 整合包 / 认不出来的。</summary>
    private static int KindRank(VersionKind kind) => kind switch
    {
        VersionKind.Vanilla => 0,
        VersionKind.Loader => 1,
        VersionKind.Pack => 2,
        _ => 9,
    };

    private static int TypeRank(string type) => type switch
    {
        "release" => 0,
        "snapshot" => 1,
        "old_beta" => 2,
        "old_alpha" => 3,
        _ => 9,
    };

    /// <summary>版本类型的显示名（每次调用都过 Tr()，所以跟着语言走）。</summary>
    public static string TypeLabel(string key) => Localizer.Tr(key switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "远古 Beta",
        "old_alpha" => "远古 Alpha",
        _ => "未知",
    });

    /// <summary>
    /// 依赖缓存标记（版本文件夹里的一个空文件）。
    ///
    /// 整合包要"全部装在一起、列表里就一条"：实例的 JSON 是压平的、客户端 jar 也拷进去了，
    /// 但装的时候还得先把原版和加载器版本装出来（Forge 的安装器就认那个目录）。
    /// 那两份**只是中间产物**，标上这个记号就不在版本列表里显示了 ——
    /// 留着不删是为了下次装同版本的包能直接复用（重跑一次 Forge 安装器要几分钟）。
    ///
    /// ⚠️ 判断只看**这个文件在不在**，不看内容：文件夹可能是别的启动器建的，
    ///    内容写坏了不该影响"要不要显示"。
    /// </summary>
    public const string CacheMarkerName = ".mosslight-cache";

    /// <summary>给这个版本文件夹打上"依赖缓存"记号（不在列表里显示）。</summary>
    public static void MarkAsCache(string versionDir)
    {
        try
        {
            Directory.CreateDirectory(versionDir);
            File.WriteAllText(Path.Combine(versionDir, CacheMarkerName), string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            // 标不上去只是"列表里多一条"，不该拦安装
        }
    }

    /// <summary>这个版本文件夹是不是"依赖缓存"。</summary>
    public static bool IsCache(string versionDir) => IsFile(Path.Combine(versionDir, CacheMarkerName));

    // ---------- 安全的文件系统探测 ----------
    // 存在的意义：绝不让"读不了某个文件"变成"整个扫描崩掉"。

    private static bool IsFile(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsDirectory(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Exists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>枚举目录的**安全版本**：读不了就当空目录。</summary>
    private static List<string> SafeList(string dir, string pattern = "*")
    {
        try
        {
            return Directory.Exists(dir)
                ? Directory.EnumerateFileSystemEntries(dir, pattern).ToList()
                : new List<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    // ---------- 目录 ----------

    /// <summary>
    /// 默认的 `.minecraft` 路径。
    ///
    /// 查找顺序：
    ///   1. 环境变量 `MCLUNCHER_MINECRAFT_DIR`（临时测试用）
    ///   2. 传进来的配置值（用户在设置页填的）
    ///   3. 各平台默认位置
    ///
    /// ⚠️ 这个顺序**只对"没显式给目录"的情况成立**。调用方明确传了目录
    ///    （<see cref="VersionScanner"/> 的构造函数、自检里的 fixture）就必须直接用 ——
    ///    1.0.0 那边是 `Path(mc_dir) if mc_dir else default_minecraft_dir()`，
    ///    环境变量只在"没给"的时候才轮得到。
    /// </summary>
    public static string DefaultMinecraftDir(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured!;
        }
        var env = Environment.GetEnvironmentVariable("MCLUNCHER_MINECRAFT_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }
        return OfficialMinecraftDir();
    }

    /// <summary>
    /// **官方启动器**自己会用的 `.minecraft` 位置（**不看配置**）。
    ///
    /// ⚠️ 跟 <see cref="DefaultMinecraftDir"/> 的区别很要紧：那个回答的是"现在该用哪个目录"
    /// （配置里写了就用配置的），这个回答的是"官方启动器会把存档放哪"。
    /// 拿这个去做「官方启动器文件夹」那一项 —— 用错的话，那一项会显示成
    /// 用户上次自己选的目录，标签和内容对不上。
    /// </summary>
    public static string OfficialMinecraftDir()
    {
        var env = Environment.GetEnvironmentVariable("MCLUNCHER_MINECRAFT_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, ".minecraft");
        }
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "minecraft");
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft");
    }

    /// <summary>猜几个可能放着 .minecraft 的目录（给 Java 扫描当 mc_dirs 用）。</summary>
    public static List<string> ScanMinecraftRoots()
    {
        var candidates = new List<string>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            candidates.Add(Path.Combine(appData, ".minecraft"));
        }
        return candidates.Where(IsDirectory).ToList();
    }

    // ---------- 扫描 ----------

    private readonly string _mcDir;
    private readonly string _versionsDir;

    /// <summary>扫描过程中跳过的坏版本，留给界面去提示，不往上抛异常。</summary>
    public List<string> Errors { get; private set; } = new();

    public string MinecraftDir => _mcDir;

    public VersionScanner(string? mcDir = null)
    {
        // 显式给的目录直接归一化，**不走 DefaultMinecraftDir 的环境变量分支** ——
        // 自检和"指定一个别的游戏目录"都靠这个语义
        _mcDir = string.IsNullOrWhiteSpace(mcDir)
            ? Path.GetFullPath(DefaultMinecraftDir())
            : Path.GetFullPath(mcDir!);
        _versionsDir = Path.Combine(_mcDir, "versions");
    }

    /// <summary>
    /// 返回所有本地版本，已排好序。
    /// 排序规则见 <see cref="Compare"/>：官方版本 → 加载器版本 → 整合包。
    /// 读不了的版本会被跳过并记进 <see cref="Errors"/>，不会中断扫描。
    /// </summary>
    public List<ScannedVersion> Scan()
    {
        Errors = new List<string>();

        if (!IsDirectory(_versionsDir))
        {
            Errors.Add(Localizer.Tr("版本目录不存在或无法访问: {path}", ("path", _versionsDir)));
            return new List<ScannedVersion>();
        }

        var entries = SafeList(_versionsDir);
        entries.Sort(StringComparer.OrdinalIgnoreCase);

        var versions = new List<ScannedVersion>();
        foreach (var entry in entries)
        {
            // 跳过 versions 底下的 .zip 之类
            if (!IsDirectory(entry))
            {
                continue;
            }

            ScannedVersion? info;
            try
            {
                info = ReadOne(entry);
            }
            catch (Exception ex)
            {
                // 兜底：任何一个版本出问题都不能拖垮整次扫描。
                // 没有这层，一个 ACL 坏掉的目录就能让整个启动器崩掉。
                Errors.Add(Localizer.Tr(
                    "{name}: 读取失败 ({kind}: {err})",
                    ("name", Path.GetFileName(entry)),
                    ("kind", ex.GetType().Name),
                    ("err", ex.Message)));
                continue;
            }

            if (info is not null)
            {
                versions.Add(info);
            }
        }

        MarkDuplicateIds(versions);
        versions.Sort(Compare);
        return versions;
    }

    /// <summary>按 id 或文件夹名找一个版本，给启动逻辑用。</summary>
    public ScannedVersion? Find(string versionId)
        => Scan().FirstOrDefault(v => v.Version.Id == versionId || v.Version.DirName == versionId);

    /// <summary>一行拿到版本列表，界面用这个就够了。需要读扫描告警时自己 new 一个 VersionScanner。</summary>
    public static List<GameVersion> ScanVersions(string? mcDir = null)
        => new VersionScanner(mcDir).Scan().Select(v => v.Version).ToList();

    // ---------- 内部实现 ----------

    private ScannedVersion? ReadOne(string dirPath)
    {
        // 依赖缓存（整合包装的时候顺带装的原版 / 加载器）不当成一个能玩的版本列出来。
        // ⚠️ 放在读 JSON 之前：这一步要先于一切，不然它会先被当成正常版本读完再丢掉。
        if (IsCache(dirPath))
        {
            return null;
        }

        var loaded = LoadJson(dirPath);
        if (loaded is null)
        {
            return null;
        }
        var (jsonPath, data) = loaded.Value;

        var dirName = Path.GetFileName(dirPath);
        var versionId = Str(data, "id") ?? dirName;
        var parentId = Str(data, "inheritsFrom");

        // 我们自己写进实例 JSON 的那段元数据（整合包装的）：原版版本号 / 加载器都在这儿。
        // ⚠️ 压平之后的实例**没有** inheritsFrom，光看名字认不出它是 1.20.4 + Fabric，
        //    所以版本号优先从这段拿。
        var modpack = data["modpack"] as JsonObject;
        var modpackMc = modpack is null ? null : Str(modpack, "minecraft");
        var modpackLoader = modpack is null ? null : Str(modpack, "loader");

        // ⚠️ 加载器要**分三处认**（按可靠性排）：
        //   ① `modpack.loader`：整合包实例，最准；
        //   ② **自己的名字**：判定分段（原版 / 加载器 / 整合包）只认这个 ——
        //      整合包实例叫 `Zombie Invade 100 Days`，拿父名字判的话它会被分到
        //      「加载器」那一段里（用户找不到它）；
        //   ③ 父版本名字（`inheritsFrom: 1.20.1-forge-47.4.10`）：给"手写 inheritsFrom"
        //      那种老实例兜底。
        var ownLoaders = DetectLoaders($"{versionId} {dirName}");
        var loaders = ownLoaders;
        if (modpackLoader is { Length: > 0 })
        {
            loaders = DetectLoaders(modpackLoader);
        }
        else if (loaders.Count == 0)
        {
            loaders = DetectLoaders(parentId ?? string.Empty);
        }

        var (gameDir, isolated) = DetectGameDir(dirPath);
        var jar = FindJar(dirPath, versionId);
        var inheritsFrom = parentId;
        var versionType = Str(data, "type") ?? "unknown";

        // 文件夹名通常比 json 里的 id 更有信息量（带加载器版本号、整合包名），
        // 两者不一样时优先显示文件夹名。
        var displayName = dirName != versionId ? dirName : versionId;

        var primaryLoader = loaders.Count > 0 ? loaders[0] : null;
        var loaderLabel = primaryLoader is not null && LoaderLabels.TryGetValue(primaryLoader, out var label)
            ? label
            : null;

        var javaMajor = ReadJavaMajor(data);
        var releaseTime = Str(data, "releaseTime");

        // ⚠️ MinecraftVersion 不能拿**文件夹名**兜底（issue #3）：
        //    `1.20.4-Fabric_0.15.11` 这种目录名会被下游当成"游戏版本"去请求 Modrinth，
        //    结果一条数据都搜不到（Modrinth 只认 `1.20.4`）。
        //    顺序固定为：压平的整合包元数据 → JSON 的 inheritsFrom（加载器版本靠它）→ JSON 的 id。
        //    文件夹名（dirName）只用来显示，**不参与**版本判定。
        var minecraftVersion = modpackMc is { Length: > 0 }
            ? modpackMc
            : GuessMinecraftVersion(inheritsFrom, versionId, versionId);

        var version = new GameVersion(
            Id: versionId,
            Name: displayName,
            MinecraftVersion: minecraftVersion,
            // ⚠️ Loader 是**显示名**（"Fabric" / "NeoForge"），不是小写 key ——
            //    它会被直接填进「版本信息」品牌串（`Mosslight/Fabric`）显示在游戏主界面上，
            //    小写的 `neoforge` 会很难看。小写 key 走 LoaderKey。
            Loader: loaderLabel ?? "Vanilla",
            LastPlayed: null,
            IconPath: null,
            Kind: modpack is not null
                // 带 `modpack` 元数据的**一定是**整合包实例（我们自己装的），
                // 不管它名字里有没有 "Fabric" —— 老编号那种 `1.20.4-Fabric_0.19.5`
                // 名字里带加载器名，按名字判会把它塞进「加载器」那一段里。
                ? VersionKind.Pack
                : Classify(dirName, ownLoaders, versionType),
            Type: versionType,
            DirName: dirName,
            Path: dirPath,
            JarPath: jar,
            InheritsFrom: inheritsFrom,
            // 模组版本没有自己的 jar 是正常的（jar 在父版本里），所以只要
            // "jar 在" 或 "能继承" 就算可启动。
            Complete: jar is not null || !string.IsNullOrEmpty(inheritsFrom),
            GameDir: gameDir,
            Isolated: isolated,
            JavaMajor: javaMajor,
            ReleaseTime: releaseTime,
            LoaderKey: primaryLoader,
            VersionJsonPath: jsonPath);

        return new ScannedVersion(
            version,
            jsonPath,
            ToTimestamp(releaseTime),
            KindRank(version.Kind),
            TypeRank(versionType),
            primaryLoader is not null);
    }

    /// <summary>
    /// 找出并读出该版本的 json。
    ///
    /// 优先 `&lt;文件夹名&gt;.json`；对不上时再看目录下其它的 `*.json`（整合包常被改名）。
    /// 三种失败要分清楚：
    ///   - 文件不存在         → 这本来就不是版本目录（native-libraries 之类），静默跳过
    ///   - 拒绝访问 / 读取失败 → 记进 errors，用户需要知道
    ///   - json 解析失败       → 记进 errors
    /// </summary>
    private (string Path, JsonObject Data)? LoadJson(string dirPath)
    {
        var dirName = Path.GetFileName(dirPath);
        var preferred = Path.Combine(dirPath, dirName + ".json");

        // ⚠️ 不能先 IsFile() 判断，那个调用本身就可能抛（ACL 坏掉时）。
        // 直接把 preferred 当候选，靠读文件时的异常类型来分流。
        var candidates = new List<string> { preferred };
        candidates.AddRange(SafeList(dirPath, "*.json")
            .Where(p => !string.Equals(p, preferred, StringComparison.OrdinalIgnoreCase)));

        foreach (var path in candidates)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (FileNotFoundException)
            {
                continue;   // 正常情况，不是版本目录
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Errors.Add(Localizer.Tr("{file}: 无法读取 ({err})",
                    ("file", $"{dirName}/{Path.GetFileName(path)}"), ("err", ex.Message)));
                continue;
            }

            JsonObject? data;
            try
            {
                data = JsonIO.ParseBytes(bytes) as JsonObject;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                Errors.Add(Localizer.Tr("{file}: json 格式错误 ({err})",
                    ("file", $"{dirName}/{Path.GetFileName(path)}"), ("err", ex.Message)));
                continue;
            }

            if (data is not null && !string.IsNullOrEmpty(Str(data, "id")))
            {
                return (path, data);
            }
            // 能读但不像版本 json（缺 id）→ 静默跳过，不打扰用户
        }

        return null;
    }

    /// <summary>
    /// 找客户端 jar。
    ///
    /// 原版是 `&lt;id&gt;.jar`。模组版本经常没有自己的 jar（真正的 jar 在父版本里），
    /// 这种情况下返回 null，让启动器顺着 inheritsFrom 往上找。
    /// </summary>
    private static string? FindJar(string dirPath, string versionId)
    {
        foreach (var name in new[] { versionId + ".jar", Path.GetFileName(dirPath) + ".jar" })
        {
            var candidate = Path.Combine(dirPath, name);
            if (IsFile(candidate))
            {
                return candidate;
            }
        }
        // 文件夹和 jar 都改过名的情况：目录下只有一个能用的 jar 就认它
        var jars = SafeList(dirPath, "*.jar").Where(IsFile).ToList();
        return jars.Count == 1 ? jars[0] : null;
    }

    /// <summary>
    /// 判断游戏目录。
    ///
    /// 版本隔离（PCL/HMCL）会把存档、配置直接放进版本文件夹。看到特征文件就
    /// 认为 game_dir = 版本文件夹，否则和 .minecraft 共用。
    /// 这是启发式判断，不是百分之百可靠。
    /// </summary>
    private (string GameDir, bool Isolated) DetectGameDir(string dirPath)
    {
        foreach (var marker in IsolatedMarkers)
        {
            if (Exists(Path.Combine(dirPath, marker)))
            {
                return (dirPath, true);
            }
        }

        // PCL2 的标记方式：`<版本>/PCL/Setup.ini` 里 `VersionArgumentIndie:1`。
        // 同一个 `.minecraft` 两边都用，所以这条也得认 —— 不然 PCL2 建的隔离版本
        // 我们这边会当成"共用 .minecraft"，存档 / mods 全指到别处去。
        if (IsPclIsolated(dirPath))
        {
            return (dirPath, true);
        }

        return (_mcDir, false);
    }

    /// <summary>
    /// 这个版本目录里 PCL 的 ini 有没有把"版本隔离"打开。
    ///
    /// 格式是 PCL 自己那套 `键:值`（不是标准 ini 的 `键=值`），见 PCL2 的
    /// `ModModpack.CopyOverrideDirectory`：它装整合包时会写
    /// `VersionArgumentIndie:1` 和 `VersionArgumentIndieV2:True`。
    /// </summary>
    private static bool IsPclIsolated(string dirPath)
    {
        try
        {
            var path = Path.Combine(dirPath, "PCL", "Setup.ini");
            if (!IsFile(path))
            {
                return false;
            }
            foreach (var line in File.ReadAllLines(path))
            {
                var text = line.Trim();
                if (!text.StartsWith("VersionArgumentIndie:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var value = text["VersionArgumentIndie:".Length..].Trim();
                return value is "1" or "true" or "True";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不了就当没开（不影响别的判断）
        }
        return false;
    }

    private static List<string> DetectLoaders(string text)
    {
        var low = text.ToLowerInvariant();
        return LoaderPatterns
            .Where(p => p.Pattern.IsMatch(low))
            .Select(p => p.Key)
            .ToList();
    }

    /// <summary>
    /// 把版本分成三类，决定列表里的分段。
    ///
    ///   vanilla: 官方版本（没加载器，文件夹名是纯版本号，或者是快照）
    ///   loader:  带加载器的版本（Forge / Fabric / NeoForge ...）
    ///   pack:    整合包，以及认不出来的东西
    /// </summary>
    private static VersionKind Classify(string dirName, List<string> loaders, string versionType)
    {
        if (loaders.Count > 0)
        {
            return VersionKind.Loader;
        }
        if (PlainVersionRe.IsMatch(dirName) || NonReleaseOfficial.Contains(versionType))
        {
            return VersionKind.Vanilla;
        }
        return VersionKind.Pack;
    }

    /// <summary>同一个 id 出现多次时，把文件夹名补进显示名，免得列表里两个一模一样。</summary>
    private static void MarkDuplicateIds(List<ScannedVersion> versions)
    {
        foreach (var group in versions.GroupBy(v => v.Version.Id, StringComparer.Ordinal))
        {
            if (group.Count() <= 1)
            {
                continue;
            }
            foreach (var info in group)
            {
                var idx = versions.IndexOf(info);
                versions[idx] = info with
                {
                    Version = info.Version with
                    {
                        Name = $"{info.Version.Id} | {info.Version.DirName}",
                    },
                };
            }
        }
    }

    /// <summary>
    /// 排序：官方版本 → 加载器版本 → 整合包；同段内正式版优先、新的排前面。
    ///
    /// 时间取负 = 新的排前面；解析不出时间的是 0，自然落到最后。
    /// </summary>
    private static int Compare(ScannedVersion a, ScannedVersion b)
    {
        var result = a.KindRank.CompareTo(b.KindRank);
        if (result != 0) return result;

        result = a.TypeRank.CompareTo(b.TypeRank);
        if (result != 0) return result;

        // 有加载器的排在没加载器的后面（同段内原版在前）
        result = a.HasLoader.CompareTo(b.HasLoader);
        if (result != 0) return result;

        result = b.ReleaseTimestamp.CompareTo(a.ReleaseTimestamp);
        if (result != 0) return result;

        return string.Compare(a.Version.Name, b.Version.Name, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- 小工具 ----------

    private static string? Str(JsonObject obj, string key)
        => obj[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

    private static int? ReadJavaMajor(JsonObject obj)
    {
        if (obj["javaVersion"] is not JsonObject javaVersion)
        {
            return null;
        }
        return javaVersion["majorVersion"] switch
        {
            JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.GetValue<int>(),
            JsonValue v when v.GetValueKind() == JsonValueKind.String
                && int.TryParse(v.GetValue<string>(), out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>设计说明见 docs/design-notes.md#mosslight-core-services-engine-versionscanner-cs</summary>
    private static long ToTimestamp(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }
        return DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed.ToUnixTimeSeconds()
            : 0;
    }

    /// <summary>
    /// 它基于哪个原版版本。
    ///
    /// 1.0.0 的扫描结果是**没有**这个字段的（那边界面直接用 loader_label 拼），
    /// 2.0 的启动页要显示它，所以在这边推一次：优先看 inheritsFrom（加载器版本的
    /// JSON 一定写着父版本），挑不出再从目录名 / id 里捞第一个像版本号的串。
    ///
    /// 挑不出来就退回 id 自己 —— 显示成"和 id 一样"比显示成空要好。
    /// </summary>
    private static string GuessMinecraftVersion(string? inheritsFrom, string dirName, string versionId)
    {
        foreach (var source in new[] { inheritsFrom, dirName, versionId })
        {
            if (string.IsNullOrEmpty(source))
            {
                continue;
            }
            if (PlainVersionRe.IsMatch(source))
            {
                return source;
            }
            var match = VersionTokenRe.Match(source);
            if (match.Success)
            {
                return match.Value;
            }
        }
        return versionId;
    }
}
