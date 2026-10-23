using System.IO.Compression;
using System.Text.Json.Nodes;
using Mosslight.Core.Localization;

namespace Mosslight.Core.Services.Engine;

// 1.18 及以前必须预先解好；1.19+ 虽能自己解压，目录仍要存在，所以统一处理。

/// <summary>一个原生库：库名 / 本地 jar 相对路径 / 解压排除规则。</summary>
public sealed record NativeEntry(string Name, string RelativePath, IReadOnlyList<string> Exclude);

/// <summary>natives 的解压。</summary>
public static class NativesExtractor
{
    /// <summary>natives 目录的位置。</summary>
    public static string NativesDir(string versionDir, string versionId)
        => Path.Combine(versionDir, $"{versionId}-natives");

    /// <summary>列出这个版本需要的原生库 jar。</summary>
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

    public sealed record ExtractResult(int Extracted, int AlreadyDone, List<string> Missing);

    /// <summary>
    /// 这个版本的原生库是否还有没解出来的。
    ///
    /// ⚠️ 不能只看"natives 目录在不在"（2026-10 的 1.12.2 / 1.8.8 就是这样翻的车）：
    ///    目录可能被别的启动器 / 半路失败的那次建过，里面只有 jinput 的 DLL，
    ///    `lwjgl64.dll` 一个都没有 —— 而"目录存在"让旧的判断永远返回 false，启动时直接
    ///    `UnsatisfiedLinkError: no lwjgl64 in java.library.path`。所以按**每个 jar 的完成标记**判。
    /// </summary>
    public static bool NeedsExtract(VersionJson versionJson, string versionDir, string versionId)
    {
        var target = NativesDir(versionDir, versionId);
        if (!Directory.Exists(target))
        {
            return true;
        }

        foreach (var entry in NativeEntries(versionJson))
        {
            var marker = Path.Combine(target, Path.GetFileName(entry.RelativePath) + ".extracted");
            if (!File.Exists(marker))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>把 natives 解压好。</summary>
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
                var complete = true;
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
                    // natives 目录外面（恶意 / 损坏的 jar 就是这么越狱的）。
                    var fullDestination = Path.GetFullPath(destination);
                    if (!fullDestination.StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
                    try
                    {
                        member.ExtractToFile(fullDestination, overwrite: true);
                        extracted++;
                    }
                    catch (Exception fileEx) when (fileEx is IOException or UnauthorizedAccessException)
                    {
                        // 单个文件被占用 / 只读不该拖垮整包：其它 DLL 照样落盘，这个 jar 不写完成标记，
                        // 下次启动再试（典型场景：游戏刚崩、某个 dll 还被系统锁着）
                        complete = false;
                        missing.Add($"{member.Name} ({fileEx.Message})");
                    }
                }

                // 记个标记：下次不用重新解（按 jar 名区分）
                if (complete)
                {
                    File.WriteAllText(marker, string.Empty);
                }
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
