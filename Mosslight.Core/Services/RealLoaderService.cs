using System.Text.Json.Nodes;
using Mosslight.Core.Localization;
using Mosslight.Core.Services.Engine;
using Mosslight.Core.Settings;

namespace Mosslight.Core.Services;

// ============================================================================
//  模组加载器安装的真实现
//  （移植自 1.0.0 的 core/loader_install.py）
//
//  ## 两类加载器，两条完全不同的路
//
//  | 类型 | 谁 | 怎么装 |
//  |---|---|---|
//  | 有 profile JSON | **Fabric / Quilt** | 一个 GET 就拿到启动器要的版本 JSON（mainClass + libraries 都写好了）→ 下库 → 落盘。**不用 Java** |
//  | 只有安装器 jar | Forge / NeoForge / OptiFine | 得下安装器再 `java -jar installer.jar --installClient` |
//
//  ## 落盘约定（跟 PCL 一致）
//
//      versions/<mc>/<mc>.json                            原版（加载器版本 inheritsFrom 它）
//      versions/<mc>-Fabric 0.19.5/<同名>.json            加载器 profile
//      versions/<mc>-Fabric 0.19.5/<同名>-natives/        启动时解出来的 natives
//      versions/<mc>-Fabric 0.19.5/mods/<Fabric API>.jar  mod 走**版本隔离**目录
//
//  ⚠️ 加载器版本 JSON 里的 **`inheritsFrom` 一定要写**：启动时的 `merge_version()`
//     靠它去合原版的 mainClass / 参数 / 资源索引，并且顺带把客户端 jar 指到原版目录。
//     少写这一行，classpath 里就没有 Minecraft 的类，Fabric 会报
//     "couldn't locate the game"。
// ============================================================================

/// <summary>真实的加载器服务。</summary>
public sealed class RealLoaderService : ILoaderService
{
    /// <summary>Fabric / Quilt 的库默认挂在 maven 的根上。</summary>
    private const string DefaultMaven = "https://maven.fabricmc.net/";

    /// <summary>Fabric API 这个 mod 的 Modrinth 项目 id。</summary>
    public const string FabricApiProject = "fabric-api";

    /// <summary>有干净 profile JSON 的（不用 Java 就能装）。</summary>
    private static readonly string[] ProfileLoaders = { "fabric", "quilt", "legacy_fabric" };

    /// <summary>只有安装器 jar 的（要 Java 跑 installer）。</summary>
    private static readonly string[] InstallerLoaders = { "forge", "neoforge", "optifine" };

    /// <summary>
    /// 第三条路：**版本 JSON 由我们自己拼**，不跑安装器。
    ///
    /// · `liteloader`：官方安装器已经下不到了（404），而它本来就是个
    ///   "launchwrapper + tweakClass" 的加载器，拼 JSON 就是它的装法
    ///   （见 <see cref="LiteLoaderCatalog"/>）；
    /// · `cleanroom`：它自己的安装器没有可抄的无人值守参数，但安装器里那份
    ///   `version.json` 是完整的 —— 抽出来落盘即可（见 <see cref="CleanroomCatalog"/>）；
    /// · `labymod`：照 PCL 的配方拼（剔隔离 LWJGL + 追加它的库 + 37 MB 客户端 jar +
    ///   `labymod_data`），见 <see cref="LabyModCatalog.BuildVersionJson"/>。
    /// </summary>
    private static readonly string[] JsonBuildLoaders = { "liteloader", "cleanroom", "labymod" };

    private readonly AppSettings _settings;
    private readonly RealGameService _games;
    private readonly RealDownloadService _downloads;
    private readonly VersionManifest _manifest;

    /// <summary>
    /// 启动器自己的目录 —— 安装器日志写在 `<它>/Logs/installer-*.log`，跟
    /// `launcher.log` 同一个地方（用户找得到、发得出来）。
    /// 没给就退回配置目录：至少能落地。
    /// </summary>
    private readonly string _baseDir;

    public RealLoaderService(
        AppSettings settings,
        RealGameService games,
        RealDownloadService downloads,
        string configDir,
        string? baseDir = null)
    {
        _settings = settings;
        _games = games;
        _downloads = downloads;
        _baseDir = string.IsNullOrWhiteSpace(baseDir) ? configDir : baseDir!;
        _manifest = new VersionManifest(configDir);
    }

    public bool IsPrototype => false;

    public async Task<IReadOnlyList<LoaderRow>> CatalogAsync(
        string minecraftVersion, CancellationToken token = default)
        => await LoaderCatalog.LoaderRowsAsync(minecraftVersion, token).ConfigureAwait(false);

    public bool CanInstall(string loaderKey)
    {
        var key = (loaderKey ?? string.Empty).ToLowerInvariant();
        return ProfileLoaders.Contains(key)
               || InstallerLoaders.Contains(key)
               || JsonBuildLoaders.Contains(key);
    }

