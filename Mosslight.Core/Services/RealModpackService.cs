using System.Text;
using Mosslight.Core.Localization;
using Mosslight.Core.Services.Engine;
using Mosslight.Core.Settings;

namespace Mosslight.Core.Services;

// ============================================================================
//  整合包（`.mrpack`）安装的真实现
//
//  真正干活的在 <see cref="ModpackInstaller"/>（解析 / 路径安全 / 展开覆盖层），
//  这里只负责编排那条流水线：
//
//      读 .mrpack → 算计划 → 建实例目录 → 下 files[] → 展开 overrides/
//        → 装加载器（写一份真能启动的 profile）→ 把整合包元数据塞进版本 JSON
//
//  ## 落盘约定
//
//      versions/<实例名>/<实例名>.json          版本 JSON（加载器 profile + modpack 段）
//      versions/<实例名>/mods/...               整合包带的 mod（**版本隔离**）
//      versions/<实例名>/config/...             整合包带的配置
//
//  ⚠️ 实例名要**先消毒**再用：真实整合包的名字里什么都有
//     （`Cobblemon Official Modpack [Fabric]`、`a/b:c`），直接拿去建目录会失败。
// ============================================================================

/// <summary>真实的整合包服务。</summary>
public sealed class RealModpackService : IModpackService
{
    private readonly AppSettings _settings;
    private readonly RealGameService _games;
    private readonly ILoaderService _loaders;
    private readonly IDownloadService _downloads;

    public RealModpackService(
        AppSettings settings,
        RealGameService games,
        ILoaderService loaders,
        IDownloadService downloads)
    {
        _settings = settings;
        _games = games;
        _loaders = loaders;
        _downloads = downloads;
    }

    public bool IsPrototype => false;

    // ---------- 预览 ----------

