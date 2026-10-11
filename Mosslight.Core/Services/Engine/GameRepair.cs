using System.Text.Json.Nodes;

namespace Mosslight.Core.Services.Engine;

// ============================================================================
//  补全缺失的库
//  （移植自 1.0.0 的 core/repair.py）
//
//  有些版本是"装了一半"的 —— 比如 `1.12.2-Forge_14.23.5.2864` 缺了
//  `org.apache.maven:maven-artifact:3.5.3`，而 Forge 启动时非要它不可，
//  于是崩在 `ClassNotFoundException: ...ArtifactVersion` 上。
//
//  这个模块只做一件事：**把版本 JSON 里声明了下载地址、但本地没有的库下回来。**
//
//  几个原则：
//  - 只补「JSON 里写了 url」的（那种才是真的该有却没下到）。
//    像 LWJGL 2 的 `lwjgl-platform` 本来就没有主 jar，不算缺。
//  - 先试 BMCLAPI 镜像，失败再回退官方源 —— BMCLAPI 是志愿者节点，
//    不能当唯一来源。
//  - 下到临时文件、校验 sha1、再原子改名。中途失败不会留下半个 jar
//    骗过后面的检查。
//
//  ⚠️ 跟 1.0.0 的一处**有意不同**：那边自己拿 urllib 一个文件一个文件地同步下
//     （还自己抄了一份镜像映射和 sha1 校验）。C# 这边直接走
//     <see cref="DownloadManager"/> —— 镜像 / 重试 / 熔断 / sha1 / 原子改名
//     它全都做过了，再抄一遍只会多出一份会腐化的副本。
//     **"哪些库算缺"的判断**才是这个模块的价值，那部分逐字移植、逐值验。
// ============================================================================

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
    /// <summary>
    /// 列出「JSON 说有、本地却没有」的库。
    ///
    /// ⚠️ 三种情况**不算缺**，别把它们报进去（报了用户会看到一堆"失败"，
    ///    其实什么都不缺）：
    ///   1. 没写 `downloads.artifact` 的 —— 老格式靠坐标拼，但没有 url 补不了
    ///   2. 只有 classifiers（native）的 —— 它压根没有主 jar
    ///   3. 坐标都拼不出来的
    /// </summary>
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
                //    所以这里保留原样（大小写不影响校验）
                Sha1: artifact?["sha1"]?.GetValue<string>() ?? string.Empty,
                Size: artifact?["size"]?.GetValue<long>() ?? 0));
        }

        return result;
    }

    /// <summary>补全的结果。</summary>
    public sealed record RepairResult(int Repaired, List<string> Failed, int Total)
    {
        public bool Ok => Failed.Count == 0;
    }

    /// <summary>
    /// 把缺的库补齐，返回 (补好了几个, 失败列表)。
    ///
    /// <paramref name="onLine"/> 收进度文字（可选）。**不会碰 UI** —— 调用方
    /// 自己决定要不要切线程。
    /// </summary>
    public static async Task<RepairResult> RepairAsync(
        string mcDir,
        JsonObject versionJson,
        Action<string>? onLine = null,
        int maxWorkers = 8,
        CancellationToken token = default)
    {
        var todo = MissingLibraries(mcDir, versionJson);
        if (todo.Count == 0)
        {
            return new RepairResult(0, new List<string>(), 0);
        }

        onLine?.Invoke(Localization.Localizer.Tr(
            "发现 {n} 个库不在本地，开始补全…", ("n", todo.Count)));

        var manager = new DownloadManager(maxWorkers: maxWorkers);
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
