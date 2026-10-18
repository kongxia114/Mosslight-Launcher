using Mosslight.Core.Localization;
using Mosslight.Core.Services.Engine;
using Mosslight.Core.Settings;

namespace Mosslight.Core.Services;

// ============================================================================
//  版本下载 / 安装的真实现
//
//  这个类只做**编排**，四件事都交给专门的模块：
//      拉清单          → VersionManifest
//      算要下哪些文件  → GameInstaller.PlanVersion
//      下文件          → DownloadManager
//      解压原生库      → NativesExtractor
//
//  为什么这样切：1.0.0 最早把"读 JSON / 算清单 / 下载 / 解压"揉在一个方法里，
//  结果是没法单测（要真联网下几个 G 才知道清单算对没有），而且规则判断、
//  natives 挑拣这些最容易错的细节全埋在下载流程里看不见。
// ============================================================================

/// <summary>真实的下载 / 安装服务。</summary>
public sealed class RealDownloadService : IDownloadService
{
    private readonly AppSettings _settings;
    private readonly VersionManifest _manifest;
    private readonly RealGameService _games;
    private readonly object _lock = new();

    private List<string> _versions = new();

    public RealDownloadService(AppSettings settings, string configDir, RealGameService games)
    {
        _settings = settings;
        _manifest = new VersionManifest(configDir);
        _games = games;
    }

    public bool IsPrototype => false;

    public IReadOnlyList<string> AvailableMinecraftVersions
    {
        get
        {
            lock (_lock)
            {
                return _versions;
            }
        }
    }

    public async Task<IReadOnlyList<string>> RefreshVersionListAsync(CancellationToken token = default)
    {
        var (data, _) = await _manifest.LoadAsync(force: false, token).ConfigureAwait(false);
        if (data is null)
        {
            // 断网又有缓存的话 LoadAsync 会把缓存还回来；真的什么都没有就保持原样
            return AvailableMinecraftVersions;
        }

        var ids = VersionManifest.AllVersionIds(data);
        lock (_lock)
        {
            _versions = ids;
        }
        return ids;
    }

