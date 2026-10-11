using System.IO.Compression;
using System.Text.Json.Nodes;
using Mosslight.Core.Localization;

namespace Mosslight.Core.Services.Engine;

// ============================================================================
//  解压 natives
//  （移植自 1.0.0 的 core/natives.py）
//
//  Minecraft 的部分依赖把动态库（.dll / .so / .dylib）放在**单独的 classifier jar**
//  里，启动前要解到一个目录，再用 `-Djava.library.path` 之类的参数指过去。
//
//  为什么统一都解一遍（哪怕 1.19+ 其实能自己解压）：
//  - 1.18 及以前**必须**预先解好，否则游戏起不来
//  - 1.19 起 LWJGL 3 能自己从 classpath 解压，但目录仍然要存在
//  统一处理比按版本分叉可靠，代价也就是解几个小 jar。
//
//  解压规则照 JSON 里的 `extract.exclude` 走（通常是排除 `META-INF/`）。
// ============================================================================

/// <summary>一个原生库：库名 / 本地 jar 相对路径 / 解压排除规则。</summary>
public sealed record NativeEntry(string Name, string RelativePath, IReadOnlyList<string> Exclude);

/// <summary>natives 的解压。</summary>
public static class NativesExtractor
{
    /// <summary>
    /// natives 目录的位置。
    ///
    /// 和 PCL / HMCL 的约定一致（`&lt;版本目录&gt;/&lt;版本id&gt;-natives`），
    /// 所以它们装好的版本能直接复用。
    /// </summary>
    public static string NativesDir(string versionDir, string versionId)
        => Path.Combine(versionDir, $"{versionId}-natives");

    /// <summary>
    /// 列出这个版本需要的原生库 jar。
    ///
    /// 返回只保留在当前系统上适用的那些。
    /// </summary>
    public static List<NativeEntry> NativeEntries(VersionJson versionJson)
    {
        var entries = new List<NativeEntry>();

        foreach (var node in versionJson.Libraries)
        {
            if (node is not JsonObject lib)
            {
                continue;
            }
            if (!LaunchRules.RulesAllow(lib["rules"]))
            {
                continue;
            }

            var key = LaunchRules.NativesKey(lib);
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            var name = lib["name"] is JsonValue nameNode &&
                       nameNode.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? nameNode.GetValue<string>()
                : "?";

            var rel = (lib["downloads"] as JsonObject)?["classifiers"] is JsonObject classifiers &&
                      classifiers[key] is JsonObject artifact &&
                      artifact["path"] is JsonValue pathNode &&
                      pathNode.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? pathNode.GetValue<string>()
                : null;

            if (string.IsNullOrEmpty(rel))
            {
                // 老格式：只有 downloads.classifiers 没有 path 时，按坐标拼。
                // Maven 坐标里的 classifier 就是 natives_key。
                var parts = name.Split(':');
                if (parts.Length < 3)
                {
                    continue;
                }
                var group = parts[0];
                var artifactName = parts[1];
                var version = parts[2];
                rel = $"{group.Replace('.', '/')}/{artifactName}/{version}/{artifactName}-{version}-{key}.jar";
            }

            var exclude = (lib["extract"] as JsonObject)?["exclude"] is JsonArray excludeArray
                ? excludeArray
                    .Where(x => x is JsonValue v &&
                                v.GetValueKind() == System.Text.Json.JsonValueKind.String)
                    .Select(x => x!.GetValue<string>())
                    .ToList()
                : new List<string> { "META-INF/" };

            entries.Add(new NativeEntry(name, rel.Replace('/', Path.DirectorySeparatorChar), exclude));
        }

        return entries;
    }

    /// <summary>解压的结果。</summary>
    public sealed record ExtractResult(int Extracted, int AlreadyDone, List<string> Missing);

    /// <summary>
    /// 把 natives 解压好。
    ///
    /// 缺失不一定是致命的 —— 按调用方的策略决定要不要拦。
    /// </summary>
    public static ExtractResult EnsureNatives(
        VersionJson versionJson,
        string mcDir,
        string versionDir,
        string versionId,
        Action<string>? log = null)
    {
        var target = NativesDir(versionDir, versionId);
        Directory.CreateDirectory(target);

        var librariesRoot = Path.Combine(mcDir, "libraries");
        var extracted = 0;
        var already = 0;
        var missing = new List<string>();

        foreach (var entry in NativeEntries(versionJson))
        {
            var jarPath = Path.Combine(librariesRoot, entry.RelativePath);
            if (!File.Exists(jarPath))
            {
                missing.Add($"{entry.Name} ({entry.RelativePath})");
                continue;
            }

            var marker = Path.Combine(target, Path.GetFileName(entry.RelativePath) + ".extracted");
            if (File.Exists(marker))
            {
                already++;
                continue;
            }

            try
            {
                using var archive = ZipFile.OpenRead(jarPath);
                foreach (var member in archive.Entries)
                {
                    // 排除规则是**前缀匹配**（`META-INF/`），跟 Python 版一致
                    if (entry.Exclude.Any(prefix => member.FullName.StartsWith(prefix, StringComparison.Ordinal)))
                    {
                        continue;
                    }
                    // 目录项跳过（Length == 0 且 FullName 以 / 结尾）
                    if (member.FullName.EndsWith('/') || member.Name.Length == 0)
                    {
                        continue;
                    }

                    var destination = Path.Combine(target, member.FullName.Replace('/', Path.DirectorySeparatorChar));

                    // ⚠️ Zip Slip 防护：压缩包里的路径可能带 `..\`，直接拼出去会写到
                    //    natives 目录外面（恶意 / 损坏的 jar 就是这么越狱的）。
                    var fullDestination = Path.GetFullPath(destination);
                    if (!fullDestination.StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
                    member.ExtractToFile(fullDestination, overwrite: true);
                    extracted++;
                }

                // 记个标记：下次不用重新解（按 jar 名区分）
                File.WriteAllText(marker, string.Empty);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                missing.Add($"{entry.Name} ({ex.Message})");
            }
        }

        if (log is not null)
        {
            if (extracted > 0)
            {
                log(Localizer.Tr("解压了 {n} 个原生库文件 → {path}", ("n", extracted), ("path", target)));
            }
            else if (already > 0)
            {
                log(Localizer.Tr("原生库已经解压过了（{n} 个）", ("n", already)));
            }
        }

        return new ExtractResult(extracted, already, missing);
    }
}
