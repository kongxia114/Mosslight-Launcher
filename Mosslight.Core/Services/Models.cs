namespace Mosslight.Core.Services;

// 契约里用到了引擎层那几个**纯常量 / 纯模型**（Modrinth 的排序名、
// 本地 mod 的条目类型）。方向是 Services → Engine，不是反过来 ——
// Engine 里的东西只认 Models.cs，不认这层接口。
using Mosslight.Core.Services.Engine;

// ============================================================================
//  数据模型 + 服务契约
//
//  界面只认这里定义的接口，不认实现 —— 所以「原型假数据」换成真引擎时，
//  页面一行都不用改（只需要 `IsPrototype` 从 true 变成 false，界面上那句
//  「原型数据：启动引擎还没接」会自己消失）。
//
//  ⚠️ 下面这些 record 的**位置参数**是对外契约：`Stubs.cs` 和界面都在按
//  位置构造它们。加字段一律**追加在末尾并且给默认值**，不要插在中间 ——
//  插在中间会让所有调用点静默错位（编译器不一定报错，因为类型可能正好兼容）。
// ============================================================================

/// <summary>装好的一个游戏版本（启动页列表里的一行）。</summary>
/// <param name="Id">目录名，也是唯一标识："1.20.5"、"1.20.1-forge"。</param>
/// <param name="Name">界面上显示的名字（文件夹名通常比 json 里的 id 更有信息量）。</param>
/// <param name="MinecraftVersion">它基于哪个原版版本。</param>
/// <param name="Loader">Vanilla / Fabric / Forge / NeoForge / Quilt …</param>
/// <param name="LastPlayed">上次启动时间（1.0.0 的版本列表用它排序）。</param>
/// <param name="IconPath">自定义图标（版本设置里选的）。</param>
/// <param name="Kind">vanilla / loader / pack —— 决定列表分段，见 <see cref="VersionKind"/>。</param>
/// <param name="Type">json 里的 type：release / snapshot / old_beta / old_alpha / unknown。</param>
/// <param name="DirName">版本文件夹名。**可能和 <see cref="Id"/> 不一样**（以 json 的 id 为准，但显示用文件夹名）。</param>
/// <param name="Path">版本文件夹的完整路径。</param>
/// <param name="JarPath">客户端 jar；模组版本常常没有自己的 jar（真正那个在父版本目录里）→ null。</param>
/// <param name="InheritsFrom">父版本 id（Fabric / Forge / OptiFine 的 JSON 里写着）。</param>
/// <param name="Complete">能不能直接启动（自己目录里有 jar，或者能顺着 inheritsFrom 继承）。</param>
/// <param name="GameDir">这个版本的游戏目录（版本隔离时就是版本文件夹自己）。</param>
/// <param name="Isolated">是不是「版本隔离」（PCL / HMCL 那种把存档塞进版本文件夹的玩法）。</param>
/// <param name="JavaMajor">版本 JSON 里 `javaVersion.majorVersion`；老版本没有这个字段 → null。</param>
/// <param name="ReleaseTime">json 里的 releaseTime（ISO8601），排序用。</param>
/// <param name="LoaderKey">
/// 加载器的**原始 key**（`fabric` / `neoforge` / `optifine`，全是小写），没有加载器就是 null。
///
/// ⚠️ 和 <see cref="Loader"/> 是两样东西，别合并：
///    `Loader` 是给界面看、也用在「版本信息」品牌串里的**显示名**（`NeoForge`、`OptiFine`），
///    大小写是刻意的（`capitalize()` 会把它变成 `Neoforge`，那是错的）；
///    `LoaderKey` 是程序判断用的标识，必须全是小写。
/// </param>
public sealed record GameVersion(
    string Id,
    string Name,
    string MinecraftVersion,
    string Loader,
    DateTimeOffset? LastPlayed = null,
    string? IconPath = null,
    VersionKind Kind = VersionKind.Vanilla,
    string Type = "unknown",
    string DirName = "",
    string Path = "",
    string? JarPath = null,
    string? InheritsFrom = null,
    bool Complete = true,
    string GameDir = "",
    bool Isolated = false,
    int? JavaMajor = null,
    string? ReleaseTime = null,
    string? LoaderKey = null,
    string? VersionJsonPath = null);

