using System.Text.Json.Nodes;
using Mosslight.Core.Localization;
using Mosslight.Core.Services.Engine;
using Mosslight.Core.Settings;

namespace Mosslight.Core.Services;

// ============================================================================
//  模组管理 + Modrinth 的真实现
//
//  这个类很薄是**故意的**：真正干活的在
//      <see cref="ModrinthApi"/>    搜索 / 项目 / 版本 / 筛选（数据层）
//      <see cref="ModsDirectory"/>  该装到哪个目录 / 本地 mod 的增删启停
//  这里只负责"把两半接起来 + 下载"。
// ============================================================================

/// <summary>真实的模组服务。</summary>
public sealed class RealModService : IModService
{
    private readonly AppSettings _settings;

    public RealModService(AppSettings settings)
    {
        _settings = settings;
    }

    public bool IsPrototype => false;

    // ---------- 搜索 / 项目 / 版本 ----------

    public Task<JsonObject> SearchAsync(
        string query = "",
        string gameVersion = ModrinthApi.AnyOption,
        string loader = ModrinthApi.AnyOption,
        string category = ModrinthApi.AnyOption,
        string projectType = "mod",
        string sort = ModrinthApi.DefaultSort,
        int limit = 20,
        int offset = 0,
        CancellationToken token = default)
        => ModrinthApi.SearchProjectsAsync(
            query, gameVersion, loader, category, projectType, sort, limit, offset, token);

    public Task<IReadOnlyList<JsonObject>> ProjectVersionsAsync(
        string projectId, CancellationToken token = default)
        => ModrinthApi.GetProjectVersionsAsync(projectId, token)
            .ContinueWith(t => (IReadOnlyList<JsonObject>)t.Result, token,
                TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

    public IReadOnlyList<JsonObject> FilterVersions(
        IEnumerable<JsonObject> versions, string gameVersion = "", string loader = "")
        => ModrinthApi.FilterVersions(versions, gameVersion, loader);

    // ---------- 目录 / 本地 mod ----------

    public string ModsDirFor(
        string minecraftDir, string gameVersion, string loader, GameVersion? versionInfo = null)
        => ModsDirectory.ModsDirFor(minecraftDir, gameVersion, loader, versionInfo);

    public string? ContentDirFor(
        string minecraftDir, string projectType, string gameVersion, string loader,
        GameVersion? versionInfo = null)
        => ModsDirectory.ContentDirFor(minecraftDir, projectType, gameVersion, loader, versionInfo);

    public IReadOnlyList<ModsDirectory.LocalMod> LocalMods(string modsDir)
        => ModsDirectory.ListMods(modsDir);

    public string? SetEnabled(string path, bool enabled)
        => ModsDirectory.SetEnabled(path, enabled);

    public bool Delete(string path)
        => ModsDirectory.Delete(path);

    // ---------- 装 ----------

    /// <summary>
    /// 把一条 Modrinth 版本装进**这个项目类型该落的目录**。
    ///
    /// ⚠️ 目录是用 <see cref="ModsDirectory.ContentDirFor"/> 算的 ——
    ///    **版本隔离的版本装进版本目录**，否则装进共用的 `mods/`
    ///    （资源包 / 光影包同理，只是最后那段目录名不同）。
    ///    跟启动时用的是同一套判断，所以不会出现"装上了但游戏里没有"。
    ///
    /// ⚠️ `projectType` 在**末尾**、默认 `"mod"`：老调用点（6 个位置参数）
    ///    一个字都不用改，行为也一模一样。
    /// </summary>
    public async Task<ModInstallResult> InstallAsync(
        JsonObject version,
        string minecraftDir,
        string gameVersion,
        string loader,
        GameVersion? versionInfo = null,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken token = default,
        string projectType = "mod")
    {
        void Report(LaunchStage stage, string message, double fraction = 0, bool indeterminate = false)
            => progress?.Report(new LaunchProgress(stage, message, fraction, indeterminate));

        try
        {
            // ⚠️ 没有固定落点的类型（数据包）在这里就**如实失败**，不硬塞进 mods/：
            //    装到一个游戏根本不读的目录里，比不做还糟（用户以为装上了）。
            var contentDir = ModsDirectory.ContentDirFor(
                minecraftDir, projectType, gameVersion, loader, versionInfo);
            if (contentDir is null)
            {
                var why = Localizer.Tr("这个类型没有固定的安装目录（数据包要放进某一个存档里）");
                Report(LaunchStage.Failed, why, 0);
                return new ModInstallResult(false, why);
            }

            var task = ModrinthApi.TaskFromVersion(version, contentDir, projectType);

            Report(LaunchStage.Downloading,
                Localizer.Tr("正在下载 {name}…", ("name", task.Label)), 0.1);

            var manager = new DownloadManager(maxWorkers: _settings.EffectiveThreads);
            manager.Add(task.Url, task.Path, task.Sha1, task.Size, task.Label, task.Kind);
            manager.Start();

            while (!manager.WaitAll(TimeSpan.FromMilliseconds(200)))
            {
                token.ThrowIfCancellationRequested();
                var snap = manager.Snapshot();
                Report(LaunchStage.Downloading,
                    Localizer.Tr("{size} · {speed}",
                        ("size", DownloadManager.HumanSize(snap.Bytes)),
                        ("speed", DownloadManager.HumanSpeed(snap.Speed))),
                    0.1 + (snap.Percent ?? 0) * 0.85);
            }

            var final = manager.Snapshot();
            var failed = final.Tasks.FirstOrDefault(t => t.State == DownloadManager.StateFailed);
            if (failed is not null)
            {
                var message = Localizer.Tr("下载失败：{why}",
                    ("why", failed.Error.Length > 0 ? failed.Error : failed.State));
                Report(LaunchStage.Failed, message, 0);
                return new ModInstallResult(false, message);
            }

            Report(LaunchStage.Done,
                Localizer.Tr("{name} 装好了", ("name", task.Label)), 1.0);
            return new ModInstallResult(true,
                Localizer.Tr("{name} 装到了 {dir}", ("name", task.Label), ("dir", contentDir)),
                task.Path,
                new InstallResult(true, Localizer.Tr("装好了"), null, final.Count, 0));
        }
        catch (OperationCanceledException)
        {
            return new ModInstallResult(false, Localizer.Tr("已取消"));
        }
        catch (LoaderInstallException ex)
        {
            // ModrinthApi.TaskFromVersion 在"这个版本没有可下载的文件"时抛它
            Report(LaunchStage.Failed, ex.Message, 0);
            return new ModInstallResult(false, ex.Message);
        }
        catch (Exception ex) when (ex is ModrinthException or IOException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            var message = Localizer.Tr("装模组失败：{err}", ("err", ex.Message));
            Report(LaunchStage.Failed, message, 0);
            return new ModInstallResult(false, message);
        }
    }
}

/// <summary>模组的假实现。</summary>
public sealed class StubModService : IModService
{
    public bool IsPrototype => true;

