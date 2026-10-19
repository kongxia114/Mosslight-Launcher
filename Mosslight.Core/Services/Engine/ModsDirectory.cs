using System.Text.RegularExpressions;

namespace Mosslight.Core.Services.Engine;

// ============================================================================
//  「这个版本的 mod 该装到哪」以及"从 Modrinth 搜索进来时怎么反查版本文件夹"
//  （移植自 1.0.0 的 core/mods.py 后半段）
//
//  规则是照 PCL2 / HMCL 的实际文件夹命名来的：
//
//      versions/1.20.4/                        原版：就一个游戏版本号
//      versions/1.20.4-Fabric 0.19.5/          Fabric：`<游戏版本>-Fabric <loader 版本>`
//      versions/1.12.2-Forge_14.23.5.2864/     Forge：**下划线**，不是空格
//
//  ⚠️ **为什么需要反查**：Modrinth 只知道"1.20.4 + fabric"，而磁盘上的文件夹叫
//     `1.20.4-Fabric 0.19.5` —— 光有游戏版本拼不出目录名（加载器版本号只有
//     磁盘上那份才知道）。所以必须**列目录 + 按名字匹配**。
//
//  ⚠️ **绝不能写成 `tail.lower().startswith(loader)` 或 `loader in name.lower()`**：
//     找 forge 时前者对 `neoforge_47.1.106` 是 False（看着能用），但后者是
//     **True** —— 于是找 forge 会直接命中 NeoForge 的文件夹，"mod 装上了但
//     游戏里没有"就是这么来的。必须按加载器名**整体**比对。
// ============================================================================

/// <summary>版本的 mods 目录解析。</summary>
public static class ModsDirectory
{
    /// <summary>视作"原版"的加载器写法（Modrinth 给的可能只是空串）。</summary>
    public static readonly string[] Vanilla =
    {
        "", "vanilla", "原版", "none", "全部",
    };

    private static readonly Regex DigitsRe = new(@"\d+", RegexOptions.Compiled);

    // ---------- 直接用扫描结果 ----------

    /// <summary>
    /// 该把 mod 装进哪个 `mods\` 目录（**已经有扫描结果时用这个**）。
    ///
    /// **默认版本隔离**：<see cref="VersionScanner"/> 扫版本时已经判过这件事了 ——
    /// 版本文件夹里出现 `mods` / `saves` / `options.txt` 之类特征就认为
    /// `game_dir` 就是这个版本文件夹（PCL / HMCL 的版本隔离），
    /// 否则 `game_dir` 就是共用的 `.minecraft`。
    ///
    /// 所以这里**不自己猜**、也不重新扫盘：直接把 `GameVersion.GameDir`
    /// 接过来用，保证"启动器认为的目录"和"启动时用的目录"始终是同一个
    /// （自己再判一次迟早会跟扫描器判得不一样，那种 bug 表现为
    /// "mod 装上了但游戏里没有"）。
    /// </summary>
    public static string ResolveModsDir(string mcDir, GameVersion? versionInfo)
    {
        var gameDir = versionInfo?.GameDir;
        return !string.IsNullOrEmpty(gameDir)
            ? Path.Combine(gameDir, "mods")
            : Path.Combine(mcDir, "mods");
    }

    // ---------- 按"游戏版本 + 加载器"反查版本文件夹 ----------