/// <summary>版本分类。决定版本列表里的分段顺序：官方版本 → 加载器版本 → 整合包。</summary>
public enum VersionKind
{
    /// <summary>官方版本（没加载器，文件夹名是纯版本号，或者是快照）。</summary>
    Vanilla,

    /// <summary>带加载器的版本（Forge / Fabric / NeoForge …）。</summary>
    Loader,

    /// <summary>整合包，以及认不出来的东西。</summary>
    Pack,
}

/// <summary>一个被扫出来的 Java。</summary>
/// <param name="Path">java.exe 的完整路径。</param>
/// <param name="Version">`java -version` 报的版本串，如 "1.8.0_481" / "17.0.9"。</param>
/// <param name="Architecture">x86_64 / x86 / 空（读不出来）。</param>
/// <param name="IsDefault">是不是启动器当前选定的那个（界面会标「（默认）」）。</param>
/// <param name="Major">主版本号：8 / 17 / 21（挑 Java 就按它）。</param>
/// <param name="Vendor">厂商（`java -version` 里 Runtime Environment 那行）。</param>
/// <param name="Error">探测失败的原因；**非空 = 这个 Java 不能用**。</param>
public sealed record JavaRuntime(
    string Path,
    string Version,
    string Architecture,
    bool IsDefault = false,
    int Major = 0,
    string Vendor = "",
    string? Error = null)
{
    /// <summary>能不能用（探测没出错、而且读到了主版本号）。</summary>
    public bool Usable => string.IsNullOrEmpty(Error) && Major > 0;

    public bool Is64Bit => string.Equals(Architecture, "x86_64", StringComparison.OrdinalIgnoreCase);

    /// <summary>界面上显示的一行，例如 `JDK 17 · 64 位 · Oracle`。</summary>
    public string Label()
    {
        var kind = Path.Contains("jdk", StringComparison.OrdinalIgnoreCase) ? "JDK" : "JRE";
        var arch = Is64Bit
            ? Localization.Localizer.Tr("64 位")
            : (Architecture.Length > 0 ? Localization.Localizer.Tr("32 位") : Localization.Localizer.Tr("位数未知"));
        var vendor = Vendor.Length > 0 ? Vendor : Localization.Localizer.Tr("未知厂商");
        return $"{kind} {(Major > 0 ? Major.ToString() : "?")} · {arch} · {vendor}";
    }
}

/// <summary>账户类型。正版（微软登录）暂时做不了 —— 见文档里的说明。</summary>
public enum AccountKind
{
    Offline,

    /// <summary>微软正版。⚠️ 个人开发者拿不到 Xbox 授权，界面上要禁用。</summary>
    Microsoft,

    /// <summary>第三方皮肤站（authlib-injector）。</summary>
    ThirdParty,
}

public sealed record Account(
    string Name,
    AccountKind Kind,
    string Uuid = "",
    string? AvatarPath = null,
    DateTimeOffset? CreatedAt = null);

/// <summary>
/// 启动一次游戏要的东西。
///
/// ⚠️ 位置参数**只有前五个是老的**，后面都是接真引擎时追加的（带默认值）。
/// 界面按位置构造的那五个保持不变。
/// </summary>
public sealed record LaunchRequest(
    GameVersion Version,
    Account? Account,
    JavaRuntime? Java,
    int MaxMemoryMb,
    string MinecraftDir,
    int MinMemoryMb = 512,
    int Width = 854,
    int Height = 480,
    string ExtraJvmArgs = "",
    string ExtraGameArgs = "",
    string ServerAddress = "",
    /// <summary>自定义信息（版本设置里的）：覆盖 `${version_type}`。空 = 用品牌默认值。</summary>
    string CustomInfo = "",
    /// <summary>版本目录。空 = `<MinecraftDir>/versions/<Id>`（扫描器知道真实路径时该传进来）。</summary>
    string? VersionDir = null,
    string JavaPath = "",
    string AccessToken = "0",
    string UserType = "legacy");

