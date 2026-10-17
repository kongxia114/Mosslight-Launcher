using System.Text.Json.Nodes;
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

    public RealModpackService(AppSettings settings, RealGameService games, ILoaderService loaders)
    {
        _settings = settings;
        _games = games;
        _loaders = loaders;
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

            var manager = new DownloadManager(maxWorkers: _settings.EffectiveThreads);
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

        // ---------- 4. 装加载器 ----------
        // ⚠️ 加载器装成**独立版本**（`1.20.1-forge-47.4.10` 这种），实例不占它，
        //    只写一条 `inheritsFrom` 指过去 —— 这是 PCL2 的做法。
        //
        //    老做法是 `VersionId: instanceName`（把加载器直接装进实例目录），
        //    这条路有两个坑，用户 2026-10-18 两个都踩到了：
        //      ① Forge 的安装器**只认自己的目录名**，装完还是 `1.20.1-forge-…`，
        //         而"装完找一下多出来哪个版本"那步会先命中**已经存在的实例目录**
        //         （里面还没 JSON）→ 校验失败 → 明明装好了却报「装完了但版本不对」；
        //      ② 就算改好名字，实例目录里也只有加载器那份 JSON，整合包再往上盖
        //         一份 `mainClass: ""` 的就彻底起不来了（「找不到客户端 jar」）。
        //    改成独立版本后，加载器还能被别的实例复用，目录名也不会跟整合包撞。
        var (loaderKey, loaderVersion) = plan.Loader;
        var needsLoader = false;
        var parentVersion = string.Empty;

        // 加载器**本该**落在哪个目录（PCL 的命名约定）
        var loaderId = loaderKey.Length > 0 && loaderVersion.Length > 0
            ? LoaderCatalog.VersionFolderName(plan.Minecraft, loaderKey, loaderVersion)
            : string.Empty;

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
                parentVersion = loaderResult.VersionId!;
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
        if (parentVersion.Length == 0 && loaderId.Length > 0)
        {
            parentVersion = LoaderInstaller.FindInstalledVersion(
                mcDir, plan.Minecraft, loaderKey, loaderVersion) ?? string.Empty;
        }

        // 没有任何加载器的包（原版 + 模组 / 资源包的轻量整合包）：直接继承原版
        if (parentVersion.Length == 0 && loaderId.Length == 0 && plan.Minecraft.Length > 0)
        {
            if (IsUsableParent(mcDir, plan.Minecraft))
            {
                parentVersion = plan.Minecraft;
            }
            else
            {
                // 复用装加载器那条路上同一条文案（"它继承的原版 X 还没装"）
                plan.Warnings.Add(Localizer.Tr("它继承的原版 {mc} 还没装（{path} 不存在）",
                    ("mc", plan.Minecraft),
                    ("path", Path.Combine(mcDir, "versions", plan.Minecraft, plan.Minecraft + ".json"))));
            }
        }

        // ---------- 5. 写版本 JSON ----------
        // 有能继承的父版本 → 写 `inheritsFrom`（主类 / 库 / 客户端 jar 全从父版本链上取）；
        // 一个都没有 → 写"如实反映现状"的兜底 JSON（列得出来，点启动会缺主类）
        if (parentVersion.Length > 0)
        {
            GameInstaller.WriteVersionJson(
                plan.InstanceJson(instanceName, parentVersion, overridesWritten), instanceDir, instanceName);
        }
        else
        {
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
    /// 这个版本能不能当实例的父版本（= 能不能真的启动）。
    ///
    /// 判据跟装加载器那边**同一份**（<see cref="LoaderInstaller.VerifyInstalled"/>）：
    /// 目录里有 JSON，且 JSON 自己有主类、或者它继承的父版本在本地。
    /// 别在这里另写一套判断 —— 两边标准不一致就会出现"装的时候说好了、启动时说不行"。
    /// </summary>
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
