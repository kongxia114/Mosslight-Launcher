using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Mosslight.Core.Localization;

namespace Mosslight.Core.Services.Engine;

// Java 查找与选择：扫描注册表 / 常见目录 / PATH / MC 自带运行时，并按版本 JSON 的 javaVersion 挑。
// ⚠️ MC 自带运行时有扁平、嵌套两种目录布局，只扫一种会漏；Oracle 的 latest 软链目录也要跟进。

/// <summary>探测到的 Java 信息（内部的原始数据，跟 JavaRuntime 分开是为了存缓存）。</summary>
public sealed record JavaProbe(
    string Path,
    string Version = "",
    int Major = 0,
    string Vendor = "",
    string Architecture = "",
    string? Error = null)
{
    public bool Usable => string.IsNullOrEmpty(Error) && Major > 0;

    public bool Is64Bit => Architecture == "x86_64";

    public JavaRuntime ToRuntime(bool isDefault = false)
        => new(Path, Version, Architecture, isDefault, Major, Vendor, Error);
}

/// <summary>扫描结果里的一条缓存记录。</summary>
internal sealed class JavaCacheEntry
{
    public bool Ok { get; set; }

    /// <summary>判断"还是不是同一个 java.exe"的依据：mtime + 大小。</summary>
    public long[] Fingerprint { get; set; } = Array.Empty<long>();

    public string Version { get; set; } = string.Empty;

    public int Major { get; set; }

    public string Vendor { get; set; } = string.Empty;

    public string Architecture { get; set; } = string.Empty;

    /// <summary>上次探测失败的时刻（Unix 秒）。</summary>
    public double FailedAt { get; set; }
}

/// <summary>Java 查找与选择。</summary>
public sealed class JavaScanner
{
    // `java -version` 超时。
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(5);

    // 扫描结果的缓存文件（放配置目录里）。
    private const int CacheVersion = 2;

    private const string CacheFileName = "java_cache.json";

    // 探测失败的路径多久之内不再重试（秒）。
    private const double FailureRetrySeconds = 6 * 3600;

    // 缓存里那条"上次没读出来"在界面上怎么显示。
    private static string FailedCacheMessage => Localizer.Tr("上次扫描没读出它的版本，暂时跳过");

    // 常见安装位置。
    private static readonly string[] CommonDirs =
    {
        @"C:\Program Files\Java",
        @"C:\Program Files (x86)\Java",
        @"C:\Program Files\Eclipse Adoptium",
        @"C:\Program Files\Eclipse Foundation",
        @"C:\Program Files\Microsoft",
        @"C:\Program Files\Zulu",
        @"C:\Program Files\BellSoft",
        @"C:\Program Files\Amazon Corretto",
        @"C:\Program Files\Semeru",
        @"C:\Program Files\RedHat",
        @"C:\Program Files\SapMachine",
        @"C:\Program Files\Common Files\Oracle\Java",
    };

    // 注册表里可能写着 JavaHome 的位置。
    private static readonly string[] RegistryKeys =
    {
        @"SOFTWARE\JavaSoft\Java Development Kit",
        @"SOFTWARE\JavaSoft\Java Runtime Environment",
        @"SOFTWARE\JavaSoft\JRE",
        @"SOFTWARE\JavaSoft\JDK",
        @"SOFTWARE\WOW6432Node\JavaSoft\Java Development Kit",
        @"SOFTWARE\WOW6432Node\JavaSoft\Java Runtime Environment",
        @"SOFTWARE\Eclipse Adoptium\JDK",
        @"SOFTWARE\Eclipse Foundation\JDK",
        @"SOFTWARE\Microsoft\JDK",
    };