public enum LaunchStage
{
    Preparing,
    CheckingFiles,
    Downloading,
    ExtractingNatives,
    Launching,
    Running,
    Failed,
    Done,
}

public sealed record LaunchProgress(
    LaunchStage Stage,
    string Message,
    double Fraction = 0.0,
    /// <summary>不确定进度（总大小未知）。界面该画成走马灯而不是 0%。</summary>
    bool Indeterminate = false);

public sealed record LaunchResult(
    bool Ok,
    string Message,
    int? ProcessId = null,
    /// <summary>拼好的命令行（给日志窗口用，已做路径缩短 + classpath 折叠）。</summary>
    string? CommandLine = null,
    /// <summary>本地缺的库（不拦启动，只提示）。</summary>
    IReadOnlyList<string>? MissingLibraries = null,
    /// <summary>启动过程中的告警（缺客户端 jar 之类）。</summary>
    IReadOnlyList<string>? Warnings = null);

/// <summary>版本列表。</summary>
public interface IGameService
{
    /// <summary>是不是**原型假数据**（真引擎还没接上）。界面要据此提示用户。</summary>
    bool IsPrototype { get; }

    IReadOnlyList<GameVersion> Installed { get; }

    /// <summary>重新扫一遍游戏目录（磁盘上的变化要手动触发才看得见）。</summary>
    Task<IReadOnlyList<GameVersion>> RefreshAsync(CancellationToken token = default);

    /// <summary>
    /// 扫描过程中跳过的坏版本（读不了 / json 坏了）。**不抛异常**，只记在这里 ——
    /// 1.0.0 的告警条（`_update_banner`）读的就是它。一个坏目录不该让整次扫描失败。
    /// </summary>
    IReadOnlyList<string> ScanErrors { get; }

    /// <summary>当前在用的 .minecraft 目录。</summary>
    string MinecraftDir { get; }
}

/// <summary>Java 探测。</summary>
public interface IJavaService
{
    bool IsPrototype { get; }

    IReadOnlyList<JavaRuntime> Detected { get; }

    /// <summary>给这个版本挑一个合适的 Java（挑不出来就 null，界面要能显示"未找到"）。</summary>
    JavaRuntime? PickFor(GameVersion version);

    Task<IReadOnlyList<JavaRuntime>> ScanAsync(CancellationToken token = default);

    /// <summary>这个版本**至少**要哪个 Java 主版本（读版本 JSON 的 javaVersion，读不到按 8 算）。</summary>
    int RequiredMajor(GameVersion version);
}

/// <summary>账户。1.0.0 只有离线账户能真正跑起来。</summary>
public interface IAccountService
{
    IReadOnlyList<Account> All { get; }

    /// <summary>当前选中的档案；null = 还没建（启动页要显示"未选择档案"）。</summary>
    Account? Current { get; }

    Account AddOffline(string name);

    void Select(Account? account);

    /// <summary>删掉一个档案（同名删）。删的是当前那个就顺延到剩下的第一个。</summary>
    void Remove(string name);

    /// <summary>重新从 accounts.json 读一遍。</summary>
    void Reload();
}

/// <summary>启动游戏。</summary>
public interface ILaunchService
{
    bool IsPrototype { get; }

    Task<LaunchResult> LaunchAsync(
        LaunchRequest request,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken token = default);

    /// <summary>正在跑的游戏进程；null = 没在跑。界面据此禁用启动按钮。</summary>
    bool IsRunning { get; }

    /// <summary>游戏退出时通知（参数：退出码）。界面用它恢复按钮、写日志。</summary>
    event EventHandler<int>? GameExited;

    /// <summary>游戏进程输出的一行（stdout + stderr 合流）。</summary>
    event EventHandler<string>? GameOutput;

    /// <summary>
    /// 把这次启动**最终会用的参数**算出来（合上「版本设置」那一层覆盖），
    /// 但**不起进程**。界面用它回填「版本设置」对话框、显示"实际会用多少内存"。
    /// </summary>
    LaunchRequest ResolveRequest(LaunchRequest request);

    /// <summary>把还在跑的游戏进程杀掉（用户点「停止游戏」时用）。</summary>
    void Stop();
}