    /// <summary>
    /// Fabric API 在这个游戏版本上的最新版号（安装页要显示 `0.163.0+26.3`，
    /// 拿不到就 null —— 界面说"没有可用版本"，不影响别的东西）。
    /// </summary>
    public async Task<string?> FabricApiVersionAsync(
        string minecraftVersion, CancellationToken token = default)
    {
        try
        {
            var all = await ModrinthApi.GetProjectVersionsAsync(FabricApiProject, token)
                .ConfigureAwait(false);
            return ModrinthApi.FilterVersions(all, minecraftVersion, "fabric")
                .FirstOrDefault()?["version_number"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // ============================================================
    //  安装
    // ============================================================

    public async Task<LoaderInstallResult> InstallAsync(
        LoaderInstallRequest request,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken token = default)
    {
        void Report(LaunchStage stage, string message, double fraction = 0, bool indeterminate = false)
            => progress?.Report(new LaunchProgress(stage, message, fraction, indeterminate));

        var key = (request.LoaderKey ?? string.Empty).ToLowerInvariant();
        var mcVersion = request.MinecraftVersion;
        var mcDir = request.MinecraftDir;

        try
        {
            if (!CanInstall(key))
            {
                return new LoaderInstallResult(false, Localizer.Tr(
                    "{name} 还没做", ("name", LoaderCatalog.LoaderNames.TryGetValue(key, out var n) ? n : key)),
                    NotImplemented: true);
            }

            // ---------- 1. 原版得先在 ----------
            // 加载器版本 inheritsFrom 原版，原版不在的话装完也起不来
            // ⚠️ Cleanroom 是例外：它的 version.json 不 inheritsFrom，库和客户端 jar
            //    都自带（`downloads.client` 直接指 1.12.2 的客户端 jar），先把原版
            //    整份装上纯属白下几百 MB。
            if (key != "cleanroom")
            {
                Report(LaunchStage.Preparing, Localizer.Tr("正在确认原版 {name} 已安装…", ("name", mcVersion)), 0.02);
                var vanillaReady = await EnsureVanillaAsync(mcVersion, mcDir, progress, token)
                    .ConfigureAwait(false);
                if (vanillaReady is not null)
                {
                    return vanillaReady;
                }
            }

            token.ThrowIfCancellationRequested();

            // ---------- 2. 三条路 ----------
            if (key == "cleanroom")
            {
                return await InstallCleanroomAsync(request, progress, token).ConfigureAwait(false);
            }
            if (key == "liteloader")
            {
                return await InstallLiteLoaderAsync(request, progress, token).ConfigureAwait(false);
            }
            if (key == "labymod")
            {
                return await InstallLabyModAsync(request, progress, token).ConfigureAwait(false);
            }
            return ProfileLoaders.Contains(key)
                ? await InstallProfileLoaderAsync(request, progress, token).ConfigureAwait(false)
                : await InstallWithInstallerAsync(request, progress, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new LoaderInstallResult(false, Localizer.Tr("已取消"));
        }
        catch (LoaderSetupException ex)
        {
            Report(LaunchStage.Failed, ex.Message, 0);
            return new LoaderInstallResult(false, ex.Message);
        }
        catch (LoaderInstallException ex)
        {
            Report(LaunchStage.Failed, ex.Message, 0);
            return new LoaderInstallResult(false, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
                                      or UnauthorizedAccessException or System.Text.Json.JsonException
                                      or InvalidOperationException or NotSupportedException)
        {
            var message = Localizer.Tr("装加载器失败：{err}", ("err", ex.Message));
            Report(LaunchStage.Failed, message, 0);
            return new LoaderInstallResult(false, message);
        }
    }

    /// <summary>
    /// 原版没装就先把原版装上。返回 null = 可以继续；非 null = 直接返回这个失败。
    /// </summary>
    private async Task<LoaderInstallResult?> EnsureVanillaAsync(
        string mcVersion, string mcDir, IProgress<LaunchProgress>? progress, CancellationToken token)
    {
        var jsonPath = Path.Combine(mcDir, "versions", mcVersion, mcVersion + ".json");
        if (File.Exists(jsonPath))
        {
            return null;
        }

        progress?.Report(new LaunchProgress(LaunchStage.Downloading,
            Localizer.Tr("原版 {name} 还没装，先装它", ("name", mcVersion)), 0.03));

        var install = await _downloads.InstallVanillaAsync(mcVersion, mcDir, progress, token)
            .ConfigureAwait(false);
        return install.Ok
            ? null
            : new LoaderInstallResult(false, Localizer.Tr(
                "原版 {name} 没装上，加载器也没法装：{msg}", ("name", mcVersion), ("msg", install.Message)));
    }

    // ============================================================
    //  Fabric / Quilt：profile JSON
    // ============================================================

    /// <summary>profile JSON 的地址；这家没有 profile（要跑安装器）时返回空串。</summary>
    public static string ProfileUrl(string mcVersion, string key, string loaderVersion)
    {
        key = (key ?? string.Empty).ToLowerInvariant();
        var mc = (mcVersion ?? string.Empty).Trim();
        var lv = (loaderVersion ?? string.Empty).Trim();
        if (mc.Length == 0 || lv.Length == 0)
        {
            return string.Empty;
        }
        return key switch
        {
            // ⚠️ Fabric 走 **BMCLAPI 镜像**；Quilt **没有镜像**（实测 404），只能走官方
            "fabric" => $"{LoaderCatalog.FabricMeta}/v2/versions/loader/{mc}/{lv}/profile/json",
            // 设计说明见 docs/design-notes.md#mosslight-core-services-realloaderservice-cs
            "legacy_fabric" => $"{LoaderCatalog.LegacyFabricMeta}/v2/versions/loader/{mc}/{lv}/profile/json",
            "quilt" => $"{LoaderCatalog.QuiltMeta}/v3/versions/loader/{mc}/{lv}/profile/json",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// 确认拿到的是"启动器要的那份 profile"（**纯函数**，离线可测）。
    ///
    /// 只认 `mainClass` + 非空 `libraries`：少了任何一个，写出去的版本 JSON
    /// 要么起不来、要么少库，而这两种失败在界面上都只表现为"启动崩了"，
    /// 离真正的原因很远。
    /// </summary>
    public static JsonObject CheckProfile(JsonNode? data, string key = "", string loaderVersion = "")
    {
        _ = loaderVersion;
        var name = LoaderCatalog.LoaderNames.TryGetValue(
            (key ?? string.Empty).ToLowerInvariant(), out var n) ? n : (key ?? string.Empty);

        if (data is not JsonObject obj)
        {
            throw new LoaderInstallException(Localizer.Tr("这个加载器没有可用的版本数据"));
        }
        if (string.IsNullOrWhiteSpace(obj["mainClass"]?.GetValue<string>()))
        {
            throw new LoaderInstallException(Localizer.Tr(
                "{name} 的版本数据里没有主类，装不了", ("name", name)));
        }
        if (obj["libraries"] is not JsonArray { Count: > 0 })
        {
            throw new LoaderInstallException(Localizer.Tr(
                "{name} 的版本数据里没有库列表，装不了", ("name", name)));
        }
        return obj;
    }

    /// <summary>取 profile JSON。</summary>
    public static async Task<JsonObject> FetchProfileAsync(
        string mcVersion, string key, string loaderVersion, CancellationToken token = default)
    {
        var url = ProfileUrl(mcVersion, key, loaderVersion);
        if (url.Length == 0)
        {
            var name = LoaderCatalog.LoaderNames.TryGetValue(
                (key ?? string.Empty).ToLowerInvariant(), out var n) ? n : key;
            throw new LoaderInstallException(Localizer.Tr(
                "{name} 得跑安装器才能装", ("name", name)));
        }

        var data = await GameInstaller.HttpJsonAsync(url, token).ConfigureAwait(false);
        if (data is null)
        {
            throw new LoaderInstallException(Localizer.Tr(
                "拿不到 {name} 的版本数据（网络问题）", ("name", key)));
        }
        return CheckProfile(data, key, loaderVersion);
    }

    /// <summary>
    /// profile 里的 `libraries` → 下载任务（**纯函数**）。
    ///
    /// ⚠️ **不复用 `GameInstaller.PlanVersion()`**：Fabric / Quilt 的 profile 里
    ///    库是 `{name, url, sha1, size}` 这种 maven 简写，**没有 `downloads.artifact`**。
    ///    `PlanVersion()` 会走"按 name 拼 maven 路径"的兜底分支，那条分支
    ///    **不带 sha1、也不带 size** —— 结果是既没有完整性校验，进度条也算不出
    ///    总量（用户会看到一条没有百分比、速度也不准的进度窗口）。这里自己拼，
    ///    把能拿到的 sha1 / size 都带上。
    /// </summary>
    public static List<InstallTask> LibraryTasks(JsonObject profile, string mcDir)
    {
        var tasks = new List<InstallTask>();

        foreach (var node in profile["libraries"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject lib)
            {
                continue;
            }
            // ⚠️ 不传就按**当前系统**挑库：加载器的 profile 里常常带别的系统的库
            //    （Quilt 的 osx 那几个），全下下来纯属白费流量
            if (!LaunchRules.RulesAllow(lib["rules"]))
            {
                continue;
            }

            var artifact = (lib["downloads"] as JsonObject)?["artifact"] as JsonObject;
            var artifactUrl = artifact?["url"]?.GetValue<string>();
            var artifactPath = artifact?["path"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(artifactUrl) && !string.IsNullOrEmpty(artifactPath))
            {
                tasks.Add(new InstallTask(
                    Url: artifactUrl,
                    Path: Path.Combine(mcDir, "libraries", artifactPath.Replace('/', Path.DirectorySeparatorChar)),
                    Sha1: artifact?["sha1"]?.GetValue<string>() ?? string.Empty,
                    Size: artifact?["size"]?.GetValue<long>() ?? 0,
                    Label: Path.GetFileName(artifactPath),
                    Kind: Localizer.Tr("库")));
                continue;
            }

            var name = lib["name"]?.GetValue<string>() ?? string.Empty;
            if (name.Length == 0 || !name.Contains(':'))
            {
                continue;
            }
            // ⚠️ 只有 classifiers（native）的库别拼主 artifact：它压根没有主 jar，
            //    拼出来的路径必然 404
            if ((lib["downloads"] as JsonObject)?["classifiers"] is JsonObject)
            {
                continue;
            }

            var (rel, _) = LaunchRules.MavenToPath(name);
            if (rel is null)
            {
                continue;
            }
            var baseUrl = (lib["url"]?.GetValue<string>() ?? DefaultMaven).TrimEnd('/');
            tasks.Add(new InstallTask(
                Url: $"{baseUrl}/{rel}",
                Path: Path.Combine(mcDir, "libraries", rel.Replace('/', Path.DirectorySeparatorChar)),
                Sha1: lib["sha1"]?.GetValue<string>() ?? string.Empty,
                Size: lib["size"]?.GetValue<long>() ?? 0,
                Label: Path.GetFileName(rel),
                Kind: Localizer.Tr("库")));
        }

        return tasks;
    }

    /// <summary>
    /// profile → 存盘用的版本 JSON（**纯函数**）。
    ///
    /// 正常情况：改个 `id`、写上 `inheritsFrom` 就成了。
    ///
    /// ⚠️ 例外：`versionId == mcVersion`（用户把名字改成了原版 id）时
    ///    **不能**写 `inheritsFrom` —— 那是自己继承自己，读的时候会一路递归到
    ///    `MaxInheritDepth` 然后报错。这种情况把原版合进来写成自包含的一份。
    /// </summary>
    public static JsonObject ProfileVersionJson(
        JsonObject profile, string mcVersion, string versionId, JsonObject? vanillaVj = null)
    {
        var data = (JsonObject)profile.DeepClone();
        data["id"] = versionId.Length > 0 ? versionId : data["id"]?.GetValue<string>() ?? string.Empty;
        if (data["type"] is null)
        {
            data["type"] = "release";
        }

        if (versionId.Length > 0 && versionId == mcVersion)
        {
            if (vanillaVj is not null)
            {
                // 把原版合进来，写成自包含的一份
                var merged = MergeVanillaInto(vanillaVj, data);
                merged.Remove("inheritsFrom");
                merged["id"] = versionId;
                // jar 指回自己：合并时它被写成父版本 id（原版 id），
                // 而自包含版本的 jar 就在本目录、名字是本目录名
                merged["jar"] = versionId;
                return merged;
            }
            data.Remove("inheritsFrom");
            return data;
        }

        data["inheritsFrom"] = mcVersion;
        return data;
    }

    /// <summary>
    /// 把原版 JSON 合进加载器的 profile（只在"目录名和原版撞了"时用）。
    ///
    /// 这里是 <see cref="VersionJsonLoader"/> 那套合并的简化版：只补 profile
    /// **缺**的字段，不覆盖它自己的。
    /// </summary>
    private static JsonObject MergeVanillaInto(JsonObject vanilla, JsonObject child)
    {
        var merged = (JsonObject)vanilla.DeepClone();
        foreach (var (key, value) in child)
        {
            merged[key] = value?.DeepClone();
        }

        // 库要合并（父在前、子在后），不是整个顶掉
        var libraries = new JsonArray();
        foreach (var source in new[] { vanilla["libraries"] as JsonArray, child["libraries"] as JsonArray })
        {
            foreach (var lib in source ?? new JsonArray())
            {
                libraries.Add(lib?.DeepClone());
            }
        }
        merged["libraries"] = libraries;
        return merged;
    }

    private async Task<LoaderInstallResult> InstallProfileLoaderAsync(
        LoaderInstallRequest request,
        IProgress<LaunchProgress>? progress,
        CancellationToken token)
    {
        var key = request.LoaderKey.ToLowerInvariant();
        var mcDir = request.MinecraftDir;
        var name = LoaderCatalog.LoaderNames.TryGetValue(key, out var n) ? n : key;

        var versionId = !string.IsNullOrEmpty(request.VersionId)
            ? request.VersionId!
            : LoaderCatalog.VersionFolderName(request.MinecraftVersion, key, request.LoaderVersion);
        if (versionId.Length == 0)
        {
            throw new LoaderInstallException(Localizer.Tr("加载器版本不对，拼不出目录名"));
        }

        progress?.Report(new LaunchProgress(LaunchStage.Preparing,
            Localizer.Tr("正在获取 {name} {ver} 的安装数据…", ("name", name), ("ver", request.LoaderVersion)), 0.05, true));

        var profile = await FetchProfileAsync(request.MinecraftVersion, key, request.LoaderVersion, token)
            .ConfigureAwait(false);

        var versionDir = Path.Combine(mcDir, "versions", versionId);

        // ⚠️ 名字跟原版 id 撞了的时候要把原版合进来（见 ProfileVersionJson）
        JsonObject? vanillaVj = null;
        if (versionId == request.MinecraftVersion)
        {
            vanillaVj = VersionJsonLoader.LoadRaw(mcDir, request.MinecraftVersion);
        }

        var versionJson = ProfileVersionJson(profile, request.MinecraftVersion, versionId, vanillaVj);
        var tasks = LibraryTasks(profile, mcDir);

        // 顺带装 Fabric API（用户勾了就装）
        if (request.InstallFabricApi && key == "fabric")
        {
            try
            {
                var apiTask = await FabricApiTaskAsync(request.MinecraftVersion, Path.Combine(versionDir, "mods"), token)
                    .ConfigureAwait(false);
                if (apiTask is not null)
                {
                    tasks.Add(apiTask);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException
                                          or InvalidOperationException or TaskCanceledException)
            {
                // Fabric API 装不上不该让整个加载器安装失败
                progress?.Report(new LaunchProgress(LaunchStage.Downloading,
                    Localizer.Tr("Fabric API 没取到（不影响加载器安装）"), 0.1));
            }
        }

        // ⚠️ 版本 JSON **必须写在下载之前**：装完没有 json 的话，版本列表
        //    （靠 `<目录>/<目录>.json` 认版本）根本看不到它 —— "下完了但列表里没有"
        GameInstaller.WriteVersionJson(versionJson, versionDir, versionId);

        var warnings = tasks.Count == 0
            ? Localizer.Tr("加载器的库里一个能下的都没有（数据不对？）")
            : string.Empty;

        var download = await RunDownloadsAsync(tasks, progress, 0.15, 0.9, token).ConfigureAwait(false);
        if (!download.Ok)
        {
            return new LoaderInstallResult(false, download.Message, versionId, download);
        }

        // ⚠️ Fabric 本身**不要求**这份 jar：`inheritsFrom` 在，classpath 用父版本的
        // ⚠️ `EnsureInstanceJar` 自己会判断：已经有 jar → 一个字节都不动（OptiFine 那种补丁 jar
        // 设计说明见 docs/design-notes.md#mosslight-core-services-realloaderservice-cs
        if (LoaderInstaller.EnsureInstanceJar(mcDir, versionId, request.MinecraftVersion))
        {
            progress?.Report(new LaunchProgress(LaunchStage.Preparing,
                Localizer.Tr("把原版客户端 jar 复制成实例自己的（{id}.jar）", ("id", versionId)), 0.93));
        }

        progress?.Report(new LaunchProgress(LaunchStage.Done,
            Localizer.Tr("{name} {ver} 装好了", ("name", name), ("ver", request.LoaderVersion)), 1.0));

        var message = Localizer.Tr("{name} {ver} 装好了（{n} 个文件）",
            ("name", name), ("ver", request.LoaderVersion), ("n", download.FilesDownloaded));
        if (warnings.Length > 0)
        {
            message += " —— " + warnings;
        }
        return new LoaderInstallResult(true, message, versionId, download);
    }

    // ============================================================
    //  Forge / NeoForge / OptiFine：跑安装器
    // ============================================================

    private async Task<LoaderInstallResult> InstallWithInstallerAsync(
        LoaderInstallRequest request,
        IProgress<LaunchProgress>? progress,
        CancellationToken token)
    {
        var key = request.LoaderKey.ToLowerInvariant();
        var mcDir = request.MinecraftDir;
        var name = LoaderCatalog.LoaderNames.TryGetValue(key, out var n) ? n : key;

        var versionId = !string.IsNullOrEmpty(request.VersionId)
            ? request.VersionId!
            : LoaderCatalog.VersionFolderName(request.MinecraftVersion, key, request.LoaderVersion);
        if (versionId.Length == 0)
        {
            throw new LoaderInstallException(Localizer.Tr("加载器版本不对，拼不出目录名"));
        }

        // 已经装好过就别重跑一遍（安装器一次要 3 分钟）。
        // ⚠️ 用**容错查找**，别只认我们的约定名：Forge 安装器装出来的是
        //    `1.20.1-forge-47.4.10`，我们约定的却是 `1.20.1-Forge_47.4.10`，
        //    只认后者的话每次都当"没装过"，白跑一遍安装器。
        var alreadyId = string.IsNullOrEmpty(request.VersionId)
            ? LoaderInstaller.FindInstalledVersion(mcDir, request.MinecraftVersion, key, request.LoaderVersion)
            : (LoaderInstaller.VerifyInstalled(mcDir, versionId).Ok ? versionId : null);
        if (alreadyId is not null)
        {
            // ⚠️ 这里**不要**顺手把它改成我们约定的名字。
            // 设计说明见 docs/design-notes.md#mosslight-core-services-realloaderservice-cs
            return new LoaderInstallResult(true,
                Localizer.Tr("{name} {ver} 已经装好了", ("name", name), ("ver", request.LoaderVersion)),
                alreadyId);
        }

        var url = LoaderInstaller.InstallerUrl(
            request.MinecraftVersion, key, request.LoaderVersion,
            request.OptifineType, request.OptifinePatch);

        // ⚠️ **备用地址**：Forge 的主地址是 BMCLAPI 自己的 `/forge/download?...` 接口
        //    （没法用镜像映射到官方），它偶尔会 403 —— 而整个 Forge 安装就靠这一个 jar
        //    （真跑时 68 个文件里就它挂了，加载器直接装不了）。
        var altUrls = key == "forge"
            ? new List<string> { LoaderInstaller.ForgeMavenUrl(request.MinecraftVersion, request.LoaderVersion) }
            : null;

        var jarPath = Path.Combine(LoaderInstaller.InstallerDir(mcDir),
            LoaderInstaller.InstallerFileName(key, request.MinecraftVersion, request.LoaderVersion));

        progress?.Report(new LaunchProgress(LaunchStage.Downloading,
            Localizer.Tr("正在下载 {name} 的安装器…", ("name", name)), 0.05));

        var download = await RunDownloadsAsync(new List<InstallTask>
        {
            new(Url: url, Path: jarPath, Label: Path.GetFileName(jarPath),
                Kind: Localizer.Tr("安装器"), AltUrls: altUrls),
        }, progress, 0.05, 0.2, token).ConfigureAwait(false);

        if (!download.Ok)
        {
            return new LoaderInstallResult(false, download.Message, versionId, download);
        }

        token.ThrowIfCancellationRequested();

        // 别把半个 jar 当真（失败时下到的常常是 HTML 错误页改名成的 jar）
        if (!LoaderInstaller.IsInstallerJar(jarPath))
        {
            var message = Localizer.Tr("下到的安装器不像个安装器（可能是错误页），先停下了");
            progress?.Report(new LaunchProgress(LaunchStage.Failed, message, 0));
            return new LoaderInstallResult(false, message, versionId, download);
        }

        // 安装器要求游戏目录里有 launcher_profiles.json，少了它 Forge 直接失败
        if (LoaderInstaller.EnsureLauncherProfiles(mcDir))
        {
            progress?.Report(new LaunchProgress(LaunchStage.Preparing,
                Localizer.Tr("建了一份最小的 launcher_profiles.json（安装器要求有它）"), 0.22));
        }

        // 替安装器把它自己要下的库先下好（国内网络的救命稻草）
        var libraryTasks = LoaderInstaller.InstallerLibraryTasks(jarPath, mcDir);
        if (libraryTasks.Count > 0)
        {
            progress?.Report(new LaunchProgress(LaunchStage.Downloading,
                Localizer.Tr("先把安装器要的 {n} 个库用镜像下好（它自己下容易被掐断）",
                    ("n", libraryTasks.Count)), 0.25));
            // ⚠️ 预下载的结果**不能丢**：失败的话安装器会自己去下，
            // 设计说明见 docs/design-notes.md#mosslight-core-services-realloaderservice-cs
            var prep = await RunDownloadsAsync(libraryTasks, progress, 0.25, 0.4, token)
                .ConfigureAwait(false);
            if (!prep.Ok)
            {
                progress?.Report(new LaunchProgress(LaunchStage.Downloading,
                    Localizer.Tr("有 {n} 个库没预下载成功，安装器会自己再试一次",
                        ("n", prep.FilesFailed)), 0.4));
            }
        }

        token.ThrowIfCancellationRequested();

        // ---------- 跑安装器 ----------
        var javaPath = PickInstallerJava(request, key)
            ?? throw new LoaderSetupException(Localizer.Tr(
                "没找到能跑安装器的 Java（{name} 的安装器要 Java {n}）",
                ("name", name), ("n", InstallerJavaMajor(request, key))));

        progress?.Report(new LaunchProgress(LaunchStage.Launching,
            Localizer.Tr("正在跑 {name} 的安装器（这一步要几分钟）…", ("name", name)), 0.45, true));

        // 设计说明见 docs/design-notes.md#mosslight-core-services-realloaderservice-cs
        var logPath = LoaderInstaller.InstallerLogPath(_baseDir, key, request.MinecraftVersion);

        var result = await Task.Run(() => LoaderInstaller.RunInstaller(
            javaPath, jarPath, mcDir, key,
            onLine: line => progress?.Report(
                new LaunchProgress(LaunchStage.Launching, line, 0.45, true)),
            token: token,
            logPath: logPath), token).ConfigureAwait(false);

        if (result.ReturnCode != 0)
        {
            var reason = result.Error.Length > 0
                ? result.Error
                : Localizer.Tr("安装器退出码 {n}", ("n", result.ReturnCode));
            var message = Localizer.Tr("{name} 的安装器失败了：{why}", ("name", name), ("why", reason));
            progress?.Report(new LaunchProgress(LaunchStage.Failed, message, 0));
            // ⚠️ 把日志路径也返回给界面：只有一句话的话，用户没法再往下查
            return new LoaderInstallResult(false,
                Localizer.Tr("{msg}（安装器的完整输出在 {path}）", ("msg", message), ("path", logPath)),
                versionId, download);
        }

        // ---------- 校验 + 改名 ----------
        progress?.Report(new LaunchProgress(LaunchStage.CheckingFiles,
            Localizer.Tr("正在校验安装结果…"), 0.95));

        // 安装器自己起的名字可能跟我们想要的不一样（OptiFine 就是）
        var actualId = FindInstalledVersionId(mcDir, versionId, key, request.MinecraftVersion, request.LoaderVersion)
                       ?? versionId;

        var (ok, why) = LoaderInstaller.VerifyInstalled(mcDir, actualId);
        if (!ok)
        {
            var message = Localizer.Tr("装完了但版本不对：{why}", ("why", why));
            progress?.Report(new LaunchProgress(LaunchStage.Failed, message, 0));
            return new LoaderInstallResult(false, message, actualId, download);
        }

        if (actualId != versionId)
        {
            // ⚠️ 改名会把目录里的 `<旧名>.jar` 一起改掉 —— 少了这一步，
            //    启动时会**悄悄**退回父版本的原版 jar（OptiFine 就白装了）
            actualId = LoaderInstaller.RenameVersion(mcDir, actualId, versionId);
        }

        // NeoForge 1.20.5+ 的安装器不产出客户端 jar：给它补一份 `<实例名>.jar`，
        // 否则 FML 的 `-DignoreList=${version_name}.jar` 命中不了父版本那个 jar，
        // 启动时报「Modules minecraft and 1_.20.5 export package net.minecraft」
        // （见 EnsureInstanceJar 的注释）。
        if (LoaderInstaller.EnsureInstanceJar(mcDir, actualId, request.MinecraftVersion))
        {
            progress?.Report(new LaunchProgress(LaunchStage.Preparing,
                Localizer.Tr("把原版客户端 jar 复制成实例自己的（{id}.jar）", ("id", actualId)), 0.97));
        }

        progress?.Report(new LaunchProgress(LaunchStage.Done,
            Localizer.Tr("{name} {ver} 装好了", ("name", name), ("ver", request.LoaderVersion)), 1.0));

        return new LoaderInstallResult(true,
            Localizer.Tr("{name} {ver} 装好了（安装器跑了 {n} 秒）",
                ("name", name), ("ver", request.LoaderVersion), ("n", (int)result.Seconds)),
            actualId, download);
    }

    /// <summary>
    /// 安装器装完之后，磁盘上到底多出来哪个版本目录。
    ///
    /// 安装器起的名字不一定等于我们想要的那个（OptiFine 自己拼的名字带下划线、
    /// 版本号写法也可能不同），所以装完得回磁盘上找一遍。
    /// 优先找完全等于 <paramref name="wanted"/> 的，其次找"以加载器名开头"的。
    /// </summary>
    /// <summary>
    /// 安装器装完之后，磁盘上到底多出来哪个版本目录。
    ///
    /// 两种情况：
    ///   1. 就是我们想要的名字（`1.20.4-NeoForge_20.4.253`）→ 直接用它；
    ///   2. 安装器自己起的名字（`neoforge-20.4.253` / `1.20.1-forge-47.4.10`）→
    ///      交给 <see cref="LoaderInstaller.FindInstalledVersion"/>，那套匹配更宽
    ///      （第四遍扫描专门认"没有游戏版本前缀"的安装器名字，见那边的注释）。
    ///
    /// ⚠️ 以前这里自己抄了一份"以加载器名开头"的扫描，**只认带 mc 前缀的形状**，
    ///    于是 NeoForge 1.20.4 装完（目录叫 `neoforge-20.4.253`）会报
    ///    「装完了但版本不对：没有版本 JSON」—— 文件其实好好躺在那个目录里。
    ///    现在这里只管"我们的名字"，别的形状统一走 LoaderInstaller，
    ///    两边不会再各修各的。
    /// </summary>
    private static string? FindInstalledVersionId(
        string mcDir, string wanted, string key, string mcVersion, string loaderVersion)
    {
        var versionsDir = Path.Combine(mcDir, "versions");

        // ⚠️ "完全等于"也要看到 JSON 才算数：同名的空目录 / 半成品目录是存在的
        // 设计说明见 docs/design-notes.md#mosslight-core-services-realloaderservice-cs
        var wantedJson = Path.Combine(versionsDir, wanted, wanted + ".json");
        if (File.Exists(wantedJson))
        {
            return wanted;
        }

        return LoaderInstaller.FindInstalledVersion(mcDir, mcVersion, key, loaderVersion);
    }
    /// <summary>设计说明见 docs/design-notes.md#mosslight-core-services-realloaderservice-cs</summary>
    private string? PickInstallerJava(LoaderInstallRequest request, string key)
    {
        var need = InstallerJavaMajor(request, key);
        var scanner = new JavaScanner(AppSettings.ResolveConfigDir(AppContext.BaseDirectory));
        var probes = scanner.FindJavas(new[] { request.MinecraftDir });

        var (picked, _) = JavaScanner.PickJava(probes, need, _settings.GetString("java_path") ?? string.Empty);
        return picked?.Path;
    }

    private static int InstallerJavaMajor(LoaderInstallRequest request, string key)
    {
        if (key == "optifine")
        {
            return 8;
        }
        var vj = VersionJsonLoader.TryLoad(request.MinecraftDir, request.MinecraftVersion);
        return Math.Max(JavaScanner.RequiredMajor(vj, 8), 8);
    }

    // ============================================================
    //  下载一批任务
    // ============================================================

    /// <summary>把一批任务交给下载引擎跑完，边跑边报进度。</summary>
    private async Task<InstallResult> RunDownloadsAsync(
        List<InstallTask> tasks,
        IProgress<LaunchProgress>? progress,
        double from,
        double to,
        CancellationToken token)
    {
        if (tasks.Count == 0)
        {
            return new InstallResult(true, Localizer.Tr("没有要下载的文件"), FilesDownloaded: 0);
        }

        var manager = new DownloadManager(
            maxWorkers: _settings.EffectiveThreads,
            source: _settings.DownloadSource);

        foreach (var task in tasks)
        {
            manager.Add(task.Url, task.Path, task.Sha1, task.Size, task.Label, task.Kind, task.AltUrls);
        }
        manager.Start();

        while (!manager.WaitAll(TimeSpan.FromMilliseconds(200)))
        {
            token.ThrowIfCancellationRequested();
            var snap = manager.Snapshot();
            var span = to - from;
            progress?.Report(new LaunchProgress(
                LaunchStage.Downloading,
                Localizer.Tr("{done}/{total} 个文件 · {size} · {speed}",
                    ("done", snap.Done), ("total", snap.Count),
                    ("size", DownloadManager.HumanSize(snap.Bytes)),
                    ("speed", DownloadManager.HumanSpeed(snap.Speed))),
                Math.Clamp(from + (snap.Percent ?? 0) * span, 0, to)));
        }

        var final = manager.Snapshot();
        var failed = final.Tasks.Where(t => t.State == DownloadManager.StateFailed).ToList();
        if (failed.Count > 0)
        {
            var message = Localizer.Tr("有 {n} 个文件没下下来", ("n", failed.Count));
            return new InstallResult(false, message, null,
                final.Done - failed.Count, failed.Count,
                failed.Select(t => t.Error.Length > 0 ? $"{t.Label}: {t.Error}" : t.Label).ToList());
        }

        return new InstallResult(true,
            Localizer.Tr("{n} 个文件下好了", ("n", final.Count)),
            null, final.Count, 0);
    }

    /// <summary>设计说明见 docs/design-notes.md#mosslight-core-services-realloaderservice-cs</summary>
    private static long LongValue(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return 0;
        }
        try
        {
            return value.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.Number => value.GetValue<long>(),
                System.Text.Json.JsonValueKind.String
                    => long.TryParse(value.GetValue<string>(), out var parsed) ? parsed : 0,
                _ => 0,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            // 数字太大 / 类型怪：当成"不知道多大"，下载器会按实际字节数算
            return 0;
        }
    }

    /// <summary>
    /// 从 JSON 里读一个字符串（不是字符串就给空串，不抛）。
    /// </summary>
    private static string StringValue(JsonNode? node)
        => node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? v.GetValue<string>()
            : string.Empty;

    // ============================================================
    //  Fabric API
    // ============================================================

    /// <summary>
    /// 给这个游戏版本找一份 Fabric API（Modrinth 上那个项目）的下载任务。
    /// 取不到就返回 null —— 它是"顺手装的"，不该让加载器安装失败。
    /// </summary>
    private static async Task<InstallTask?> FabricApiTaskAsync(
        string mcVersion, string modsDir, CancellationToken token)
    {
        var all = await ModrinthApi.GetProjectVersionsAsync(FabricApiProject, token)
            .ConfigureAwait(false);
        // ⚠️ 筛不出东西就返回 null，**别退而求其次** —— 装错加载器的 mod
        //    只会让游戏起不来，比"没得选"更糟
        var version = ModrinthApi
            .FilterVersions(all, mcVersion, "fabric")
            .FirstOrDefault();
        if (version is null)
        {
            return null;
        }
        return ModrinthApi.TaskFromVersion(version, modsDir);
    }

    // ============================================================
    //  LiteLoader：自己拼版本 JSON（官方安装器已经下不到了）
    // ============================================================

    /// <summary>
    /// 装 LiteLoader：**不跑安装器**，照 versions.json 里那份数据拼出启动器要的版本 JSON。
    ///
    /// 跟 Fabric 一样是"写出 JSON + 下几个库"，区别只在 JSON 是我们拼的
    /// （`inheritsFrom` 原版 + `--tweakClass`），理由见 `LiteLoaderCatalog` 的类注释。
    /// </summary>
    private async Task<LoaderInstallResult> InstallLiteLoaderAsync(
        LoaderInstallRequest request, IProgress<LaunchProgress>? progress, CancellationToken token)
    {
        var mcVersion = request.MinecraftVersion;
        var loaderVersion = request.LoaderVersion;
        var mcDir = request.MinecraftDir;

        progress?.Report(new LaunchProgress(LaunchStage.Preparing,
            Localizer.Tr("正在取 LiteLoader {ver} 的信息…", ("ver", loaderVersion)), 0.08, true));

        // 重新拉一份列表（安装时不能信界面那份："点什么装什么"要拿真实数据对一遍）
        var json = await GameInstaller
            .HttpJsonAsync(LiteLoaderCatalog.VersionsUrl, token).ConfigureAwait(false)
            ?? throw new LoaderSetupException(Localizer.Tr("拿不到 LiteLoader 的版本列表（网络问题）"));

        var versions = LiteLoaderCatalog.ParseVersions(json, mcVersion);
        if (versions.Count == 0)
        {
            throw new LoaderSetupException(Localizer.Tr(
                "LiteLoader 没有 {mc} 的正式版（它只发到 1.10.2，之后那几个只有测试版）",
                ("mc", mcVersion)));
        }

        var picked = LiteLoaderCatalog.PickArtefact(json, mcVersion, loaderVersion)
                     ?? throw new LoaderSetupException(Localizer.Tr(
                         "LiteLoader 没有 {ver} 这个版本（{mc} 只到 {latest}）",
                         ("ver", loaderVersion), ("mc", mcVersion), ("latest", versions[0].Version)));

        // 原版的 minecraftArguments 要原样带过去（见 LiteLoaderCatalog.BuildVersionJson 的注释）
        var vanillaJson = VersionJsonLoader.TryLoad(mcDir, mcVersion)
                          ?? throw new LoaderSetupException(Localizer.Tr(
                              "找不到原版 {mc} 的版本 JSON，LiteLoader 装不上", ("mc", mcVersion)));
        var parentArgs = vanillaJson.Raw["minecraftArguments"]?.GetValue<string>() ?? string.Empty;
        if (parentArgs.Length == 0)
        {
            throw new LoaderSetupException(Localizer.Tr(
                "{mc} 的版本 JSON 里没有 minecraftArguments（不是 1.12.2 及更早的格式），LiteLoader 装不了",
                ("mc", mcVersion)));
        }

        var versionId = !string.IsNullOrWhiteSpace(request.VersionId)
            ? request.VersionId!
            : LoaderCatalog.VersionFolderName(mcVersion, "liteloader", loaderVersion);

        var vj = LiteLoaderCatalog.BuildVersionJson(mcVersion, versionId, picked, parentArgs);
        var versionDir = Path.Combine(mcDir, "versions", versionId);
        GameInstaller.WriteVersionJson(vj, versionDir, versionId);

        token.ThrowIfCancellationRequested();

        // 库：数据里那几条（launchwrapper / asm-all …）+ LiteLoader 自己的 jar。
        // ⚠️ LiteLoader 那条**必须**按 `downloads.artifact` 走：它的文件名跟 maven
        //    坐标推出来的不一样（`1.7.10_04` 版本号 + `liteloader-1.7.10.jar` 文件名），
        //    靠 name 推会去下一个不存在的文件。
        progress?.Report(new LaunchProgress(LaunchStage.Downloading,
            Localizer.Tr("正在下载 LiteLoader…"), 0.2, true));
        var tasks = new List<InstallTask>();
        foreach (var node in vj["libraries"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject lib)
            {
                continue;
            }

            var artifact = (lib["downloads"] as JsonObject)?["artifact"] as JsonObject;
            var artifactUrl = artifact?["url"]?.GetValue<string>() ?? string.Empty;
            var artifactPath = artifact?["path"]?.GetValue<string>() ?? string.Empty;
            if (artifactUrl.Length > 0 && artifactPath.Length > 0)
            {
                tasks.Add(new InstallTask(
                    Url: artifactUrl,
                    Path: Path.Combine(mcDir, "libraries", artifactPath.Replace('/', Path.DirectorySeparatorChar)),
                    Sha1: StringValue(artifact?["sha1"]),
                    // ⚠️ 别直接 `GetValue<long>()`：JSON 里写成 `0` 的是 Int32，
                    //    这么读会抛"Int32 转 Int64 失败"（真踩到过，整条安装直接失败）
                    Size: LongValue(artifact?["size"]),
                    Label: Path.GetFileName(artifactPath),
                    Kind: Localizer.Tr("库")));
                continue;
            }

            var name = lib["name"]?.GetValue<string>() ?? string.Empty;
            var (rel, _) = LaunchRules.MavenToPath(name);
            if (rel is null)
            {
                continue;
            }
            var baseUrl = (lib["url"]?.GetValue<string>() ?? "https://libraries.minecraft.net/").TrimEnd('/');
            tasks.Add(new InstallTask(
                Url: $"{baseUrl}/{rel}",
                Path: Path.Combine(mcDir, "libraries", rel.Replace('/', Path.DirectorySeparatorChar)),
                Sha1: string.Empty,
                Size: 0,
                Label: Path.GetFileName(rel),
                Kind: Localizer.Tr("库")));
        }

        var download = await RunDownloadsAsync(tasks, progress, 0.2, 0.98, token).ConfigureAwait(false);
        if (!download.Ok)
        {
            return new LoaderInstallResult(false,
                Localizer.Tr("LiteLoader 的文件没下全：{msg}", ("msg", download.Message)),
                versionId, download);
        }

        return new LoaderInstallResult(true,
            Localizer.Tr("LiteLoader {ver} 装好了（{id}）", ("ver", loaderVersion), ("id", versionId)),
            versionId, download);
    }

    // ============================================================
    //  LabyMod：照 PCL 的配方拼版本 JSON（剔隔离 LWJGL + 追加它的库 + 客户端 jar）
    // ============================================================

    /// <summary>
    /// 装 LabyMod：**不跑安装器**，照 PCL2-CE 的 `ModDownloadLib.cs` 那套拼版本 JSON。
    ///
    /// 步骤（细节和出处见 <see cref="LabyModCatalog.BuildVersionJson"/> 的注释）：
    ///   1. 按游戏版本取 manifest 里那份 JSON（`customManifestUrl`）；
    ///   2. 拉 `libraries/{channel}.json`（108 条库，**含 fork 过的 launchwrapper**）
    ///      和 `manifest/{channel}/latest.json`（commit / sha1 / size）；
    ///   3. 拼出来落盘 → 资源索引 → 规划任务 → 下载。
    ///
    /// ⚠️ 这条路**不需要原版**：那份 JSON 自带 `downloads.client` 和 `assetIndex`，
    ///    库也是它自己列全的（原版的 25/67 条 + LabyMod 的 108 条）。
    /// ⚠️ 它要 **Java 21+**、而且启动时**必须是正版登录**（LabyMod 服务端要验证）——
    ///    这两点提示放在装完的那句话里。
    /// </summary>
    private async Task<LoaderInstallResult> InstallLabyModAsync(
        LoaderInstallRequest request, IProgress<LaunchProgress>? progress, CancellationToken token)
    {
        var mcVersion = request.MinecraftVersion;
        var loaderVersion = request.LoaderVersion;
        var mcDir = request.MinecraftDir;

        var versionId = !string.IsNullOrWhiteSpace(request.VersionId)
            ? request.VersionId!
            : LoaderCatalog.VersionFolderName(mcVersion, "labymod", loaderVersion);

        // ---------- 1. 两份全局清单 + 这个游戏版本那份 ----------
        progress?.Report(new LaunchProgress(LaunchStage.Preparing,
            Localizer.Tr("正在获取 LabyMod {ver} 的清单…", ("ver", loaderVersion)), 0.05, true));

        // 频道：界面从版本行里带下来的（production / snapshot）；
        // 没给就按 production 走（PCL 默认也是 production）
        var channel = string.IsNullOrWhiteSpace(request.Channel)
            ? LabyModCatalog.ProductionChannel
            : request.Channel!;

        var manifest = await GameInstaller.HttpJsonAsync(
            channel == LabyModCatalog.SnapshotChannel
                ? LabyModCatalog.SnapshotManifest
                : LabyModCatalog.ProductionManifest, token).ConfigureAwait(false)
            ?? throw new LoaderSetupException(Localizer.Tr("拿不到 LabyMod 的版本列表（网络问题）"));

        var rows = LabyModCatalog.ParseManifest(manifest, channel, mcVersion);
        var row = rows.FirstOrDefault(r => string.Equals(r.Version, loaderVersion, StringComparison.Ordinal))
                  ?? throw new LoaderSetupException(Localizer.Tr(
                      "LabyMod 没有 {ver} 这个版本（{mc} 只到 {latest}）",
                      ("ver", loaderVersion), ("mc", mcVersion),
                      ("latest", rows.Count > 0 ? rows[0].Version : "—")));

        var versionJsonUrl = row.FileName ?? string.Empty;
        if (versionJsonUrl.Length == 0)
        {
            throw new LoaderSetupException(Localizer.Tr(
                "LabyMod 的清单里没有 {mc} 的版本 JSON 地址", ("mc", mcVersion)));
        }

        var versionJson = await GameInstaller.HttpJsonAsync(versionJsonUrl, token).ConfigureAwait(false)
                          ?? throw new LoaderSetupException(Localizer.Tr(
                              "拿不到 LabyMod 的版本 JSON（网络问题）"));

        var librariesJson = await GameInstaller.HttpJsonAsync(
            LabyModCatalog.LibrariesUrl(channel), token).ConfigureAwait(false)
            ?? throw new LoaderSetupException(Localizer.Tr("拿不到 LabyMod 的库清单（网络问题）"));

        var latestJson = (await GameInstaller.HttpJsonAsync(
            channel == LabyModCatalog.SnapshotChannel
                ? LabyModCatalog.SnapshotManifest
                : LabyModCatalog.ProductionManifest, token).ConfigureAwait(false)) ?? manifest;

        // ---------- 2. 拼 JSON 落盘 ----------
        progress?.Report(new LaunchProgress(LaunchStage.Preparing,
            Localizer.Tr("正在拼 LabyMod 的版本信息…"), 0.15, true));

        var vj = LabyModCatalog.BuildVersionJson(versionJson, librariesJson, latestJson, channel, versionId);
        var versionDir = Path.Combine(mcDir, "versions", versionId);
        GameInstaller.WriteVersionJson(vj, versionDir, versionId);

        token.ThrowIfCancellationRequested();

        // ---------- 3. 资源索引 + 任务 ----------
        progress?.Report(new LaunchProgress(LaunchStage.CheckingFiles,
            Localizer.Tr("正在获取资源索引…"), 0.2, true));
        var assetIndex = await GameInstaller.LoadAssetIndexAsync(mcDir, vj, null, token)
            .ConfigureAwait(false);
        var plan = GameInstaller.PlanVersion(vj, mcDir, assetIndex: assetIndex);
        var tasks = plan.Tasks().ToList();
        if (tasks.Count == 0)
        {
            throw new LoaderSetupException(Localizer.Tr(
                "LabyMod 的版本信息里没有任何要下载的文件，装不了"));
        }

        progress?.Report(new LaunchProgress(LaunchStage.Downloading,
            Localizer.Tr("开始下载 LabyMod 的 {n} 个文件…", ("n", tasks.Count)), 0.25, false));
        var download = await RunDownloadsAsync(tasks, progress, 0.25, 0.98, token).ConfigureAwait(false);
        if (!download.Ok)
        {
            return new LoaderInstallResult(false,
                Localizer.Tr("LabyMod 的文件没下全：{msg}", ("msg", download.Message)),
                versionId, download);
        }

        return new LoaderInstallResult(true,
            Localizer.Tr("LabyMod {ver} 装好了（{id}）· 启动要 Java 21+ 和正版账号",
                ("ver", loaderVersion), ("id", versionId)),
            versionId, download);
    }

    // ============================================================
    //  Cleanroom：从官方安装器里取版本 JSON，自己落盘
    // ============================================================

    /// <summary>
    /// 装 Cleanroom：下安装器 → 抽出 `version.json` → 补地址 → 落盘 → 下文件。
    ///
    /// **不跑安装器**（它是自己的 `com.cleanroommc.installer.Main`，没有可抄的
    /// 无人值守参数），理由和三个坑都在 `CleanroomCatalog` 的类注释里。
    /// ⚠️ 这条路**不需要原版**：它的 version.json 不 `inheritsFrom`，库和客户端 jar
    ///    都是自己带的（`downloads.client` 直接指 1.12.2 的客户端 jar）。
    /// </summary>
    private async Task<LoaderInstallResult> InstallCleanroomAsync(
        LoaderInstallRequest request, IProgress<LaunchProgress>? progress, CancellationToken token)
    {
        var mcVersion = request.MinecraftVersion;
        var loaderVersion = request.LoaderVersion;
        var mcDir = request.MinecraftDir;

        if (!CleanroomCatalog.Supports(mcVersion))
        {
            throw new LoaderSetupException(Localizer.Tr(
                "Cleanroom 只支持 Minecraft {only}（它没有 {mc} 的版本）",
                ("only", CleanroomCatalog.OnlyMinecraftVersion), ("mc", mcVersion)));
        }

        var versionId = !string.IsNullOrWhiteSpace(request.VersionId)
            ? request.VersionId!
            : LoaderCatalog.VersionFolderName(mcVersion, "cleanroom", loaderVersion);

        // ---------- 1. 下安装器（15 MB）拿 version.json ----------
        progress?.Report(new LaunchProgress(LaunchStage.Downloading,
            Localizer.Tr("正在下载 Cleanroom {ver} 的安装包…", ("ver", loaderVersion)), 0.05, true));

        var tempDir = LoaderInstaller.InstallerDir(mcDir);
        Directory.CreateDirectory(tempDir);
        var installerPath = Path.Combine(tempDir, $"cleanroom-{loaderVersion}-installer.jar");
        var installerDownload = await RunDownloadsAsync(new List<InstallTask>
        {
            new(Url: CleanroomCatalog.InstallerUrl(loaderVersion), Path: installerPath,
                Label: Path.GetFileName(installerPath), Kind: Localizer.Tr("安装器")),
        }, progress, 0.05, 0.15, token).ConfigureAwait(false);
        if (!installerDownload.Ok)
        {
            return new LoaderInstallResult(false,
                Localizer.Tr("Cleanroom 的安装包没下下来：{msg}", ("msg", installerDownload.Message)),
                versionId, installerDownload);
        }

        token.ThrowIfCancellationRequested();

        progress?.Report(new LaunchProgress(LaunchStage.Preparing,
            Localizer.Tr("正在从安装包里取出 Cleanroom 的版本信息…"), 0.16, true));

        var vj = CleanroomCatalog.ReadVersionJson(installerPath);
        var fixedCount = CleanroomCatalog.PrepareVersionJson(vj, loaderVersion, versionId);
        _ = fixedCount;   // 见 CleanroomCatalog.PrepareVersionJson：不用判断成功与否

        var versionDir = Path.Combine(mcDir, "versions", versionId);
        GameInstaller.WriteVersionJson(vj, versionDir, versionId);

        // 安装器本身用完就删（15 MB，留着没用；universal jar 已经在库任务里了）
        try
        {
            File.Delete(installerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;   // 删不掉不算失败（下次装会被覆盖）
        }

        token.ThrowIfCancellationRequested();

        // ---------- 2. 资源索引 + 文件清单 ----------
        progress?.Report(new LaunchProgress(LaunchStage.CheckingFiles,
            Localizer.Tr("正在获取资源索引…"), 0.2, true));
        var assetIndex = await GameInstaller.LoadAssetIndexAsync(mcDir, vj, null, token)
            .ConfigureAwait(false);
        var plan = GameInstaller.PlanVersion(vj, mcDir, assetIndex: assetIndex);
        var tasks = plan.Tasks().ToList();

        if (tasks.Count == 0)
        {
            throw new LoaderSetupException(Localizer.Tr(
                "Cleanroom 的 version.json 里没有任何要下载的文件，装不了"));
        }

        // ---------- 3. 下载 ----------
        progress?.Report(new LaunchProgress(LaunchStage.Downloading,
            Localizer.Tr("开始下载 Cleanroom 的 {n} 个文件…", ("n", tasks.Count)), 0.25, false));
        var download = await RunDownloadsAsync(tasks, progress, 0.25, 0.98, token).ConfigureAwait(false);
        if (!download.Ok)
        {
            return new LoaderInstallResult(false,
                Localizer.Tr("Cleanroom 的文件没下全：{msg}", ("msg", download.Message)),
                versionId, download);
        }

        return new LoaderInstallResult(true,
            Localizer.Tr("Cleanroom {ver} 装好了（{id}）· {note}",
                ("ver", loaderVersion), ("id", versionId),
                ("note", CleanroomCatalog.JavaNoteFor(loaderVersion))),
            versionId, download);
    }
}

/// <summary>加载器装不了 / 数据不对（信息是给人看的，会显示在界面上）。</summary>
public sealed class LoaderInstallException : Exception
{
    public LoaderInstallException(string message) : base(message)
    {
    }
}