    public ModpackPlan? Inspect(byte[] blob, out string error)
    {
        try
        {
            var plan = ModpackInstaller.PlanFromBytes(blob);
            error = string.Empty;
            return plan;
        }
        catch (ModpackException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    // ---------- 安装 ----------

    public async Task<ModpackInstallResult> InstallAsync(
        ModpackInstallRequest request,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken token = default)
    {
        void Report(LaunchStage stage, string message, double fraction = 0, bool indeterminate = false)
            => progress?.Report(new LaunchProgress(stage, message, fraction, indeterminate));

        try
        {
            return await Task.Run(
                () => Install(request, progress, Report, token),
                token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ModpackInstallResult(false, Localizer.Tr("已取消"));
        }
        catch (ModpackException ex)
        {
            Report(LaunchStage.Failed, ex.Message, 0);
            return new ModpackInstallResult(false, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.Text.Json.JsonException or InvalidOperationException
                                      or NotSupportedException)
        {
            var message = Localizer.Tr("装整合包失败：{err}", ("err", ex.Message));
            Report(LaunchStage.Failed, message, 0);
            return new ModpackInstallResult(false, message);
        }
    }

    /// <summary>
    /// 安装的**入口**：只有 URL（从 Modrinth 的「整合包」栏直接点下载）时先落成临时文件，
    /// 再交给 <see cref="InstallCore"/> 走同一条流水线。
    ///
    /// 为什么把"下载"放在这里、而不是让界面自己下：
    ///   · 临时文件的**清理**必须跟着安装走（装完 / 失败 / 取消都要删）——
    ///     放在界面上，用户中途关页面就会留下一堆 .mrpack 垃圾；
    ///   · 进度回调（下载百分比）本来就在这条流水线上，拆出去会让界面收两份进度。
    ///
    /// ⚠️ `MrpackUrl` 是**追加在 record 末尾的可选字段**，不给就是老行为
    ///    （只认本地路径），"从本地选一个 .mrpack 安装"那条路一个字没变。
    /// </summary>
    private ModpackInstallResult Install(
        ModpackInstallRequest request,
        IProgress<LaunchProgress>? progress,
        Action<LaunchStage, string, double, bool> report,
        CancellationToken token)
    {
        var mrpackPath = request.MrpackPath;
        var temporary = false;

        if (string.IsNullOrWhiteSpace(mrpackPath) && !string.IsNullOrWhiteSpace(request.MrpackUrl))
        {
            var fetched = DownloadMrpackAsync(request.MrpackUrl!, report, token, out var error);
            if (fetched is null)
            {
                report(LaunchStage.Failed, error, 0, false);
                return new ModpackInstallResult(false, error);
            }
            mrpackPath = fetched;
            temporary = true;
        }

        try
        {
            return InstallCore(request, mrpackPath, progress, report, token);
        }
        finally
        {
            // 临时文件不许留下来：一个整合包动辄几十上百 MB
            if (temporary)
            {
                try
                {
                    File.Delete(mrpackPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Debug.WriteLine($"[Modpack] 临时文件没删掉：{mrpackPath}（{ex.Message}）");
                }
            }
        }
    }

    /// <summary>
    /// 把一个 `.mrpack` 下到临时文件。失败返回 null，原因写在 <paramref name="error"/> 里。
    ///
    /// ⚠️ 文件名前面挂一段随机串：同一个整合包连点两次、或者两个整合包同名，
    ///    固定文件名会让两次下载**写同一个文件**（DownloadManager 是按落点去重的，
    ///    那样第二次会直接被当成"已经在下"而静默复用第一份）。
    /// </summary>
    private static string? DownloadMrpackAsync(
        string url, Action<LaunchStage, string, double, bool> report, CancellationToken token,
        out string error)
    {
        error = string.Empty;
        string dest;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Mosslight", "modpacks");
            Directory.CreateDirectory(dir);
            var name = Path.GetFileName(new Uri(url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "modpack.mrpack";
            }
            dest = Path.Combine(dir, Guid.NewGuid().ToString("N")[..8] + "-" + name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or UriFormatException or ArgumentException)
        {
            error = Localizer.Tr("整合包下载失败：{why}", ("why", ex.Message));
            return null;
        }

        try
        {
            report(LaunchStage.Downloading, Localizer.Tr("正在下载整合包…"), 0.01, true);

            // ⚠️ 这里下的是 `.mrpack` 本身（Modrinth 的 CDN），**没有 BMCLAPI 镜像**，
            //    所以下载源策略对它没意义 —— 固定用默认策略就行。
            var manager = new DownloadManager(maxWorkers: 1);
            manager.Add(url, dest, string.Empty, 0, Path.GetFileName(dest), Localizer.Tr("整合包"));
            manager.Start();

            while (!manager.WaitAll(TimeSpan.FromMilliseconds(200)))
            {
                token.ThrowIfCancellationRequested();
                var snap = manager.Snapshot();
                report(LaunchStage.Downloading,
                    Localizer.Tr("正在下载整合包：{size} · {speed}",
                        ("size", DownloadManager.HumanSize(snap.Bytes)),
                        ("speed", DownloadManager.HumanSpeed(snap.Speed))),
                    0.01 + (snap.Percent ?? 0) * 0.04, false);
            }

            var final = manager.Snapshot();
            var failed = final.Tasks.FirstOrDefault(t => t.State == DownloadManager.StateFailed);
            if (failed is not null)
            {
                error = Localizer.Tr("整合包下载失败：{why}",
                    ("why", failed.Error.Length > 0 ? failed.Error : failed.State));
                return null;
            }
            return dest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidOperationException)
        {
            error = Localizer.Tr("整合包下载失败：{why}", ("why", ex.Message));
            return null;
        }
    }

    private ModpackInstallResult InstallCore(
        ModpackInstallRequest request,
        string mrpackPath,
        IProgress<LaunchProgress>? progress,
        Action<LaunchStage, string, double, bool> report,
        CancellationToken token)
    {
        // 本地小包装：`report` 是四参数的（最后那个是"不确定进度"），
        // 这里给个默认值，省得每个调用点都写一遍 `false`
        void Say(LaunchStage stage, string message, double fraction = 0, bool indeterminate = false)
            => report(stage, message, fraction, indeterminate);

        // ---------- 1. 读包 + 算计划 ----------
        Say(LaunchStage.Preparing, Localizer.Tr("正在读整合包…"), 0.02, true);

        if (!File.Exists(mrpackPath))
        {
            var missing = Localizer.Tr("找不到整合包文件：{path}", ("path", mrpackPath));
            Say(LaunchStage.Failed, missing, 0);
            return new ModpackInstallResult(false, missing);
        }

        var blob = File.ReadAllBytes(mrpackPath);
        var plan = ModpackInstaller.PlanFromBytes(blob);

        // ⚠️ 实例名要先消毒 —— 真实整合包的名字直接拿去建目录在 Windows 上会失败
        var instanceName = ModpackInstaller.SanitizeInstanceName(
            string.IsNullOrWhiteSpace(request.InstanceName) ? plan.Name : request.InstanceName);

        var mcDir = string.IsNullOrWhiteSpace(request.MinecraftDir)
            ? _games.ResolveMinecraftDir()
            : request.MinecraftDir;

        var instanceDir = Path.Combine(mcDir, "versions", instanceName);
        Directory.CreateDirectory(instanceDir);

        // ⚠️ 顺手建一个空的 `mods/`：扫描器认出"版本隔离"就是看
        //    `mods/` `saves/` `options.txt` 这几个特征。整合包**本来就该是隔离的**
        //    （mod、config、存档都落在实例目录里），可有些包只带 config、一个 mod 都没有 ——
        //    那种包不建 `mods/` 的话会被当成"共用 .minecraft"，
        //    结果包里的 config 和存档根本没生效，而且一声不响。
        Directory.CreateDirectory(Path.Combine(instanceDir, "mods"));

        Say(LaunchStage.Preparing,
            Localizer.Tr("整合包：{name}", ("name", plan.Name)), 0.05);

        token.ThrowIfCancellationRequested();

        // ---------- 2. 下 files[] ----------
        var tasks = plan.DownloadTasks(instanceDir);
        var filesDownloaded = 0;
        var filesFailed = 0;

        if (tasks.Count > 0)
        {
            Say(LaunchStage.Downloading,
                Localizer.Tr("开始下载 {n} 个文件…", ("n", tasks.Count)), 0.1);

            var manager = new DownloadManager(maxWorkers: _settings.EffectiveThreads, source: _settings.DownloadSource);
            foreach (var t in tasks)
            {
                manager.Add(t.Url, t.Path, t.Sha1, t.Size, t.Label, t.Kind);
            }
            manager.Start();

            while (!manager.WaitAll(TimeSpan.FromMilliseconds(200)))
            {
                token.ThrowIfCancellationRequested();
                var snap = manager.Snapshot();
                Say(LaunchStage.Downloading,
                    Localizer.Tr("{done}/{total} 个文件 · {size} · {speed}",
                        ("done", snap.Done), ("total", snap.Count),
                        ("size", DownloadManager.HumanSize(snap.Bytes)),
                        ("speed", DownloadManager.HumanSpeed(snap.Speed))),
                    0.1 + (snap.Percent ?? 0) * 0.6);
            }

            var final = manager.Snapshot();
            filesFailed = final.Tasks.Count(t => t.State == DownloadManager.StateFailed);
            filesDownloaded = final.Count - filesFailed;
        }

        token.ThrowIfCancellationRequested();

        // ---------- 3. 展开覆盖层 ----------
        var overridesWritten = 0;
        if (plan.Overrides.Count > 0)
        {
            Say(LaunchStage.ExtractingNatives,
                Localizer.Tr("正在展开覆盖层（{n} 个文件）…", ("n", plan.Overrides.Count)), 0.75);

            var result = ModpackInstaller.ApplyOverrides(
                blob, plan, instanceDir,
                progress: (done, total) => Say(LaunchStage.ExtractingNatives,
                    Localizer.Tr("覆盖层 {done}/{total}", ("done", done), ("total", total)),
                    0.75 + (total > 0 ? done / (double)total : 0) * 0.1));
            overridesWritten = result.Written;
        }

        token.ThrowIfCancellationRequested();

        // ---------- 4. 原版本体 + 加载器 ----------
        // 目标形态：**一个版本文件夹里就是全部** ——
        //   versions/<实例>/<实例>.json   压平后的版本 JSON（主类 / 库 / 参数都在里面）
        //   versions/<实例>/<实例>.jar    客户端 jar（拷进来）
        //   versions/<实例>/mods|config|… 整合包的文件
        //
        // 原版和加载器只是**中间产物**：先把它们装出来（客户端库 / 资源 / jar 都要用到），
        // 压平 + 拷 jar 之后给它们打上"依赖缓存"记号 —— 版本列表里就不显示了，
        // 但**留在磁盘上**，下次装同版本的包能直接复用（重跑一次 Forge 安装器要几分钟）。
        //
        // ⚠️ 历史包袱：老实现把加载器直接装进实例目录（`VersionId: 实例名`），
        //    结果实例里既没有 jar 也没有主类 → 启动报「找不到客户端 jar」。
        //    再往后一版改成 `inheritsFrom` 指加载器版本 —— 能启动了，但版本列表里
        //    会多出"原版 + 加载器版本"两条（用户明确不要）。现在压平，两头都占。
        var (loaderKey, loaderVersion) = plan.Loader;
        var needsLoader = false;
        var baseVersion = string.Empty;      // 压平的源头：加载器版本，或（没有加载器时）原版

        // 加载器**本该**落在哪个目录（PCL 的命名约定）
        var loaderId = loaderKey.Length > 0 && loaderVersion.Length > 0
            ? LoaderCatalog.VersionFolderName(plan.Minecraft, loaderKey, loaderVersion)
            : string.Empty;

        // 4a. 原版：客户端 jar / 库 / 资源都得先有（"游戏本体自动装进游戏目录"）
        // ⚠️ 先记下装之前 versions/ 里有哪些目录 —— "哪几个是这次才冒出来的"靠它判，
        //    比按名字猜可靠（加载器安装器起的名字跟我们约定的可能不一样）。
        var versionsBefore = SnapshotVersionDirs(mcDir);
        var vanillaDir = Path.Combine(mcDir, "versions", plan.Minecraft);
        var vanillaExisted = File.Exists(Path.Combine(vanillaDir, plan.Minecraft + ".json"));
        if (plan.Minecraft.Length > 0 && !vanillaExisted)
        {
            // ⚠️ 先建目录 + 打"依赖缓存"记号**再开始下**：
            //    这一个要下几百 MB，中途取消 / 断网时半成品会留在 versions/ 里。
            //    先打记号的话它至少不会当成"用户自己装的版本"列出来；
            //    下次装同一个包时也会把它当"我们的"清掉（见下面的 vanillaOurs）。
            VersionScanner.MarkAsCache(vanillaDir);

            Say(LaunchStage.Downloading,
                Localizer.Tr("原版 {name} 还没装，先装它", ("name", plan.Minecraft)), 0.82);
            var vanilla = _downloads
                .InstallVanillaAsync(plan.Minecraft, mcDir, progress, token)
                .GetAwaiter().GetResult();
            if (!vanilla.Ok)
            {
                plan.Warnings.Add(Localizer.Tr("原版 {name} 没装上：{msg}",
                    ("name", plan.Minecraft), ("msg", vanilla.Message)));
            }
        }

        // 4b. 加载器
        if (request.InstallLoader && loaderId.Length > 0)
        {
            Say(LaunchStage.Preparing,
                Localizer.Tr("正在装整合包要的 {name} {ver}…",
                    ("name", loaderKey), ("ver", loaderVersion)), 0.86, true);

            var loaderResult = _loaders.InstallAsync(new LoaderInstallRequest(
                MinecraftVersion: plan.Minecraft,
                LoaderKey: loaderKey,
                LoaderVersion: loaderVersion,
                MinecraftDir: mcDir,
                VersionId: null), progress, token).GetAwaiter().GetResult();

            if (loaderResult.Ok && !string.IsNullOrWhiteSpace(loaderResult.VersionId))
            {
                baseVersion = loaderResult.VersionId!;
            }
            else
            {
                plan.Warnings.Add(Localizer.Tr("加载器没装上：{msg}", ("msg", loaderResult.Message)));
            }
        }

        // 加载器没装成（或者用户关掉了"顺便装加载器"）：**磁盘上已经有一个能用的就照样用**
        // —— 比如加载器其实装好了、只是上面那条路没认出来，不该整包白装。
        // 用容错查找：安装器起的名字（`1.20.1-forge-47.4.10`）跟我们约定的
        // （`1.20.1-Forge_47.4.10`）不一样，只认一种就会出现"明明装好了却说没有"。
        if (baseVersion.Length == 0 && loaderId.Length > 0)
        {
            baseVersion = LoaderInstaller.FindInstalledVersion(
                mcDir, plan.Minecraft, loaderKey, loaderVersion) ?? string.Empty;
        }

        // 加载器要是这次才装出来的，**先打上记号**：万一后面压平 / 拷 jar 失败、
        // 或者用户中途取消，也不会把一份纯中间产物露在版本列表里。
        if (baseVersion.Length > 0 && versionsBefore is not null
            && !versionsBefore.Contains(baseVersion)
            && !string.Equals(baseVersion, plan.Minecraft, StringComparison.OrdinalIgnoreCase))
        {
            VersionScanner.MarkAsCache(Path.Combine(mcDir, "versions", baseVersion));
        }

        // 没有任何加载器的包（原版 + 模组 / 资源包的轻量整合包）：源头就是原版
        if (baseVersion.Length == 0 && loaderId.Length == 0)
        {
            baseVersion = plan.Minecraft;
        }

        // ---------- 5. 压平 + 拷 jar，写成自包含实例 ----------
        var flattened = baseVersion.Length > 0 && IsUsableParent(mcDir, baseVersion)
            ? VersionJsonFlattener.Flatten(mcDir, baseVersion, instanceName)
            : null;

        if (flattened is not null)
        {
            // 元数据塞进去：「这个实例是哪个整合包装的」+ 原版版本号 / 加载器
            // （压平之后没有 inheritsFrom，扫描器就靠这段认它们是哪一版）
            flattened["modpack"] = plan.ModpackMeta(overridesWritten);
            flattened["id"] = instanceName;
            GameInstaller.WriteVersionJson(flattened, instanceDir, instanceName);

            // 客户端 jar：加载器目录里有就用它（OptiFine 那种打过补丁的客户端 jar 就在那儿），
            // 否则用原版那个。
            if (!CopyInstanceJar(mcDir, instanceDir, instanceName, baseVersion, plan.Minecraft))
            {
                plan.Warnings.Add(Localizer.Tr("客户端 jar 没拷进来，这个实例可能起不来"));
            }

            // 跟 PCL2 对齐：在实例目录里写 `PCL\Setup.ini` 的版本隔离开关。
            // 同一个 `.minecraft` 两边都可能打开，写出来两边才认同一件事
            // （PCL2 自己装整合包时也是这么写的，见它的 `CopyOverrideDirectory`）。
            WritePclSetup(instanceDir, plan.Minecraft, loaderKey, loaderVersion);

            // 中间产物用完就清：删"**我们的**"那几份（这次才冒出来的，或者上一次没装完、
            // 带着缓存记号的半成品）；用户自己装的原版 / 加载器版本绝不动。
            // 删不掉（被别的进程占着）、或者这次连 versions/ 都列不出来（versionsBefore 为 null）
            // 就保持/补上记号 —— 至少不出现在列表里。
            var vanillaOurs = versionsBefore is not null
                              && (!versionsBefore.Contains(plan.Minecraft)
                                  || VersionScanner.IsCache(vanillaDir));
            var loaderOurs = versionsBefore is not null
                             && baseVersion.Length > 0
                             && !string.Equals(baseVersion, plan.Minecraft,
                                 StringComparison.OrdinalIgnoreCase)
                             && (!versionsBefore.Contains(baseVersion)
                                 || VersionScanner.IsCache(
                                     Path.Combine(mcDir, "versions", baseVersion)));
            foreach (var dir in IntermediateVersionsToDelete(
                         plan.Minecraft, baseVersion, vanillaOurs, loaderOurs))
            {
                if (!DeleteVersionDir(Path.Combine(mcDir, "versions", dir)))
                {
                    VersionScanner.MarkAsCache(Path.Combine(mcDir, "versions", dir));
                }
            }
        }
        else
        {
            // 连可用的源头都没有（原版没装上、加载器也没有）：
            // 写一份"如实反映现状"的 JSON —— 列表里看得见，点启动会缺主类。
            needsLoader = true;
            GameInstaller.WriteVersionJson(
                plan.VersionJson(instanceName, overridesWritten), instanceDir, instanceName);
        }

        // ---------- 6. 结论 ----------
        Say(LaunchStage.Done, Localizer.Tr("整合包装好了"), 1.0);

        var ok = filesFailed == 0;
        var summary = Localizer.Tr("{name} 装好了：{files} 个文件、{ov} 个覆盖层文件",
            ("name", plan.Name), ("files", filesDownloaded), ("ov", overridesWritten));
        if (filesFailed > 0)
        {
            summary = Localizer.Tr("{name} 装完了，但有 {n} 个文件没下下来",
                ("name", plan.Name), ("n", filesFailed));
        }
        if (needsLoader)
        {
            summary += " —— " + Localizer.Tr("加载器没装上，这个实例还不能直接启动");
        }
        return new ModpackInstallResult(
            Ok: ok,
            Message: summary,
            VersionId: instanceName,
            FilesDownloaded: filesDownloaded,
            FilesFailed: filesFailed,
            OverridesWritten: overridesWritten,
            Warnings: plan.Warnings.ToList(),
            NeedsLoader: needsLoader);
    }

    /// <summary>
    /// 装之前先记下 `versions/` 里已有哪些目录名（判断"这次才冒出来的"用）。
    /// **列不出来就返回 null** —— 调用方据此"一个都不删"（宁可留着，也别误删用户的版本）。
    /// </summary>
    private static HashSet<string>? SnapshotVersionDirs(string mcDir)
    {
        try
        {
            var versionsDir = Path.Combine(mcDir, "versions");
            if (!Directory.Exists(versionsDir))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.EnumerateDirectories(versionsDir))
            {
                var name = Path.GetFileName(dir);
                if (!string.IsNullOrEmpty(name))
                {
                    set.Add(name);
                }
            }
            return set;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>设计说明见 docs/design-notes.md#mosslight-core-services-realmodpackservice-cs</summary>
    public static IReadOnlyList<string> IntermediateVersionsToDelete(
        string mcVersion, string baseVersion, bool vanillaIsOurs, bool loaderIsOurs)
    {
        var result = new List<string>();
        var mc = (mcVersion ?? string.Empty).Trim();
        var baseId = (baseVersion ?? string.Empty).Trim();

        if (mc.Length > 0 && vanillaIsOurs)
        {
            result.Add(mc);
        }
        if (baseId.Length > 0 && !string.Equals(baseId, mc, StringComparison.OrdinalIgnoreCase)
            && loaderIsOurs)
        {
            result.Add(baseId);
        }
        return result;
    }

    /// <summary>删一个版本目录（删不掉返回 false，不抛）。</summary>
    private static bool DeleteVersionDir(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return true;
            }
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 写 PCL2 的版本设置文件（`versions/&lt;实例&gt;/PCL/Setup.ini`）：
    /// 版本隔离 + 这一版是什么（原版版本号 / 加载器）。
    ///
    /// 为什么写：PCL2 装整合包时就是往实例目录里写这些（见它的
    /// `ModModpack.CopyOverrideDirectory` 和 `ModDownloadLib.McInstall`）。
    /// 同一个 `.minecraft` 目录经常两边轮流打开，写出来两边才认同一件事 ——
    /// 我们自己也读 `VersionArgumentIndie` 判断隔离（`VersionScanner.DetectGameDir`）。
    ///
    /// 真实样本（用户机器上 PCL2 装的 `LeSkyBlock` 实例）：
    /// <code>
    /// VersionFabric:0.19.2
    /// Info:正式版 26.1.2, Fabric 0.19.2
    /// VersionVanilla:26.1.2
    /// VersionVanillaName:26.1.2
    /// VersionArgumentIndieV2:True
    /// VersionArgumentIndie:1
    /// </code>
    ///
    /// ⚠️ 格式是 PCL 自己那套 `键:值`（冒号，不是 `=`）；已经存在的其它键**不删**，
    ///    只覆盖我们负责的这几个（整合包自带 `PCL\Setup.ini` 时尤其重要，
    ///    里面有它的 Logo 等设置）。
    /// </summary>
    private static void WritePclSetup(
        string instanceDir, string mcVersion, string loaderKey, string loaderVersion)
    {
        try
        {
            var dir = Path.Combine(instanceDir, "PCL");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "Setup.ini");

            var lines = File.Exists(path)
                ? File.ReadAllLines(path).ToList()
                : new List<string>();

            SetIniValue(lines, "VersionArgumentIndie", "1");
            SetIniValue(lines, "VersionArgumentIndieV2", "True");

            var loaderName = string.Empty;
            if (mcVersion.Length > 0)
            {
                SetIniValue(lines, "VersionVanilla", mcVersion);
                SetIniValue(lines, "VersionVanillaName", mcVersion);
            }

            if (loaderKey.Length > 0 && loaderVersion.Length > 0)
            {
                // PCL2 的键名：VersionFabric / VersionForge / VersionNeoForge / VersionOptiFine / VersionQuilt
                var iniKey = loaderKey.ToLowerInvariant() switch
                {
                    "fabric" => "VersionFabric",
                    "forge" => "VersionForge",
                    "neoforge" => "VersionNeoForge",
                    "optifine" => "VersionOptiFine",
                    "quilt" => "VersionQuilt",
                    _ => string.Empty,
                };
                if (iniKey.Length > 0)
                {
                    SetIniValue(lines, iniKey, loaderVersion);
                }
                loaderName = LoaderCatalog.LoaderNames.TryGetValue(
                    loaderKey.ToLowerInvariant(), out var known) ? known : loaderKey;
            }

            // `Info` 就是 PCL2 版本列表上那行副标题：「正式版 26.1.2, Fabric 0.19.2」
            if (mcVersion.Length > 0)
            {
                var info = $"正式版 {mcVersion}";
                if (loaderName.Length > 0)
                {
                    info += $", {loaderName} {loaderVersion}";
                }
                SetIniValue(lines, "Info", info);
            }

            File.WriteAllLines(path, lines, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            // 写不进去只是"PCL2 那边看不到这些信息"，不影响我们自己的启动
        }
    }

    /// <summary>在 ini 行里设一个键（有就覆盖、没有就追加），格式 `键:值`。</summary>
    private static void SetIniValue(List<string> lines, string key, string value)
    {
        var text = $"{key}:{value}";
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = text;
                return;
            }
        }
        lines.Add(text);
    }

    /// <summary>
    /// 把客户端 jar 拷到实例目录里，文件名用**实例名**。
    ///
    /// 拷的是哪一个（优先级）：
    ///   1. 加载器版本目录里的 `&lt;加载器版本&gt;.jar` —— OptiFine 那种**打过补丁的客户端**，
    ///      用原版那个就等于白装（进游戏没有光影）；
    ///   2. 原版目录里的 `&lt;原版&gt;.jar`。
    ///
    /// ⚠️ 必须是**拷**不是"指过去"：实例要能脱离原版目录独立存在
    ///    （原版目录只是依赖缓存，用户清理掉它也不该把这个整合包弄坏）。
    /// </summary>
    private static bool CopyInstanceJar(
        string mcDir, string instanceDir, string instanceName,
        string baseVersion, string mcVersion)
    {
        var candidates = new List<string>();
        if (baseVersion.Length > 0 && baseVersion != mcVersion)
        {
            candidates.Add(Path.Combine(mcDir, "versions", baseVersion, baseVersion + ".jar"));
        }
        if (mcVersion.Length > 0)
        {
            candidates.Add(Path.Combine(mcDir, "versions", mcVersion, mcVersion + ".jar"));
        }

        var dest = Path.Combine(instanceDir, instanceName + ".jar");
        foreach (var source in candidates)
        {
            try
            {
                if (!File.Exists(source))
                {
                    continue;
                }
                File.Copy(source, dest, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or NotSupportedException)
            {
                // 换下一个来源试试；都不行就返回 false（调用方记一条 warning）
            }
        }
        return false;
    }

    /// <summary>设计说明见 docs/design-notes.md#mosslight-core-services-realmodpackservice-cs</summary>
    private static bool IsUsableParent(string mcDir, string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return false;
        }
        return LoaderInstaller.VerifyInstalled(mcDir, versionId).Ok;
    }
}

/// <summary>整合包的假实现。</summary>
public sealed class StubModpackService : IModpackService
{
    public bool IsPrototype => true;

    /// <summary>解析是纯逻辑，假实现也照真规则做 —— 没必要假装解析不了。</summary>
    public ModpackPlan? Inspect(byte[] blob, out string error)
    {
        try
        {
            var plan = ModpackInstaller.PlanFromBytes(blob);
            error = string.Empty;
            return plan;
        }
        catch (ModpackException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public Task<ModpackInstallResult> InstallAsync(
        ModpackInstallRequest request,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken token = default)
    {
        progress?.Report(new LaunchProgress(LaunchStage.Failed, "原型阶段还没接整合包安装", 0.0));
        return Task.FromResult(new ModpackInstallResult(
            false, "这是 2.0 的界面原型：整合包安装还没搬过来。"));
    }
}