    private static readonly Regex VersionRe = new("version\\s+\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly string _cachePath;

    public JavaScanner(string configDir)
    {
        _cachePath = Path.Combine(configDir, CacheFileName);
    }

    // 读一个 java 的信息

    /// <summary>跑一次 `java -version` 把版本 / 位数 / 厂商读出来。</summary>
    public static JavaProbe ReadJavaInfo(string javaExe)
    {
        var probe = new JavaProbe(javaExe);
        string output;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-version");

            using var process = Process.Start(psi);
            if (process is null)
            {
                return probe with { Error = Localizer.Tr("无法执行：{err}", ("err", "Process.Start 返回 null")) };
            }

            // ⚠️ 必须先异步把两路读完再 WaitForExit，否则管道缓冲写满时
            // 子进程会卡在写、父进程卡在等 —— 经典死锁。
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(VersionTimeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // 已经退出了 / 杀不掉，都无所谓 —— 下面照样按超时处理
                }
                return probe with { Error = Localizer.Tr("运行超时") };
            }

            // 给异步读取一点收尾时间（进程退出了但管道里可能还有尾巴）
            Task.WaitAll(new Task[] { stdoutTask, stderrTask }, TimeSpan.FromSeconds(2));
            output = (stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty)
                     + (stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 文件不存在 / 没有执行权限，都归到这里
            return probe with { Error = Localizer.Tr("文件不存在") + $" ({ex.NativeErrorCode})" };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return probe with { Error = Localizer.Tr("无法执行：{err}", ("err", ex.Message)) };
        }

        var match = VersionRe.Match(output);
        if (!match.Success)
        {
            return probe with { Error = Localizer.Tr("无法解析版本") };
        }

        var version = match.Groups[1].Value;
        var parts = version.Split('.');
        int major;
        try
        {
            // "1.8.0_481" → 8 ；"17.0.9" → 17
            major = parts[0] == "1" && parts.Length > 1
                ? int.Parse(parts[1])
                : int.Parse(parts[0]);
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or OverflowException)
        {
            return probe with { Version = version, Error = Localizer.Tr("版本号看不懂：{ver}", ("ver", version)) };
        }

        // 厂商：取 "Runtime Environment" 那一行
        var vendor = string.Empty;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Contains("Runtime Environment", StringComparison.Ordinal))
            {
                vendor = line;
                break;
            }
        }
        if (vendor.Length == 0)
        {
            var vendorMatch = Regex.Match(output, "^(OpenJDK|Java\\(TM\\)|Microsoft|Zulu|Temurin).*",
                RegexOptions.Multiline);
            if (vendorMatch.Success)
            {
                vendor = vendorMatch.Value.Trim();
                if (vendor.Length > 60)
                {
                    vendor = vendor[..60];
                }
            }
        }

        // 位数：优先看输出里的字样，其次看进程本身
        var lower = output.ToLowerInvariant();
        string arch;
        if (lower.Contains("64-bit") || lower.Contains("x86_64") || lower.Contains("amd64"))
        {
            arch = "x86_64";
        }
        else if (lower.Contains("32-bit") || lower.Contains("i386"))
        {
            arch = "x86";
        }
        else
        {
            arch = Environment.Is64BitOperatingSystem ? "x86_64" : "x86";
        }

        return new JavaProbe(javaExe, version, major, vendor, arch);
    }

    // 找候选路径

    private static List<string> RegistryJavas()
    {
        var found = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            return found;
        }

        foreach (var subKey in RegistryKeys)
        {
            try
            {
                // 32 位视图也要看（WOW6432Node 已经显式列了，这里两个视图都开一遍更稳）
                using var key = Registry.LocalMachine.OpenSubKey(subKey);
                if (key is null)
                {
                    continue;
                }
                foreach (var versionName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var versionKey = key.OpenSubKey(versionName);
                        if (versionKey?.GetValue("JavaHome") is not string home || home.Length == 0)
                        {
                            continue;
                        }
                        var exe = Path.Combine(home, "bin", "java.exe");
                        if (File.Exists(exe))
                        {
                            found.Add(exe);
                        }
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                    {
                        // 单个子键读不了就跳过
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // 这个键不存在 / 没权限，正常
            }
        }
        return found;
    }

    /// <summary>在 base 下面找 `bin/java.exe`（默认往下两层）。</summary>
    private static List<string> DirJavas(string baseDir, int maxDepth = 2)
    {
        var found = new List<string>();
        if (!SafeDirectoryExists(baseDir))
        {
            return found;
        }

        void Walk(string dir, int depth)
        {
            if (depth > maxDepth)
            {
                return;
            }
            foreach (var sub in SafeEnumerateDirectories(dir))
            {
                var exe = Path.Combine(sub, "bin", "java.exe");
                if (SafeFileExists(exe))
                {
                    found.Add(exe);
                }
                Walk(sub, depth + 1);
            }
        }

        // 注意起点是 1：base 自己不算一层（`Program Files\Java\jdk-17\bin\java.exe`）
        Walk(baseDir, 1);
        return found;
    }

    private static List<string> PathEnvJavas()
    {
        var found = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }
            var exe = Path.Combine(entry.Trim(), "java.exe");
            if (SafeFileExists(exe))
            {
                found.Add(exe);
            }
        }
        return found;
    }

    /// <summary>扫 Minecraft / PCL 下载的自带运行时。</summary>
    private static List<string> MinecraftRuntimeJavas(IEnumerable<string>? mcDirs)
    {
        var found = new List<string>();
        var roots = new List<string>();

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (OperatingSystem.IsWindows() && !string.IsNullOrEmpty(appData))
        {
            roots.Add(Path.Combine(appData, ".minecraft", "runtime"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            roots.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "minecraft", "runtime"));
        }
        else
        {
            roots.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft", "runtime"));
        }

        foreach (var mcDir in mcDirs ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(mcDir))
            {
                roots.Add(Path.Combine(mcDir, "runtime"));
            }
        }

        foreach (var root in roots)
        {
            if (!SafeDirectoryExists(root))
            {
                continue;
            }
            foreach (var component in SafeEnumerateDirectories(root))
            {
                // 扁平布局（官方 + PCL 实测）
                var flat = Path.Combine(component, "bin", "java.exe");
                if (SafeFileExists(flat))
                {
                    found.Add(flat);
                    continue;
                }
                // 嵌套布局（老版官方启动器）
                foreach (var osArch in SafeEnumerateDirectories(component))
                {
                    var nested = Path.Combine(osArch, Path.GetFileName(component), "bin", "java.exe");
                    if (SafeFileExists(nested))
                    {
                        found.Add(nested);
                    }
                }
            }
        }
        return found;
    }

    /// <summary>所有可能的 java.exe 路径（去重、过滤掉 javaw.exe）。</summary>
    public static List<string> CandidatePaths(IEnumerable<string>? mcDirs = null)
    {
        var pool = new List<string>();
        pool.AddRange(PathEnvJavas());
        pool.AddRange(RegistryJavas());
        pool.AddRange(MinecraftRuntimeJavas(mcDirs));
        if (OperatingSystem.IsWindows())
        {
            foreach (var dir in CommonDirs)
            {
                pool.AddRange(DirJavas(dir));
            }
        }

        // 归一化路径，避免同一个 Java 被算两次（软链、大小写、`..` 都会造成重复）
        var unique = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in pool)
        {
            string resolved;
            try
            {
                resolved = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (seen.Add(resolved))
            {
                unique.Add(path);
            }
        }
        return unique;
    }

    // 缓存

    /// <summary>
    /// "还是不是同一个 java.exe"的依据：mtime + 大小；⚠️ 不能只看路径：同一个路径上重装了别的
    /// Java 是完全正常的 （卸载 8 装 17），只看路径会把旧版本号一直用下去。
    /// </summary>
    private static long[]? Fingerprint(string javaExe)
    {
        try
        {
            var info = new FileInfo(javaExe);
            return new[] { info.LastWriteTimeUtc.Ticks, info.Length };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool WithinRetryWindow(JavaCacheEntry entry)
        => entry.FailedAt > 0
           && (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - entry.FailedAt) < FailureRetrySeconds;

    private Dictionary<string, JavaCacheEntry> LoadCache()
    {
        try
        {
            var bytes = File.ReadAllBytes(_cachePath);
            var root = JsonIO.ParseBytes(bytes) as JsonObject;
            if (root is null || root["cache_version"]?.GetValue<int>() != CacheVersion)
            {
                return new Dictionary<string, JavaCacheEntry>();
            }
            if (root["entries"] is not JsonObject entries)
            {
                return new Dictionary<string, JavaCacheEntry>();
            }

            var result = new Dictionary<string, JavaCacheEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, node) in entries)
            {
                if (node is not JsonObject obj)
                {
                    continue;
                }

                // ⚠️ 探测结果嵌在 `info` 里，读写必须都走 `info`；结构对不上会让第二次启动起所有 Java 都显示不可用。
                var info = obj["info"] as JsonObject;

                result[key] = new JavaCacheEntry
                {
                    Ok = obj["ok"]?.GetValue<bool>() ?? false,
                    Fingerprint = obj["fp"] is JsonArray fp
                        ? fp.Where(x => x is not null).Select(x => x!.GetValue<long>()).ToArray()
                        : Array.Empty<long>(),
                    Version = info?["version"]?.GetValue<string>() ?? string.Empty,
                    Major = info?["major"]?.GetValue<int>() ?? 0,
                    Vendor = info?["vendor"]?.GetValue<string>() ?? string.Empty,
                    Architecture = info?["arch"]?.GetValue<string>() ?? string.Empty,
                    FailedAt = obj["failed_at"]?.GetValue<double>() ?? 0,
                };
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                      or InvalidOperationException or FormatException or NotSupportedException)
        {
            return new Dictionary<string, JavaCacheEntry>();
        }
    }

    private void SaveCache(Dictionary<string, JavaCacheEntry> entries)
    {
        var payload = new JsonObject
        {
            ["cache_version"] = CacheVersion,
            ["entries"] = new JsonObject(entries.Select(kv =>
                new KeyValuePair<string, JsonNode?>(kv.Key, new JsonObject
                {
                    ["ok"] = kv.Value.Ok,
                    ["fp"] = new JsonArray(kv.Value.Fingerprint.Select(x => JsonValue.Create(x)).ToArray()),
                    ["info"] = new JsonObject
                    {
                        ["version"] = kv.Value.Version,
                        ["major"] = kv.Value.Major,
                        ["vendor"] = kv.Value.Vendor,
                        ["arch"] = kv.Value.Architecture,
                    },
                    ["failed_at"] = kv.Value.FailedAt,
                }))),
        };

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var text = payload.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            // 原子写：先写 .tmp 再替换，中途断电不会留下半个文件
            var tmp = _cachePath + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            File.Move(tmp, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Java] 扫描缓存写不进去（{ex.Message}），下次还得重扫");
        }
    }

    // 对外接口

    /// <summary>扫描所有 Java 并读出版本信息，按主版本从高到低排。</summary>
    public List<JavaProbe> FindJavas(
        IEnumerable<string>? mcDirs = null,
        int maxWorkers = 8,
        Action<string>? onProgress = null,
        bool useCache = true,
        CancellationToken token = default)
    {
        var paths = CandidatePaths(mcDirs);
        onProgress?.Invoke(Localizer.Tr("正在检测 {n} 个候选…", ("n", paths.Count)));

        var cached = useCache ? LoadCache() : new Dictionary<string, JavaCacheEntry>(StringComparer.OrdinalIgnoreCase);
        var results = new List<JavaProbe>();
        var fresh = new Dictionary<string, JavaCacheEntry>(StringComparer.OrdinalIgnoreCase);
        var todo = new List<string>();

        foreach (var path in paths)
        {
            if (cached.TryGetValue(path, out var entry) && entry.Fingerprint.Length > 0)
            {
                var fingerprint = Fingerprint(path);
                var same = fingerprint is not null && entry.Fingerprint.SequenceEqual(fingerprint);

                if (same && entry.Ok)
                {
                    results.Add(new JavaProbe(path, entry.Version, entry.Major, entry.Vendor, entry.Architecture));
                    fresh[path] = entry;
                    continue;
                }
                // 上次探测失败：在重试窗口内不再花几秒去碰它
                if (same && WithinRetryWindow(entry))
                {
                    results.Add(new JavaProbe(path, Error: FailedCacheMessage));
                    fresh[path] = entry;
                    continue;
                }
            }
            todo.Add(path);
        }

        if (todo.Count > 0)
        {
            // 起 java 进程是阻塞 IO，用 Parallel 跑（比自建线程池省事，语义也一样）
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, maxWorkers),
                CancellationToken = token,
            };

            var probes = new JavaProbe?[todo.Count];
            try
            {
                Parallel.For(0, todo.Count, options, i =>
                {
                    try
                    {
                        probes[i] = ReadJavaInfo(todo[i]);
                    }
                    catch (Exception ex)
                    {
                        // 兜底：单个失败不影响整体
                        probes[i] = new JavaProbe(todo[i], Error: $"{ex.GetType().Name}: {ex.Message}");
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // 取消是用户的意思，不是错误
            }

            foreach (var probe in probes)
            {
                if (probe is null)
                {
                    continue;
                }
                results.Add(probe);
                var fingerprint = Fingerprint(probe.Path);
                if (fingerprint is null)
                {
                    continue;   // 连文件都 stat 不到，没什么可记的
                }
                fresh[probe.Path] = new JavaCacheEntry
                {
                    Ok = probe.Usable,
                    Fingerprint = fingerprint,
                    Version = probe.Version,
                    Major = probe.Major,
                    Vendor = probe.Vendor,
                    Architecture = probe.Architecture,
                    FailedAt = probe.Usable ? 0 : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                };
            }
        }

        if (useCache && !token.IsCancellationRequested)
        {
            SaveCache(fresh);
        }

        // 主版本从高到低；同版本按路径
        results.Sort((a, b) =>
        {
            var result = b.Major.CompareTo(a.Major);
            return result != 0 ? result : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        });
        return results;
    }

    /// <summary>按版本要求的 Java 主版本挑一个。</summary>
    public static (JavaProbe? Java, string Note) PickJava(
        IEnumerable<JavaProbe>? javas, int requiredMajor, string preferred = "")
    {
        var usable = (javas ?? Enumerable.Empty<JavaProbe>()).Where(j => j.Usable).ToList();

        JavaProbe? ignored = null;
        if (!string.IsNullOrEmpty(preferred))
        {
            foreach (var java in usable)
            {
                if (PathsEqual(java.Path, preferred))
                {
                    if (java.Major == requiredMajor)
                    {
                        return (java, Localizer.Tr("使用你指定的 Java"));
                    }
                    // 主版本对不上就**不硬来** —— 硬用的话，指定了 Java 8
                    // 会把所有要 17 的版本一起弄坏。自动挑，并在说明里讲清楚。
                    ignored = java;
                    break;
                }
            }
        }

        (JavaProbe?, string) Note(JavaProbe? java, string why)
        {
            if (ignored is not null)
            {
                why += Localizer.Tr("（你指定的 Java {n} 主版本对不上，已忽略）", ("n", ignored.Major));
            }
            return (java, why);
        }

        var exact = usable.Where(j => j.Major == requiredMajor).ToList();
        if (exact.Count > 0)
        {
            exact.Sort((a, b) =>
            {
                var result = b.Is64Bit.CompareTo(a.Is64Bit);
                return result != 0 ? result : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            });
            return Note(exact[0], Localizer.Tr("自动选择（要求 Java {n}）", ("n", requiredMajor)));
        }

        var newer = usable.Where(j => j.Major > requiredMajor).ToList();
        if (newer.Count > 0)
        {
            newer.Sort((a, b) =>
            {
                var result = a.Major.CompareTo(b.Major);
                if (result != 0) return result;
                result = b.Is64Bit.CompareTo(a.Is64Bit);
                return result != 0 ? result : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            });
            return Note(newer[0], Localizer.Tr("没有 Java {need}，用 Java {have} 顶替（可能起不来）",
                ("need", requiredMajor), ("have", newer[0].Major)));
        }

        return Note(null, Localizer.Tr("没找到 Java {n}，请先安装", ("n", requiredMajor)));
    }

    /// <summary>版本 JSON 要求的主版本；老版本没有 javaVersion 字段就按 8 算。</summary>
    public static int RequiredMajor(VersionJson? versionJson, int fallback = 8)
        => versionJson is null || versionJson.JavaMajor <= 0 ? fallback : versionJson.JavaMajor;

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    // 安全的文件系统探测

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static List<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateDirectories(path).ToList()
                : new List<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }
}
