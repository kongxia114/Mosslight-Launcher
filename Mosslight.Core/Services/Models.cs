namespace Mosslight.Core.Services;

// 契约里用到了引擎层那几个**纯常量 / 纯模型**（Modrinth 的排序名、
// 本地 mod 的条目类型）。方向是 Services → Engine，不是反过来 ——
// Engine 里的东西只认 Models.cs，不认这层接口。
using Mosslight.Core.Services.Engine;

// 数据模型 + 服务契约：界面只认这里的接口，不认实现。
// ⚠️ record 的位置参数是对外契约，加字段只能追加在末尾并给默认值，插在中间会让调用点静默错位。

/// <summary>装好的一个游戏版本（启动页列表里的一行）。</summary>
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

public enum AccountKind
{
    Offline,

    Microsoft,

    /// <summary>第三方皮肤站（authlib-injector）。</summary>
    ThirdParty,
}

public sealed record Account(
    string Name,
    AccountKind Kind,
    string Uuid = "",
    string? AvatarPath = null,
    DateTimeOffset? CreatedAt = null,
    // 第三方（LittleSkin）登录态：离线账户这三项永远是空
    string AccessToken = "",
    string RefreshToken = "",
    string AuthServer = "",
    string ClientToken = "",
    /// <summary>第三方 token 的到期时间；null = 不知道（Yggdrasil 密码登录通常不返回）。</summary>
    DateTimeOffset? TokenExpiresAt = null,
    /// <summary>这个第三方账号下的**全部**角色（LittleSkin 一个账号可以有多个）。</summary>
    IReadOnlyList<Auth.MinecraftProfile>? AvailableProfiles = null,
    /// <summary>当前选中的角色 UUID（AvailableProfiles 里的某一个）。</summary>
    string SelectedProfileUuid = "");

/// <summary>
/// 启动一次游戏要的东西；⚠️ 位置参数**只有前五个是老的**，后面都是接真引擎时追加的（带默认值）。
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
    /// <summary>自定义信息（版本设置里的）：覆盖 `${version_type}`。</summary>
    string CustomInfo = "",
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
    /// <summary>不确定进度（总大小未知）。</summary>
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

public interface IGameService
{
    /// <summary>是不是**原型假数据**（真引擎还没接上）。</summary>
    bool IsPrototype { get; }

    IReadOnlyList<GameVersion> Installed { get; }

    /// <summary>重新扫一遍游戏目录（磁盘上的变化要手动触发才看得见）。</summary>
    Task<IReadOnlyList<GameVersion>> RefreshAsync(CancellationToken token = default);

    /// <summary>扫描过程中跳过的坏版本（读不了 / json 坏了）。</summary>
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

    /// <summary>
    /// 这个版本**至少**要哪个 Java 主版本（读版本 JSON 的 javaVersion，读不到按 8 算）。
    /// </summary>
    int RequiredMajor(GameVersion version);

    IReadOnlyList<string> CustomPaths { get; }

    Task<JavaRuntime?> AddCustomAsync(string javaExe, CancellationToken token = default);

    bool RemoveCustom(string javaExe);
}

public interface IAccountService
{
    IReadOnlyList<Account> All { get; }

    /// <summary>当前选中的档案；null = 还没建（启动页要显示"未选择档案"）。</summary>
    Account? Current { get; }

    Account AddOffline(string name);

    void Select(Account? account);

    /// <summary>删掉一个档案（同名删）。</summary>
    void Remove(string name);

    /// <summary>重新从 accounts.json 读一遍。</summary>
    void Reload();
}

public interface ILaunchService
{
    bool IsPrototype { get; }

    Task<LaunchResult> LaunchAsync(
        LaunchRequest request,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken token = default);

    /// <summary>正在跑的游戏进程；null = 没在跑。</summary>
    bool IsRunning { get; }

    /// <summary>游戏退出时通知（参数：退出码）。</summary>
    event EventHandler<int>? GameExited;