/// <summary>版本 / 整合包下载。</summary>
public interface IDownloadService
{
    bool IsPrototype { get; }

    IReadOnlyList<string> AvailableMinecraftVersions { get; }

    /// <summary>重新从官方清单拉一遍可下载的版本列表。</summary>
    Task<IReadOnlyList<string>> RefreshVersionListAsync(CancellationToken token = default);

    /// <summary>装一个原版版本（客户端 jar + 全部库 + 资源）。</summary>
    Task<InstallResult> InstallVanillaAsync(
        string minecraftVersion,
        string minecraftDir,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken token = default);
}

/// <summary>一次安装的结果。</summary>
public sealed record InstallResult(
    bool Ok,
    string Message,
    string? VersionId = null,
    int FilesDownloaded = 0,
    int FilesFailed = 0,
    IReadOnlyList<string>? FailedFiles = null);

/// <summary>装一个模组加载器要的东西。</summary>
public sealed record LoaderInstallRequest(
    string MinecraftVersion,
    string LoaderKey,
    string LoaderVersion,
    string MinecraftDir,
    /// <summary>版本目录名。空 = 按 PCL 约定拼（`1.21.4-Fabric 0.19.5`）。</summary>
    string? VersionId = null,
    /// <summary>顺便把 Fabric API 装进 `mods/`（只有 Fabric 才有意义）。</summary>
    bool InstallFabricApi = false,
    /// <summary>OptiFine 专用：安装器地址要用的两段（版本列表里带着）。</summary>
    string? OptifineType = null,
    string? OptifinePatch = null);

/// <summary>装加载器的结果。</summary>
public sealed record LoaderInstallResult(
    bool Ok,
    string Message,
    string? VersionId = null,
    /// <summary>装载器自己要下的那些文件（原版那批不算）。</summary>
    InstallResult? Download = null,
    /// <summary>老实说清楚"这个我们还没做"，别假装能装。</summary>
    bool NotImplemented = false);

/// <summary>
/// 一行加载器版本。
///
/// 大部分字段是所有加载器共有的；后面几个是 OptiFine 专用的 ——
/// 它的安装器下载地址要靠 `type` / `patch` 两段拼，光有 version 反推不出来。
/// </summary>
public sealed record LoaderVersion(
    string Version,
    string Modified = "",
    bool Stable = true,
    int Build = 0,
    string? InstallerHash = null,
    string? FileName = null,
    string? Forge = null,
    string? Type = null,
    string? Patch = null);

/// <summary>加载器那一行的状态。三态，界面必须照实显示。</summary>
public enum LoaderState
{
    /// <summary>能列出并安装。</summary>
    Ok,

    /// <summary>我们还没做。</summary>
    NotImplemented,

    /// <summary>
    /// 有接口但这次拉不到（网络问题）。
    /// ⚠️ 这个**不能**说成"还没做"，否则用户以为功能不存在，
    ///    而其实只是网络抖了一下。
    /// </summary>
    Error,
}

/// <summary>下载页第二页要显示的一行。</summary>
public sealed record LoaderRow(
    string Key,
    string Name,
    LoaderState State,
    int Count,
    /// <summary>最新的那个版本号。⚠️ <see cref="Count"/> == 0 是**正常情况**
    /// （这个游戏版本确实还没出这个加载器），不是错误、也不该说"还没做"。</summary>
    string Latest,
    IReadOnlyList<LoaderVersion> Versions,
    string Error);

/// <summary>模组加载器：列版本 + 装。</summary>
public interface ILoaderService
{
    bool IsPrototype { get; }

    /// <summary>
    /// 这个游戏版本能装哪些加载器、各有哪些版本。
    ///
    /// ⚠️ 一家的接口挂了不能让其它四家都不显示 —— 每家单独标状态
    /// （<see cref="LoaderState.Ok"/> / <see cref="LoaderState.Error"/> /
    /// <see cref="LoaderState.NotImplemented"/>）。
    /// </summary>
    Task<IReadOnlyList<LoaderRow>> CatalogAsync(string minecraftVersion, CancellationToken token = default);