    /// <summary>
    /// 装一个原版版本：版本 JSON + 客户端 jar + 全部库 + natives + 资源。
    /// </summary>
    public async Task<InstallResult> InstallVanillaAsync(
        string minecraftVersion,
        string minecraftDir,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken token = default)
    {
        void Report(LaunchStage stage, string message, double fraction = 0, bool indeterminate = false)
            => progress?.Report(new LaunchProgress(stage, message, fraction, indeterminate));

        try
        {
            return await Task.Run(
                () => Install(minecraftVersion, minecraftDir, progress, Report, token),
                token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new InstallResult(false, Localizer.Tr("已取消"));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                      or System.Text.Json.JsonException or InvalidOperationException)
        {
            var message = Localizer.Tr("安装失败：{err}", ("err", ex.Message));
            Report(LaunchStage.Failed, message);
            return new InstallResult(false, message);
        }
    }

    private InstallResult Install(
        string minecraftVersion,
        string minecraftDir,
        IProgress<LaunchProgress>? progress,
        Action<LaunchStage, string, double, bool> report,
        CancellationToken token)
    {
        // ---------- 1. 清单里找这个版本 ----------
        report(LaunchStage.Preparing, Localizer.Tr("正在获取版本清单…"), 0.02, true);

        var (manifest, info) = _manifest.LoadAsync(force: false, token).GetAwaiter().GetResult();
        if (manifest is null)
        {
            var message = Localizer.Tr("拿不到版本清单（检查一下网络）");
            report(LaunchStage.Failed, message, 0, false);
            return new InstallResult(false, message);
        }

        var entry = (manifest["versions"] as System.Text.Json.Nodes.JsonArray ?? new())
            .OfType<System.Text.Json.Nodes.JsonObject>()
            .FirstOrDefault(v => v["id"]?.GetValue<string>() == minecraftVersion);

        var versionUrl = entry?["url"]?.GetValue<string>();
        if (string.IsNullOrEmpty(versionUrl))
        {
            var message = Localizer.Tr("版本清单里没有 {name}", ("name", minecraftVersion));
            report(LaunchStage.Failed, message, 0, false);
            return new InstallResult(false, message);
        }

        token.ThrowIfCancellationRequested();

        // ---------- 2. 版本 JSON 落盘 ----------
        report(LaunchStage.Preparing, Localizer.Tr("正在获取 {name} 的版本信息…", ("name", minecraftVersion)), 0.05, true);

        var vj = GameInstaller.HttpJsonAsync(versionUrl, token).GetAwaiter().GetResult();
        if (vj is null || !GameInstaller.LooksLikeLauncherJson(vj))
        {
            var message = Localizer.Tr("这个版本的 JSON 不对（没有 mainClass / libraries）");
            report(LaunchStage.Failed, message, 0, false);
            return new InstallResult(false, message);
        }

        var versionDir = Path.Combine(minecraftDir, "versions", minecraftVersion);
        try
        {
            GameInstaller.WriteVersionJson(vj, versionDir, minecraftVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var message = Localizer.Tr("写版本 JSON 失败：{err}", ("err", ex.Message));
            report(LaunchStage.Failed, message, 0, false);
            return new InstallResult(false, message);
        }

        token.ThrowIfCancellationRequested();

        // ---------- 3. 资源索引 ----------
        report(LaunchStage.CheckingFiles, Localizer.Tr("正在获取资源索引…"), 0.1, true);
        var assetIndex = GameInstaller.LoadAssetIndexAsync(minecraftDir, vj, null, token)
            .GetAwaiter().GetResult();

        // ---------- 4. 算清单 ----------
        var plan = GameInstaller.PlanVersion(vj, minecraftDir, assetIndex: assetIndex);
        var tasks = plan.Tasks().ToList();

        if (tasks.Count == 0)
        {
            var message = Localizer.Tr("这个版本没有任何要下载的东西（JSON 不对？）");
            report(LaunchStage.Failed, message, 0, false);
            return new InstallResult(false, message);
        }

        token.ThrowIfCancellationRequested();

        // ---------- 5. 下载 ----------
        report(LaunchStage.Downloading,
            Localizer.Tr("开始下载 {n} 个文件…", ("n", tasks.Count)), 0.15, false);

        var manager = new DownloadManager(
            maxWorkers: _settings.EffectiveThreads,
            source: _settings.DownloadSource);

        foreach (var task in tasks)
        {
            manager.Add(
                url: task.Url,
                dest: task.Path,
                sha1: task.Sha1,
                size: task.Size,
                label: task.Label,
                kind: task.Kind,
                altUrls: task.AltUrls);
        }

        manager.Start();

        // 边等边报进度。⚠️ 这里**必须**轮询而不是等一个回调：
        // 下载管理器的工作线程不能碰 UI，进度只能由这边定时取快照再报上去。
        var lastReport = DateTime.UtcNow;
        while (!manager.WaitAll(TimeSpan.FromMilliseconds(200)))
        {
            token.ThrowIfCancellationRequested();

            if ((DateTime.UtcNow - lastReport).TotalMilliseconds < 250)
            {
                continue;
            }
            lastReport = DateTime.UtcNow;

            var snap = manager.Snapshot();
            var fraction = 0.15 + (snap.Percent ?? 0) * 0.8;
            var speed = DownloadManager.HumanSpeed(snap.Speed);
            var suffix = snap.MirrorOff ? Localizer.Tr("（镜像不通，走官方源）") : string.Empty;

            report(LaunchStage.Downloading,
                Localizer.Tr("{done}/{total} 个文件 · {size} · {speed}{suffix}",
                    ("done", snap.Done), ("total", snap.Count),
                    ("size", DownloadManager.HumanSize(snap.Bytes)),
                    ("speed", speed), ("suffix", suffix)),
                Math.Clamp(fraction, 0, 0.95), false);
        }

        token.ThrowIfCancellationRequested();

        var final = manager.Snapshot();
        var failedTasks = final.Tasks.Where(t => t.State == DownloadManager.StateFailed).ToList();
        var failedFiles = failedTasks.Select(t => t.Error.Length > 0 ? $"{t.Label}: {t.Error}" : t.Label).ToList();

        // ---------- 6. 解压 natives ----------
        report(LaunchStage.ExtractingNatives, Localizer.Tr("正在解压原生库…"), 0.97, false);

        var nativesDir = NativesExtractor.NativesDir(versionDir, minecraftVersion);
        var merged = VersionJsonLoader.TryLoad(minecraftDir, minecraftVersion);
        var nativeNotes = new List<string>();
        if (merged is not null)
        {
            var result = NativesExtractor.EnsureNatives(
                merged, minecraftDir, versionDir, minecraftVersion,
                log: text => nativeNotes.Add(text));
            nativeNotes.AddRange(result.Missing);
        }

        // ---------- 7. 结论 ----------
        if (failedTasks.Count > 0)
        {
            var message = Localizer.Tr("装完了，但有 {n} 个文件没下下来", ("n", failedTasks.Count));
            report(LaunchStage.Failed, message, 1, false);
            return new InstallResult(false, message, minecraftVersion,
                final.Done - failedTasks.Count, failedTasks.Count, failedFiles);
        }

        var okMessage = Localizer.Tr("安装完成：{name}（{n} 个文件）",
            ("name", minecraftVersion), ("n", final.Count));
        report(LaunchStage.Done, okMessage, 1, false);

        if (info.Stale)
        {
            // 用的是过期缓存 —— 说一声，免得用户以为拿的是最新的
            report(LaunchStage.Done, Localizer.Tr("（版本清单来自缓存，可能不是最新的）"), 1, false);
        }

        return new InstallResult(true, okMessage, minecraftVersion, final.Count, 0, null);
    }
}
