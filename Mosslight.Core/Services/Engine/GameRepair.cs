using System.Text.Json.Nodes;

namespace Mosslight.Core.Services.Engine;

// 补全缺失的库：只补版本 JSON 里写了下载地址、但本地没有的库。
// 下到临时文件、校验 sha1、再原子改名；镜像失败回退官方源。
// ⚠️ 直接复用 DownloadManager 的下载 / 重试 / 熔断逻辑，不要另抄一份。

/// <summary>一个"JSON 说有、本地却没有"的库。</summary>
public sealed record MissingLibrary(
    string Name,
    /// <summary>相对 `libraries/` 的路径。</summary>
    string Path,
    string Url,
    string Sha1 = "",
    long Size = 0);

/// <summary>补全缺失的库。</summary>
public static class GameRepair
{
    /// <summary>列出「JSON 说有、本地却没有」的库。</summary>
    public static List<MissingLibrary> MissingLibraries(string mcDir, JsonObject versionJson)
    {
        var result = new List<MissingLibrary>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in versionJson["libraries"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject lib)
            {
                continue;
            }
            if (!LaunchRules.RulesAllow(lib["rules"]))
            {
                continue;
            }

            var artifact = (lib["downloads"] as JsonObject)?["artifact"] as JsonObject;
            var rel = artifact?["path"]?.GetValue<string>();
            var url = artifact?["url"]?.GetValue<string>();

            if (string.IsNullOrEmpty(rel) || string.IsNullOrEmpty(url))
            {
                // 没有下载地址的：要么是老格式（用坐标拼），要么本来就没有主 jar
                var (mavenRel, _) = LaunchRules.MavenToPath(lib["name"]?.GetValue<string>());
                if (mavenRel is null)
                {
                    continue;
                }
                if (File.Exists(Path.Combine(mcDir, "libraries", mavenRel.Replace('/', Path.DirectorySeparatorChar))))
                {
                    continue;
                }
                // 老格式没有 url，补不了，跳过（不算"缺"）
                continue;
            }

            var target = Path.Combine(mcDir, "libraries", rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target) || !seen.Add(rel))
            {
                continue;
            }

            result.Add(new MissingLibrary(
                Name: lib["name"]?.GetValue<string>() ?? "?",
                Path: rel,
                Url: url,
                // ⚠️ Python 那边 sha1 转小写，C# 这边比较时统一用 OrdinalIgnoreCase，
                // 所以这里保留原样（大小写不影响校验）
                Sha1: artifact?["sha1"]?.GetValue<string>() ?? string.Empty,
                Size: artifact?["size"]?.GetValue<long>() ?? 0));
        }

        return result;
    }

    public sealed record RepairResult(int Repaired, List<string> Failed, int Total)
    {
        public bool Ok => Failed.Count == 0;
    }

    /// <summary>把缺的库补齐，返回 (补好了几个, 失败列表)。</summary>
    public static async Task<RepairResult> RepairAsync(
        string mcDir,
        JsonObject versionJson,
        Action<string>? onLine = null,
        int maxWorkers = 8,
        CancellationToken token = default,
        DownloadSourceMode source = DownloadSourceMode.Auto)
    {
        var todo = MissingLibraries(mcDir, versionJson);
        if (todo.Count == 0)
        {
            return new RepairResult(0, new List<string>(), 0);
        }

        onLine?.Invoke(Localization.Localizer.Tr(
            "发现 {n} 个库不在本地，开始补全…", ("n", todo.Count)));

        var manager = new DownloadManager(maxWorkers: maxWorkers, source: source);
        foreach (var item in todo)
        {
            var dest = Path.Combine(mcDir, "libraries", item.Path.Replace('/', Path.DirectorySeparatorChar));
            onLine?.Invoke(Localization.Localizer.Tr(
                "{name}", ("name", item.Name)));
            manager.Add(item.Url, dest, item.Sha1, item.Size, Path.GetFileName(item.Path),
                Localization.Localizer.Tr("库"));
        }

        manager.Start();

        // 边等边报进度（跟别处一样：工作线程不能碰 UI，只能这边定时取快照）
        while (!manager.WaitAll(TimeSpan.FromMilliseconds(200)))
        {
            token.ThrowIfCancellationRequested();
            var snap = manager.Snapshot();
            onLine?.Invoke(Localization.Localizer.Tr(
                "[{i}/{n}] {size} · {speed}",
                ("i", snap.Done), ("n", snap.Count),
                ("size", DownloadManager.HumanSize(snap.Bytes)),
                ("speed", DownloadManager.HumanSpeed(snap.Speed))));
        }

        var final = manager.Snapshot();
        var failed = final.Tasks
            .Where(t => t.State == DownloadManager.StateFailed)
            .Select(t => t.Label)
            .ToList();
        var repaired = final.Count - failed.Count;

        if (repaired > 0)
        {
            onLine?.Invoke(Localization.Localizer.Tr("补全了 {n} 个库", ("n", repaired)));
        }
        return new RepairResult(repaired, failed, todo.Count);
    }
}
