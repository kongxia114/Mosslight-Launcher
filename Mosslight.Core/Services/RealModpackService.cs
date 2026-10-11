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
        var (loaderKey, loaderVersion) = plan.Loader;
        var needsLoader = false;

        if (request.InstallLoader && loaderKey.Length > 0 && loaderVersion.Length > 0)
        {
            Say(LaunchStage.Preparing,
                Localizer.Tr("正在装整合包要的 {name} {ver}…",
                    ("name", loaderKey), ("ver", loaderVersion)), 0.86, true);

            var loaderResult = _loaders.InstallAsync(new LoaderInstallRequest(
                MinecraftVersion: plan.Minecraft,
                LoaderKey: loaderKey,
                LoaderVersion: loaderVersion,
                MinecraftDir: mcDir,
                // ⚠️ 版本目录名就用实例名 —— 整合包的文件已经落在
                //    `versions/<实例名>/` 底下了，加载器必须写进同一个目录
                VersionId: instanceName), progress, token).GetAwaiter().GetResult();

            if (!loaderResult.Ok)
            {
                // 加载器没装上不算整包失败：mod 和配置都已经在了，
                // 界面要说清楚"这个实例还不能直接启动"
                needsLoader = true;
                plan.Warnings.Add(Localizer.Tr("加载器没装上：{msg}", ("msg", loaderResult.Message)));
            }
        }
        else if (loaderKey.Length == 0)
        {
            needsLoader = true;
            plan.Warnings.Add(Localizer.Tr("这个整合包没说要装加载器"));
        }

        // ---------- 5. 写版本 JSON ----------
        // 加载器装上了 → 它已经写了一份真能启动的 profile，我们只把 modpack 段塞进去；
        // 没装上 → 写一份"如实反映现状"的兜底 JSON（列得出来，点启动会缺 mainClass）
        var versionJsonPath = Path.Combine(instanceDir, instanceName + ".json");
        if (File.Exists(versionJsonPath))
        {
            TryMergeModpackMeta(versionJsonPath, plan, overridesWritten);
        }
        else
        {
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
    /// 把 `modpack` 那段元数据塞进已经写好的版本 JSON。
    ///
    /// 加载器那条路（<see cref="RealLoaderService"/>）写的是**它的** profile，
    /// 里面没有整合包信息 —— 少了这一段，用户就看不出"这个实例是哪个整合包装的"。
    /// 写不进去只记日志：版本 JSON 本身是好的，不该因为这段附加信息失败。
    /// </summary>
    private static void TryMergeModpackMeta(string versionJsonPath, ModpackPlan plan, int overridesWritten)
    {
        try
        {
            var data = JsonIO.ParseBytes(File.ReadAllBytes(versionJsonPath)) as JsonObject;
            if (data is null)
            {
                return;
            }
            data["modpack"] = plan.ModpackMeta(overridesWritten);
            File.WriteAllText(versionJsonPath,
                data.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }) + "\n", new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.Text.Json.JsonException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine($"[Modpack] modpack 段没写进去：{ex.Message}");
        }
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