    /// <summary>游戏进程输出的一行（stdout + stderr 合流）。</summary>
    event EventHandler<string>? GameOutput;

    /// <summary>
    /// 把这次启动**最终会用的参数**算出来（合上「版本设置」那一层覆盖），但**不起进程**。
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
    string? VersionId = null,
    /// <summary>顺便把 Fabric API 装进 `mods/`（只有 Fabric 才有意义）。</summary>
    bool InstallFabricApi = false,
    /// <summary>OptiFine 专用：安装器地址要用的两段（版本列表里带着）。</summary>
    string? OptifineType = null,
    string? OptifinePatch = null,
    /// <summary>LabyMod 专用：频道（`production` / `snapshot`）。</summary>
    string? Channel = null);

/// <summary>装加载器的结果。</summary>
public sealed record LoaderInstallResult(
    bool Ok,
    string Message,
    string? VersionId = null,
    /// <summary>装载器自己要下的那些文件（原版那批不算）。</summary>
    InstallResult? Download = null,
    /// <summary>老实说清楚"这个我们还没做"，别假装能装。</summary>
    bool NotImplemented = false);

/// <summary>一行加载器版本。</summary>
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

/// <summary>加载器那一行的状态。</summary>
public enum LoaderState
{
    /// <summary>能列出并安装。</summary>
    Ok,

    NotImplemented,

    /// <summary>有接口但这次拉不到（网络问题）。</summary>
    Error,
}

/// <summary>下载页第二页要显示的一行。</summary>
public sealed record LoaderRow(
    string Key,
    string Name,
    LoaderState State,
    int Count,
    /// <summary>最新的那个版本号。</summary>
    string Latest,
    IReadOnlyList<LoaderVersion> Versions,
    string Error);

/// <summary>模组加载器：列版本 + 装。</summary>
public interface ILoaderService
{
    bool IsPrototype { get; }

    /// <summary>
    /// 这个游戏版本能装哪些加载器、各有哪些版本；⚠️ 一家的接口挂了不能让其它四家都不显示 —— 每家
    /// 单独标状态 （LoaderState.Ok / LoaderState.Error / LoaderState.NotImplemented）。
    /// </summary>
    Task<IReadOnlyList<LoaderRow>> CatalogAsync(string minecraftVersion, CancellationToken token = default);

    bool CanInstall(string loaderKey);

    /// <summary>Fabric API 在这个游戏版本上的**最新版号**（拿不到返回 null）。</summary>
    Task<string?> FabricApiVersionAsync(string minecraftVersion, CancellationToken token = default);

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

/// <summary>模组管理与 Modrinth。</summary>
public interface IModService
{
    bool IsPrototype { get; }

    /// <summary>
    /// 搜索（返回原始的回包：`hits` / `total_hits` / `offset` / `limit`）；⚠️ `loader` 只对 mod /
    /// modpack 有意义：资源包和光影包没有加载器一说，硬塞 `categories:fabric` 会把结果
    /// **几乎全筛掉而且不报错** （实测资源包 36437 → 16）。
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
        string source = ModrinthApi.AnyOption,
        CancellationToken token = default);

    /// <summary>项目的所有版本（新的在前）。</summary>
    Task<IReadOnlyList<System.Text.Json.Nodes.JsonObject>> ProjectVersionsAsync(
        string projectId, CancellationToken token = default);

    /// <summary>按游戏版本 + 加载器筛。</summary>
    IReadOnlyList<System.Text.Json.Nodes.JsonObject> FilterVersions(
        IEnumerable<System.Text.Json.Nodes.JsonObject> versions,
        string gameVersion = "", string loader = "");

    /// <summary>
    /// 把项目的全部版本分成「游戏版本 × 加载器 × 正式/预览」的组（详情页那份目录）；⚠️ 跟
    /// FilterVersions 是**两件事**：那个是"只留匹配的"，这个是"全都要，但把匹配的排最前、并且标出
    /// 来" —— 详情页要的是后者，不然用户永远看不到"我要的 1.12.2 那一版"。
    /// </summary>
    IReadOnlyList<Engine.ModVersionGroup> GroupVersions(
        IEnumerable<System.Text.Json.Nodes.JsonObject> versions,
        string preferVersion = "", string preferLoader = "",
        bool splitByLoader = true);