    public Task<JsonObject> SearchAsync(
        string query = "",
        string gameVersion = ModrinthApi.AnyOption,
        string loader = ModrinthApi.AnyOption,
        string category = ModrinthApi.AnyOption,
        string projectType = "mod",
        string sort = ModrinthApi.DefaultSort,
        int limit = 20,
        int offset = 0,
        CancellationToken token = default)
        => Task.FromResult(new JsonObject
        {
            ["hits"] = new JsonArray(),
            ["total_hits"] = 0,
            ["offset"] = 0,
            ["limit"] = limit,
        });

    public Task<IReadOnlyList<JsonObject>> ProjectVersionsAsync(
        string projectId, CancellationToken token = default)
        => Task.FromResult<IReadOnlyList<JsonObject>>(Array.Empty<JsonObject>());

    public IReadOnlyList<JsonObject> FilterVersions(
        IEnumerable<JsonObject> versions, string gameVersion = "", string loader = "")
        => Array.Empty<JsonObject>();

    /// <summary>假实现还是照**真规则**算目录 —— 目录反查是纯逻辑，没必要假装。</summary>
    public string ModsDirFor(
        string minecraftDir, string gameVersion, string loader, GameVersion? versionInfo = null)
        => ModsDirectory.ModsDirFor(minecraftDir, gameVersion, loader, versionInfo);

    public string? ContentDirFor(
        string minecraftDir, string projectType, string gameVersion, string loader,
        GameVersion? versionInfo = null)
        => ModsDirectory.ContentDirFor(minecraftDir, projectType, gameVersion, loader, versionInfo);

    public IReadOnlyList<ModsDirectory.LocalMod> LocalMods(string modsDir)
        => ModsDirectory.ListMods(modsDir);

    public string? SetEnabled(string path, bool enabled) => ModsDirectory.SetEnabled(path, enabled);

    public bool Delete(string path) => ModsDirectory.Delete(path);

    public Task<ModInstallResult> InstallAsync(
        JsonObject version,
        string minecraftDir,
        string gameVersion,
        string loader,
        GameVersion? versionInfo = null,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken token = default,
        string projectType = "mod")
    {
        // 参数一个都不能"没用上"（编译器会为未使用的参数报警告的是别的语言，
        // 这里显式引用一下，顺便说明假实现的落点也是照真规则算的）
        _ = (version, minecraftDir, gameVersion, loader, versionInfo, token, projectType);
        progress?.Report(new LaunchProgress(LaunchStage.Failed, "原型阶段还没接模组下载", 0.0));
        return Task.FromResult(new ModInstallResult(
            false, "这是 2.0 的界面原型：模组下载还没搬过来。"));
    }
}