    /// <summary>
    /// `versions/` 下所有**像个版本**的文件夹名。
    ///
    /// 判据是"文件夹里有同名的 .json" —— PCL2 / HMCL / 官方启动器都是这个约定。
    /// 不这么卡的话，`versions/` 里的缓存、备份、natives 目录也会被当成版本。
    ///
    /// ⚠️ 用安全探测：真实环境里确实有读不了的目录（ACL 被改坏、被安全软件
    ///    锁住、解压中断），一个坏目录不能拖垮整次枚举。
    /// </summary>
    public static List<string> ListVersionDirs(string mcDir)
    {
        var root = Path.Combine(mcDir, "versions");
        var result = new List<string>();

        List<string> entries;
        try
        {
            if (!Directory.Exists(root))
            {
                return result;
            }
            entries = Directory.EnumerateDirectories(root).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        foreach (var entry in entries)
        {
            try
            {
                var name = Path.GetFileName(entry);
                if (File.Exists(Path.Combine(entry, name + ".json")))
                {
                    result.Add(name);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;   // 单个坏目录不能拖垮整次枚举
            }
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>`1.20.4-Fabric 0.19.5` → `("1.20.4", "Fabric 0.19.5")`；没有 `-` 的就是纯原版，尾巴给空串。</summary>
    public static (string Head, string Tail) SplitVersionFolder(string? name)
    {
        var text = name ?? string.Empty;
        var dash = text.IndexOf('-');
        return dash < 0 ? (text, string.Empty) : (text[..dash], text[(dash + 1)..]);
    }

    /// <summary>
    /// 从 `-` 后面那段里抠出**加载器名**：`NeoForge_47.1.106` → `neoforge`。
    ///
    /// 分隔符是空格或下划线 —— PCL2 两种都写（`Fabric 0.19.5` / `Forge_14.23.5.2864`）。
    /// 返回小写；空串就是没有加载器。
    ///
    /// `1.20.4-1.20.4-forge-51.0.0` 这类（Forge 官方安装器生成的中间目录）也能抠出来。
    /// </summary>
    public static string LoaderToken(string? tail)
    {
        var token = (tail ?? string.Empty).Trim();
        if (token.Length == 0)
        {
            return string.Empty;
        }
        // 空格和下划线都是分隔符；`.` 不算（版本号里有），但要拦住
        // "forge-51.0.0" 这种又连着加载器版本的情况
        foreach (var sep in new[] { ' ', '_', '-' })
        {
            var at = token.IndexOf(sep);
            if (at >= 0)
            {
                token = token[..at];
            }
        }
        return token.Trim().ToLowerInvariant();
    }

    /// <summary>从 `Fabric 0.19.5` 里抠出版本号数组，用来比大小。</summary>
    public static int[] Digits(string? text)
        => DigitsRe.Matches(text ?? string.Empty)
            .Select(m => int.TryParse(m.Value, out var n) ? n : 0)
            .ToArray();

    /// <summary>
    /// 这个文件夹名里的加载器**是不是** <paramref name="loader"/>。
    ///
    /// 先看 `-` 后面那段的加载器名（`NeoForge_47.1.106` → neoforge），
    /// 对不上再在整串里找 `legacy-fabric` 这种**带前缀的写法**
    /// （Legacy Fabric 的文件夹就长这样）。
    /// </summary>
    public static bool IsLoaderOf(string name, string loader)
    {
        var token = LoaderToken(SplitVersionFolder(name).Tail);
        if (token == loader)
        {
            return true;
        }
        var low = (name ?? string.Empty).ToLowerInvariant();
        return low.Contains("legacy-" + loader, StringComparison.Ordinal)
               || low.Contains("legacy_" + loader, StringComparison.Ordinal);
    }

    /// <summary>匹配的结果。</summary>
    public sealed record VersionMatch(string? Folder, string Why);

    /// <summary>
    /// 在 `versions/` 里找最合适的版本文件夹。
    ///
    /// 返回 `(文件夹名 或 null, 说明)`。说明是给人看的 ——
    /// "为什么选了这个"经常要回头看（比如同一个游戏版本装了两份 Fabric）。
    ///
    /// 匹配规则（从紧到松）：
    ///   1. 要原版：名字**就是**游戏版本，或者 `-` 后面是空的
    ///   2. 要加载器：`-` 前面 == 游戏版本，且 `-` 后面的**加载器名整体**
    ///      等于它（`Fabric 0.19.5`、`Forge_14.23.5.2864` 两种写法都能中，
    ///      而找 forge **不会**命中 NeoForge）
    ///   3. 兜底：名字里同时含游戏版本和加载器名（宽松匹配，说明里会标出来）
    /// </summary>
    public static VersionMatch MatchVersionDir(string mcDir, string? gameVersion, string? loader = "")
    {
        var names = ListVersionDirs(mcDir);
        if (names.Count == 0)
        {
            return new VersionMatch(null, Localization.Localizer.Tr("versions 目录里没有找到任何版本"));
        }
        if (string.IsNullOrEmpty(gameVersion))
        {
            return new VersionMatch(null, Localization.Localizer.Tr("不知道游戏版本"));
        }

        var want = (loader ?? string.Empty).Trim();
        var low = want.ToLowerInvariant();
        var wantLoader = !Vanilla.Contains(low);

        var exact = new List<(int[] Sort, string Name)>();
        var loose = new List<string>();

        foreach (var name in names)
        {
            var (head, tail) = SplitVersionFolder(name);

            if (!wantLoader)
            {
                if (name == gameVersion || (head == gameVersion && tail.Length == 0))
                {
                    exact.Add((Array.Empty<int>(), name));
                }
                continue;
            }

            if (head == gameVersion && LoaderToken(tail) == low)
            {
                // 同一个游戏版本 + 加载器可能装了好几份（加载器版本不同）
                // → 取版本号最高的
                exact.Add((Digits(tail), name));
            }
            else if (name.Contains(gameVersion, StringComparison.Ordinal) && IsLoaderOf(name, low))
            {
                loose.Add(name);
            }
        }

        if (exact.Count > 0)
        {
            // 版本号高的排前面
            exact.Sort((a, b) => CompareDigitKeys(b.Sort, a.Sort));
            return new VersionMatch(exact[0].Name,
                Localization.Localizer.Tr("匹配 {version} + {loader}",
                    ("version", gameVersion), ("loader", want)));
        }
        if (loose.Count > 0)
        {
            return new VersionMatch(loose[0], Localization.Localizer.Tr(
                "按名字宽松匹配到 {name}（自己核对一下）", ("name", loose[0])));
        }
        return new VersionMatch(null, Localization.Localizer.Tr(
            "没找到 {version} 的 {loader} 版本文件夹",
            ("version", gameVersion), ("loader", want.Length > 0 ? want : Localization.Localizer.Tr("原版"))));
    }

    private static int CompareDigitKeys(int[] a, int[] b)
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

    /// <summary>
    /// **从 Modrinth 搜索进来时该用这个**：按游戏版本 + 加载器算 `mods\` 目录。
    ///
    /// 优先顺序：
    ///   1. 有 <paramref name="versionInfo"/>（扫描器扫出来的那份）→ 用它算好的
    ///      `GameDir`（最准）
    ///   2. 没有 → 按游戏版本 + 加载器去 `versions/` 里反查文件夹名
    ///      （版本隔离：`versions/1.20.4-Fabric 0.19.5/mods`）
    ///   3. 还是没匹配到 → 退回 `<mc_dir>/mods`
    ///
    /// ⚠️ 第 3 条不是错误路径，是**正常会走到**的：用户搜了个 1.21.4 的 mod，
    ///    但本地只装了 1.20.1 —— 这时候装进共用的 `mods/` 并让界面提示
    ///    "没有匹配的版本文件夹"，比硬造一个目录名强。
    /// </summary>
    public static string ModsDirFor(
        string mcDir, string? gameVersion = "", string? loader = "", GameVersion? versionInfo = null)
    {
        if (versionInfo is not null)
        {
            return ResolveModsDir(mcDir, versionInfo);
        }
        if (string.IsNullOrEmpty(gameVersion))
        {
            return Path.Combine(mcDir, "mods");
        }

        var (name, _) = MatchVersionDir(mcDir, gameVersion, loader);
        return !string.IsNullOrEmpty(name)
            ? Path.Combine(mcDir, "versions", name, "mods")
            : Path.Combine(mcDir, "mods");
    }

    // ---------- 按项目类型算落点（2026-10：资源包 / 光影包接上 Modrinth） ----------

    /// <summary>
    /// 项目类型 → 游戏目录下那个固定的子目录名。
    ///
    /// ⚠️ **数据包不在这张表里，也不能在**：数据包要放进**某一个存档**的
    ///    `saves/&lt;存档&gt;/datapacks/`，压根没有"游戏目录下的固定落点"。
    ///    硬给它一个名字（比如 `<i>&lt;游戏目录&gt;/datapacks</i>`）比不做还糟：
    ///    界面会显示一句"下载会落到 …"，而游戏**从来不读**那个目录，
    ///    用户以为装上了、进游戏却找不到 —— 这正是最难查的一类 bug。
    ///    所以查不到就是 null，让界面去说"还没接"。
    ///
    /// ⚠️ 整合包也不在表里：它走**另一个服务**（<c>IModpackService</c>），
    ///    落点是 `versions/&lt;实例名&gt;/` 那个独立实例目录，不是游戏目录下的子目录。
    /// </summary>
    private static readonly Dictionary<string, string> ContentFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mod"] = "mods",
        ["resourcepack"] = "resourcepacks",
        ["shader"] = "shaderpacks",
    };

    /// <summary>这个项目类型该落到哪个子目录名；**没有固定落点**（数据包 / 认不出来的类型）返回 null。</summary>
    public static string? FolderFor(string? projectType)
        => ContentFolders.TryGetValue((projectType ?? string.Empty).Trim(), out var folder) ? folder : null;

    /// <summary>
    /// 这个项目类型该装到哪个目录（**从 Modrinth 搜索进来时用这个**）。
    ///
    /// 版本隔离的判断**完全复用 <see cref="ModsDirFor"/>**：先让它按老规则算出
    /// `…/mods`，再把最后那一段换成这个类型的目录名（`resourcepacks` / `shaderpacks`）。
    /// 为什么不自己再写一遍"隔离还是不隔离"：那套规则（扫描器的 `GameDir`、
    /// `versions/` 反查、找不到就退回共用目录）跟启动时用的是同一份，
    /// 抄一遍迟早会分叉 —— 分叉的表现就是"资源包装上了但游戏里选不到"。
    ///
    /// 返回 null = 这个类型没有游戏目录下的固定落点（见 <see cref="FolderFor"/>）。
    /// </summary>
    public static string? ContentDirFor(
        string mcDir, string projectType = "mod", string? gameVersion = "", string? loader = "",
        GameVersion? versionInfo = null)
    {
        var folder = FolderFor(projectType);
        if (folder is null)
        {
            return null;
        }
        if (string.Equals(folder, "mods", StringComparison.Ordinal))
        {
            return ModsDirFor(mcDir, gameVersion, loader, versionInfo);
        }

        // ModsDirFor 的三条分支都以 "mods" 结尾，所以它的父目录就是"这个版本的游戏目录"
        var modsDir = ModsDirFor(mcDir, gameVersion, loader, versionInfo);
        var parent = Path.GetDirectoryName(modsDir);
        if (string.IsNullOrEmpty(parent))
        {
            parent = mcDir;     // mcDir 本身是空串时 GetDirectoryName 会给空串
        }
        return Path.Combine(parent, folder);
    }

    // ---------- 存档目录（数据包的落点在这些存档下面） ----------

    /// <summary>
    /// 存档目录（`saves`）。**跟 mods 同一个版本隔离规则** —— 隔离版本的存档在
    /// `versions/&lt;名&gt;/saves`，否则在 `&lt;游戏目录&gt;/saves`。
    ///
    /// 数据包的落点是 `<see cref="SavesDirFor"/>/&lt;存档&gt;/datapacks`，
    /// 所以这里必须跟启动时"游戏读哪个 saves"的判断一致，不然装上去游戏里看不到。
    /// </summary>
    public static string SavesDirFor(
        string mcDir, string? gameVersion = "", string? loader = "", GameVersion? versionInfo = null)
    {
        // ModsDirFor 的三条分支都以 "mods" 结尾，所以它的父目录就是"这个版本的游戏目录"
        var modsDir = ModsDirFor(mcDir, gameVersion, loader, versionInfo);
        var parent = Path.GetDirectoryName(modsDir);
        if (string.IsNullOrEmpty(parent))
        {
            parent = mcDir;     // mcDir 本身是空串时 GetDirectoryName 会给空串
        }
        return Path.Combine(parent, "saves");
    }

    /// <summary>
    /// 存档目录下有哪些存档（只看目录，按名字排序）。
    ///
    /// ⚠️ 会读磁盘，**别在界面线程上直接调**。
    /// 读不到（目录不存在 / 没权限）一律返回空表：界面上会显示"没有找到存档"。
    /// </summary>
    public static IReadOnlyList<string> ListWorlds(string savesDir)
    {
        try
        {
            if (!Directory.Exists(savesDir))
            {
                return Array.Empty<string>();
            }
            return Directory.GetDirectories(savesDir)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    // ---------- 本地 mod 的增删启停 ----------

    /// <summary>本地 `mods/` 里的一个 jar。</summary>
    public sealed record LocalMod(string FileName, string Path, long Size, bool Enabled)
    {
        /// <summary>禁用只是把后缀改成 `.disabled`（跟 PCL / HMCL 一个约定）。</summary>
        public string DisplayName => Enabled
            ? System.IO.Path.GetFileNameWithoutExtension(FileName)
            : System.IO.Path.GetFileNameWithoutExtension(
                FileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                    ? FileName[..^".disabled".Length]
                    : FileName);
    }

    private const string DisabledSuffix = ".disabled";

    /// <summary>
    /// 列出一个 `mods/` 目录里的 mod（**只看第一层**）。
    ///
    /// ⚠️ 只看第一层：`mods/` 下面还可能有 `1.20.1/` 这种按版本分的子目录，
    ///    递归会把它们也算进来（PCL 也只数第一层）。
    /// </summary>
    public static List<LocalMod> ListMods(string modsDir)
    {
        var result = new List<LocalMod>();
        try
        {
            if (!Directory.Exists(modsDir))
            {
                return result;
            }

            foreach (var path in Directory.EnumerateFiles(modsDir))
            {
                var name = Path.GetFileName(path);
                var isDisabled = name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);
                var effective = isDisabled ? name[..^DisabledSuffix.Length] : name;
                if (!effective.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                long size;
                try
                {
                    size = new FileInfo(path).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    size = 0;
                }

                result.Add(new LocalMod(name, path, size, !isDisabled));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不了就当空目录 —— 这只是个列表
        }

        result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>
    /// 启用 / 禁用一个 mod（改后缀）。返回新的路径；失败返回 null。
    ///
    /// ⚠️ 用**改名**而不是移进子目录：`mods/` 下面的子目录会被一些加载器
    ///    当成"按版本分的 mod 目录"去扫描，行为不可控。`.disabled` 后缀是
    ///    PCL / HMCL 的约定，加载器不认这个后缀，天然就忽略它。
    /// </summary>
    public static string? SetEnabled(string path, bool enabled)
    {
        try
        {
            var isDisabled = path.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);
            if (enabled && isDisabled)
            {
                var target = path[..^DisabledSuffix.Length];
                File.Move(path, target, overwrite: false);
                return target;
            }
            if (!enabled && !isDisabled)
            {
                var target = path + DisabledSuffix;
                File.Move(path, target, overwrite: false);
                return target;
            }
            return path;    // 已经是想要的状态
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 删掉一个 mod。**真的删掉了才返回 true**。
    ///
    /// ⚠️ 必须先判存在：.NET 的 `File.Delete` 对不存在的文件**不抛异常**
    ///    （Python 的 `Path.unlink()` 会抛 `FileNotFoundError`）。
    ///    不判的话，"删一个已经被外部删掉的 mod"会report 成功 ——
    ///    布尔值就失去意义了，界面也会显示一句假的"已删除"。
    /// </summary>
    public static bool Delete(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
