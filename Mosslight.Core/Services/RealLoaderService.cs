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
    private static readonly string[] ProfileLoaders = { "fabric", "quilt" };

    /// <summary>只有安装器 jar 的（要 Java 跑 installer）。</summary>
    private static readonly string[] InstallerLoaders = { "forge", "neoforge", "optifine" };

    private readonly AppSettings _settings;
    private readonly RealGameService _games;
    private readonly RealDownloadService _downloads;
    private readonly VersionManifest _manifest;

    public RealLoaderService(
        AppSettings settings,
        RealGameService games,
        RealDownloadService downloads,
        string configDir)
    {
        _settings = settings;
        _games = games;
        _downloads = downloads;
        _manifest = new VersionManifest(configDir);
    }

    public bool IsPrototype => false;

    public async Task<IReadOnlyList<LoaderRow>> CatalogAsync(
        string minecraftVersion, CancellationToken token = default)
        => await LoaderCatalog.LoaderRowsAsync(minecraftVersion, token).ConfigureAwait(false);

    public bool CanInstall(string loaderKey)
        => ProfileLoaders.Contains((loaderKey ?? string.Empty).ToLowerInvariant())
           || InstallerLoaders.Contains((loaderKey ?? string.Empty).ToLowerInvariant());

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
            Report(LaunchStage.Preparing, Localizer.Tr("正在确认原版 {name} 已安装…", ("name", mcVersion)), 0.02);
            var vanillaReady = await EnsureVanillaAsync(mcVersion, mcDir, progress, token)
                .ConfigureAwait(false);
            if (vanillaReady is not null)
            {
                return vanillaReady;
            }

            token.ThrowIfCancellationRequested();

            // ---------- 2. 两条路 ----------
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

        // 已经装好过就别重跑一遍（安装器一次要 3 分钟）
        var (already, _) = LoaderInstaller.VerifyInstalled(mcDir, versionId);
        if (already)
        {
            return new LoaderInstallResult(true,
                Localizer.Tr("{name} {ver} 已经装好了", ("name", name), ("ver", request.LoaderVersion)),
                versionId);
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
            await RunDownloadsAsync(libraryTasks, progress, 0.25, 0.4, token).ConfigureAwait(false);
        }

        token.ThrowIfCancellationRequested();

        // ---------- 跑安装器 ----------
        var javaPath = PickInstallerJava(request, key)
            ?? throw new LoaderSetupException(Localizer.Tr(
                "没找到能跑安装器的 Java（{name} 的安装器要 Java {n}）",
                ("name", name), ("n", InstallerJavaMajor(request, key))));

        progress?.Report(new LaunchProgress(LaunchStage.Launching,
            Localizer.Tr("正在跑 {name} 的安装器（这一步要几分钟）…", ("name", name)), 0.45, true));

        var result = await Task.Run(() => LoaderInstaller.RunInstaller(
            javaPath, jarPath, mcDir, key,
            onLine: line => progress?.Report(
                new LaunchProgress(LaunchStage.Launching, line, 0.45, true)),
            token: token), token).ConfigureAwait(false);

        if (result.ReturnCode != 0)
        {
            var reason = result.Error.Length > 0
                ? result.Error
                : Localizer.Tr("安装器退出码 {n}", ("n", result.ReturnCode));
            var message = Localizer.Tr("{name} 的安装器失败了：{why}", ("name", name), ("why", reason));
            progress?.Report(new LaunchProgress(LaunchStage.Failed, message, 0));
            return new LoaderInstallResult(false, message, versionId, download);
        }

        // ---------- 校验 + 改名 ----------
        progress?.Report(new LaunchProgress(LaunchStage.CheckingFiles,
            Localizer.Tr("正在校验安装结果…"), 0.95));

        // 安装器自己起的名字可能跟我们想要的不一样（OptiFine 就是）
        var actualId = FindInstalledVersionId(mcDir, versionId, key, request.MinecraftVersion)
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
    private static string? FindInstalledVersionId(string mcDir, string wanted, string key, string mcVersion)
    {
        var versionsDir = Path.Combine(mcDir, "versions");
        if (!Directory.Exists(versionsDir))
        {
            return null;
        }

        List<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(versionsDir)
                .Select(Path.GetFileName)
                .Where(x => !string.IsNullOrEmpty(x))
                .Select(x => x!)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (folders.Contains(wanted))
        {
            return wanted;
        }

        // "以加载器名开头"（`-` 后面那截）—— 跟文件夹名约定对得上
        var loaderName = LoaderCatalog.FolderNames.TryGetValue(key, out var n) ? n : key;
        var candidates = folders
            .Where(f =>
            {
                var dash = f.IndexOf('-');
                if (dash < 0) return false;
                var tail = f[(dash + 1)..];
                return tail.StartsWith(loaderName, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        // 只认有 json 的那些（半成品目录不算）
        var ready = candidates
            .Where(f => File.Exists(Path.Combine(versionsDir, f, f + ".json")))
            .ToList();

        return ready.Count > 0 ? ready[0] : null;
    }

    /// <summary>给安装器挑个 Java。</summary>
    ///
    /// ⚠️ 要求的最低版本**跟着游戏版本走**：1.20.1 的 Forge 安装器要 Java 17
    ///    才跑得动，拿 Java 8 去跑会 `UnsupportedClassVersionError`。
    ///    但也不能低于 8（安装器自己编译目标是 8）。
    ///
    /// ⚠️ **OptiFine 是例外，而且方向相反**：它那个安装器是 Java 8 编的，
    ///    在 Java 17 上会**卡住不返回**（实测：10 分钟一行输出都没有）。
    ///    所以 OptiFine 一律优先 Java 8，哪怕游戏本身要 17 ——
    ///    安装器只是拿来打补丁的，跟游戏跑在哪个 Java 上无关。
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
            useMirror: true);

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
}

/// <summary>加载器装不了 / 数据不对（信息是给人看的，会显示在界面上）。</summary>
public sealed class LoaderInstallException : Exception
{
    public LoaderInstallException(string message) : base(message)
    {
    }
}