    /// <summary>这个版本的 mod 该装进哪个 `mods\` 目录。</summary>
    string ModsDirFor(string minecraftDir, string gameVersion, string loader,
        GameVersion? versionInfo = null);

    /// <summary>
    /// **这个项目类型**该装进哪个目录（资源包 → `resourcepacks\`、光影 → `shaderpacks\`）；⚠️ 返
    /// 回 **null** 表示这个类型没有游戏目录下的固定落点 —— 目前只有数据包 （要放进某一个存档的
    /// `saves/&lt;存档&gt;/datapacks/`）。
    /// </summary>
    string? ContentDirFor(string minecraftDir, string projectType, string gameVersion, string loader,
        GameVersion? versionInfo = null);

    /// <summary>列出一个 `mods\` 目录里的 mod。</summary>
    IReadOnlyList<Engine.ModsDirectory.LocalMod> LocalMods(string modsDir);

    /// <summary>启用 / 禁用（改 `.disabled` 后缀）。</summary>
    string? SetEnabled(string path, bool enabled);

    /// <summary>删掉一个 mod（真的删掉了才返回 true）。</summary>
    bool Delete(string path);

    /// <summary>
    /// 把一条 Modrinth 版本装进**这个项目类型该落的目录**；⚠️ 目录是用 ContentDirFor 算的（mod 走
    /// ModsDirFor）—— **版本隔离的版本装进版本目录**，否则装进共用的 `mods/`（资源包 / 光影同理，
    /// 只是最后那段目录名不同）。
    /// </summary>
    Task<ModInstallResult> InstallAsync(
        System.Text.Json.Nodes.JsonObject version,
        string minecraftDir,
        string gameVersion,
        string loader,
        GameVersion? versionInfo = null,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken token = default,
        string projectType = "mod",
        string? targetDir = null);
}

/// <summary>装一个整合包。</summary>
public sealed record ModpackInstallRequest(
    /// <summary>`.mrpack` 文件路径（本地已经下好的那个）。</summary>
    string MrpackPath,
    string MinecraftDir,
    string? InstanceName = null,
    /// <summary>顺带把整合包要的加载器装上（Fabric / Quilt 能装；Forge 那几家走安装器）。</summary>
    bool InstallLoader = true,
    /// <summary>`.mrpack` 的**下载地址**（可选，追加在末尾的扩展位）。</summary>
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
    /// <summary>
    /// 加载器没装上时是 true —— 实例列得出来但点启动会缺 mainClass，界面要说清楚。
    /// </summary>
    bool NeedsLoader = false);

/// <summary>整合包（`.mrpack`）安装。</summary>
public interface IModpackService
{
    bool IsPrototype { get; }

    /// <summary>只看不装：把这个 `.mrpack` 解析成计划（给界面预览"要装什么"）。</summary>
    Engine.ModpackPlan? Inspect(byte[] blob, out string error);

    Task<ModpackInstallResult> InstallAsync(
        ModpackInstallRequest request,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken token = default);
}

/// <summary>
/// 按版本覆盖启动参数（`versions.json`）；⚠️ 三态，不是两态：每一项要么「跟随启动器默认」（文件里
/// **没有**这个键）、要么「这个版本自己定」（文件里有）。
/// </summary>
public interface IVersionSettingsService
{
    bool IsPrototype { get; }

    /// <summary>这个版本的覆盖项（没有就是空的）。</summary>
    Engine.PerVersionSettings Get(string versionId);

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

    /// <summary>挂上**真引擎**。</summary>
    public static LauncherServices Production(Settings.AppSettings settings, string baseDir)
        => Engine.ServiceFactory.Create(settings, baseDir);
}