    /// <summary>
    /// 老实说这个加载器现在**能不能**由启动器自己装完。
    ///
    /// Fabric / Quilt 走 profile JSON，一个 GET 就够；
    /// Forge / NeoForge / OptiFine 要下安装器 jar 再跑一次 Java。
    /// 两种情况都实现了，这里返回的都是 true —— 留着是给界面判断
    /// "要不要先跟用户说一声这步要几分钟"。
    /// </summary>
    bool CanInstall(string loaderKey);

    Task<LoaderInstallResult> InstallAsync(
        LoaderInstallRequest request,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken token = default);
}

/// <summary>一个模组版本 → 装到哪个目录的结果。</summary>
public sealed record ModInstallResult(
    bool Ok,
    string Message,
    string? FilePath = null,
    InstallResult? Download = null);

/// <summary>
/// 模组管理与 Modrinth。
///
/// ⚠️ 搜索 / 项目 / 版本这几层返回的是 **Modrinth 的原始 JSON**
/// （<see cref="System.Text.Json.Nodes.JsonObject"/>），不是自定义模型 ——
/// 跟 1.0.0 的取舍一致：界面（卡片、详情页）全是照 Modrinth 的字段名写的
/// （`hits` / `slug` / `icon_url` / `game_versions` …），包一层自定义对象会让
/// 每个取值都要改一遍，收益却只是"类型好看"。防呆取值放在界面那一层。
/// </summary>
public interface IModService
{
    bool IsPrototype { get; }

    /// <summary>
    /// 搜索（返回原始的回包：`hits` / `total_hits` / `offset` / `limit`）。
    ///
    /// <paramref name="projectType"/> 就是 Modrinth 的 `project_type`
    /// （`mod` / `modpack` / `datapack` / `resourcepack` / `shader`）——
    /// **默认 `"mod"，不传就是老行为**。2026-10 起界面会拿它去搜资源包 / 光影包 /
    /// 整合包，所以它不再是"只有 mod"的隐含假设。
    ///
    /// ⚠️ `loader` 只对 mod / modpack 有意义：资源包和光影包没有加载器一说，
    ///    硬塞 `categories:fabric` 会把结果**几乎全筛掉而且不报错**
    ///    （实测资源包 36437 → 16）。引擎里已经拦了这层，
    ///    见 <see cref="ModrinthApi.SearchProjectsAsync"/>。
    /// </summary>
    Task<System.Text.Json.Nodes.JsonObject> SearchAsync(
        string query = "",
        string gameVersion = ModrinthApi.AnyOption,
        string loader = ModrinthApi.AnyOption,
        string category = ModrinthApi.AnyOption,
        string projectType = "mod",
        string sort = ModrinthApi.DefaultSort,
        int limit = 20,
        int offset = 0,
        CancellationToken token = default);

    /// <summary>项目的所有版本（新的在前）。筛游戏版本 / 加载器用 <see cref="FilterVersions"/>。</summary>
    Task<IReadOnlyList<System.Text.Json.Nodes.JsonObject>> ProjectVersionsAsync(
        string projectId, CancellationToken token = default);

    /// <summary>按游戏版本 + 加载器筛。</summary>
    IReadOnlyList<System.Text.Json.Nodes.JsonObject> FilterVersions(
        IEnumerable<System.Text.Json.Nodes.JsonObject> versions,
        string gameVersion = "", string loader = "");

    /// <summary>
    /// 这个版本的 mod 该装进哪个 `mods\` 目录。
    ///
    /// 给了 <paramref name="versionInfo"/>（扫描结果）就用它算好的 `GameDir`
    /// （最准）；没有就按游戏版本 + 加载器去 `versions/` 里反查。
    /// </summary>
    string ModsDirFor(string minecraftDir, string gameVersion, string loader,
        GameVersion? versionInfo = null);

    /// <summary>
    /// **这个项目类型**该装进哪个目录（资源包 → `resourcepacks\`、光影 → `shaderpacks\`）。
    ///
    /// 版本隔离的判断跟 <see cref="ModsDirFor"/> 是同一份（内部就是拿它算完再换最后一段），
    /// 所以"启动器认为的目录"和"启动时真正读的目录"不会分叉。
    ///
    /// ⚠️ 返回 **null** 表示这个类型没有游戏目录下的固定落点 —— 目前只有数据包
    ///    （要放进某一个存档的 `saves/&lt;存档&gt;/datapacks/`）。界面据此显示"还没接"，
    ///    **不要**退而求其次给个目录：那会让人以为装上了，游戏却根本不读。
    /// </summary>
    string? ContentDirFor(string minecraftDir, string projectType, string gameVersion, string loader,
        GameVersion? versionInfo = null);

    /// <summary>列出一个 `mods\` 目录里的 mod。</summary>
    IReadOnlyList<Engine.ModsDirectory.LocalMod> LocalMods(string modsDir);

    /// <summary>启用 / 禁用（改 `.disabled` 后缀）。返回新路径；失败返回 null。</summary>
    string? SetEnabled(string path, bool enabled);

    /// <summary>删掉一个 mod（真的删掉了才返回 true）。</summary>
    bool Delete(string path);

    /// <summary>
    /// 把一条 Modrinth 版本装进**这个项目类型该落的目录**。
    ///
    /// ⚠️ 目录是用 <see cref="ContentDirFor"/> 算的（mod 走 <see cref="ModsDirFor"/>）——
    ///    **版本隔离的版本装进版本目录**，否则装进共用的 `mods/`（资源包 / 光影同理，
    ///    只是最后那段目录名不同）。跟启动时用的是同一套判断，所以不会出现
    ///    "装上了但游戏里没有"。
    ///
    /// <paramref name="projectType"/> **默认 `"mod"` = 老行为**，老调用点一个字都不用改。
    /// 传了数据包（没有固定落点）会如实返回失败，不会硬塞进 mods/。
    /// </summary>
    Task<ModInstallResult> InstallAsync(
        System.Text.Json.Nodes.JsonObject version,
        string minecraftDir,
        string gameVersion,
        string loader,
        GameVersion? versionInfo = null,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken token = default,
        string projectType = "mod");
}

/// <summary>装一个整合包。</summary>
public sealed record ModpackInstallRequest(
    /// <summary>`.mrpack` 文件路径（本地已经下好的那个）。</summary>
    string MrpackPath,
    string MinecraftDir,
    /// <summary>实例名。空 = 用整合包自己的名字（会先消毒成合法目录名）。</summary>
    string? InstanceName = null,
    /// <summary>顺带把整合包要的加载器装上（Fabric / Quilt 能装；Forge 那几家走安装器）。</summary>
    bool InstallLoader = true,
    /// <summary>
    /// `.mrpack` 的**下载地址**（可选，追加在末尾的扩展位）。
    ///
    /// 给"从 Modrinth 直接装一个整合包"用：那时手上只有 URL，还没有本地文件。
    /// 传了它就由**服务自己**先下到临时文件、再走同一条安装流水线
    /// （临时文件装完就删）。不给 = 老行为，只认 <see cref="MrpackPath"/>。
    /// </summary>
    string? MrpackUrl = null);

/// <summary>装整合包的结果。</summary>
public sealed record ModpackInstallResult(
    bool Ok,
    string Message,
    string? VersionId = null,
    int FilesDownloaded = 0,
    int FilesFailed = 0,
    int OverridesWritten = 0,
    /// <summary>整合包里的告警（不安全路径 / 重复落点 / 缺下载地址…）。</summary>
    IReadOnlyList<string>? Warnings = null,
    /// <summary>加载器没装上时是 true —— 实例列得出来但点启动会缺 mainClass，界面要说清楚。</summary>
    bool NeedsLoader = false);

/// <summary>整合包（`.mrpack`）安装。</summary>
public interface IModpackService
{
    bool IsPrototype { get; }

    /// <summary>
    /// 只看不装：把这个 `.mrpack` 解析成计划（给界面预览"要装什么"）。
    /// 解析不了返回 null，原因在 <paramref name="error"/> 里。
    /// </summary>
    Engine.ModpackPlan? Inspect(byte[] blob, out string error);

    Task<ModpackInstallResult> InstallAsync(
        ModpackInstallRequest request,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken token = default);
}

/// <summary>
/// 按版本覆盖启动参数（`versions.json`）。
///
/// ⚠️ 三态，不是两态：每一项要么「跟随启动器默认」（文件里**没有**这个键）、
///    要么「这个版本自己定」（文件里有）。**不是**"把默认值抄一份过来"——
///    抄过来的话，以后改了启动器默认，所有动过一次的版本都不会跟着变。
///    所以"取消自定义" = 把这个键删掉（<see cref="Update"/> 传空对象）。
/// </summary>
public interface IVersionSettingsService
{
    bool IsPrototype { get; }

    /// <summary>这个版本的覆盖项（没有就是空的）。</summary>
    Engine.PerVersionSettings Get(string versionId);

    /// <summary>整体替换。空的 = 恢复成全部跟随默认。</summary>
    bool Update(string versionId, Engine.PerVersionSettings overrides);

    /// <summary>忘掉这个版本的覆盖 = 全部跟随默认。</summary>
    bool Forget(string versionId);

    /// <summary>被覆盖过的版本 id（界面可以标一下"这个版本改过"）。</summary>
    IReadOnlyList<string> CustomizedVersions();

    /// <summary>合上启动器默认，算最终生效的值。</summary>
    Engine.EffectiveSettings Resolve(string versionId, Engine.EffectiveSettings defaults);

    /// <summary>启动器这一层的默认值（界面上「跟随全局设置」跟的就是它）。</summary>
    Engine.EffectiveSettings LauncherDefaults();
}

/// <summary>所有服务的集合 —— 界面只拿这一个东西，免得每个页面各 new 一遍。</summary>
public sealed class LauncherServices
{
    public LauncherServices(
        IGameService games,
        IJavaService java,
        IAccountService accounts,
        ILaunchService launch,
        IDownloadService downloads,
        /// <summary>模组加载器（Fabric / Forge / NeoForge / Quilt / OptiFine）。</summary>
        ILoaderService loaders,
        /// <summary>模组管理与 Modrinth。</summary>
        IModService mods,
        /// <summary>整合包（`.mrpack`）。</summary>
        IModpackService modpacks,
        /// <summary>按版本覆盖启动参数（versions.json）。</summary>
        IVersionSettingsService versionSettings)
    {
        Games = games;
        Java = java;
        Accounts = accounts;
        Launch = launch;
        Downloads = downloads;
        Loaders = loaders;
        Mods = mods;
        Modpacks = modpacks;
        VersionSettings = versionSettings;
    }

    public IGameService Games { get; }

    public IJavaService Java { get; }

    public IAccountService Accounts { get; }

    public ILaunchService Launch { get; }

    public IDownloadService Downloads { get; }

    public ILoaderService Loaders { get; }

    public IModService Mods { get; }

    public IModpackService Modpacks { get; }

    public IVersionSettingsService VersionSettings { get; }

    /// <summary>有任何一个服务还是假数据 → 界面要走"原型提示"那条路。</summary>
    public bool AnyPrototype => Games.IsPrototype || Java.IsPrototype
                                || Launch.IsPrototype || Downloads.IsPrototype
                                || Loaders.IsPrototype || Mods.IsPrototype
                                || Modpacks.IsPrototype || VersionSettings.IsPrototype;

    /// <summary>挂上全部假实现（原型阶段用，自检里也还在用）。</summary>
    public static LauncherServices Prototype() => new(
        new StubGameService(),
        new StubJavaService(),
        new StubAccountService(),
        new StubLaunchService(),
        new StubDownloadService(),
        new StubLoaderService(),
        new StubModService(),
        new StubModpackService(),
        new StubVersionSettingsService());

    /// <summary>
    /// 挂上**真引擎**。配置目录、游戏目录都从 <paramref name="settings"/> 里读，
    /// 所以界面改完设置只要用新参数重建一次这个对象就行。
    /// </summary>
    public static LauncherServices Production(Settings.AppSettings settings, string baseDir)
        => Engine.ServiceFactory.Create(settings, baseDir);
}
