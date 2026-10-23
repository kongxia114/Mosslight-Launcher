using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mosslight.Core.Services.Engine;

// 跑官方安装器的加载器（Forge / NeoForge / OptiFine）的落地细节。
// ⚠️ 需要 launcher_profiles.json；OptiFine 要用 `-cp <jar> optifine.Installer` 才不弹窗，且只认 %APPDATA%。
// ⚠️ 安装器输出可达 3 万行，必须过滤后再进日志；OptiFine 改名时连打过补丁的客户端 jar 一起改。

/// <summary>装不上（信息是给人看的：会显示在界面状态行上）。</summary>
public sealed class LoaderSetupException : Exception
{
    public LoaderSetupException(string message) : base(message)
    {
    }
}

/// <summary>跑安装器 / 拼地址这些落地细节。</summary>
public static class LoaderInstaller
{
    private const string Bmclapi = "https://bmclapi2.bangbang93.com";

    public const int DefaultTimeoutSeconds = 7200;

    public const string OptifineHeadlessClass = "optifine.Installer";

    // 没有 launcher_profiles.json 时造一份最小的（安装器只要求它存在且是个 JSON）。
    private const string LauncherProfilesJson = """
        {
          "profiles": {},
          "settings": {
            "enableSnapshots": false,
            "enableAdvanced": false
          },
          "version": 3
        }
        """;

    private static readonly string[] LegacyForgeVersions =
    {
        "1.8.9", "1.8.8", "1.8", "1.7.10", "1.7.2", "1.6.4", "1.6.2", "1.5.2",
    };

    public static string ForgeMavenUrl(string mcVersion, string loaderVersion)
    {
        var mc = (mcVersion ?? string.Empty).Trim();
        var lv = (loaderVersion ?? string.Empty).Trim();
        var artifact = LegacyForgeVersions.Contains(mc) ? $"{mc}-{lv}-{mc}" : $"{mc}-{lv}";
        return $"https://maven.minecraftforge.net/net/minecraftforge/forge/{artifact}/forge-{artifact}-installer.jar";
    }

    /// <summary>这个加载器的安装器从哪下。</summary>
    public static string InstallerUrl(
        string mcVersion, string key, string loaderVersion,
        string? optifineType = null, string? optifinePatch = null, string? package = null)
    {
        key = (key ?? string.Empty).ToLowerInvariant();
        var mc = (mcVersion ?? string.Empty).Trim();
        var lv = (loaderVersion ?? string.Empty).Trim();

        switch (key)
        {
            case "forge":
            {
                // 手写 query：`mcversion` / `version` / `category` / `format` 四个都要
                var query = $"mcversion={Uri.EscapeDataString(mc)}" +
                            $"&version={Uri.EscapeDataString(lv)}" +
                            "&category=installer&format=jar";
                return $"{Bmclapi}/forge/download?{query}";
            }

            case "neoforge":
            {
                // 1.20.1 那代的 NeoForge 走 `net/neoforged/forge/`（历史命名，
                // 版本号是 47.1.x —— 那时它还叫 "Forge 的分支"）。
                // 1.20.2 起才改成 `net/neoforged/neoforge/`。
                // 不用调用方传，按版本号自己认：省得每个调用点都要记得这条历史。
                var pkg = package;
                if (string.IsNullOrEmpty(pkg))
                {
                    pkg = mc == "1.20.1" || lv.StartsWith("47.", StringComparison.Ordinal)
                        ? "forge"
                        : "neoforge";
                }

                // ⚠️ 1.20.1 那批在 maven 里的**产物版本号带着游戏版本前缀**：
                // `/net/neoforged/forge/1.20.1-47.1.106/forge-1.20.1-47.1.106-installer.jar`
                // 而 BMCLAPI 的版本列表给的是 `47.1.106`，所以这里要自己拼回去 ——
                // 直接拿 `47.1.106` 去问是 404（踩过）。
                var artifact = lv;
                if (pkg == "forge" && !lv.StartsWith(mc + "-", StringComparison.Ordinal))
                {
                    artifact = $"{mc}-{lv}";
                }
                return $"https://maven.neoforged.net/releases/net/neoforged/{pkg}/{artifact}/" +
                       $"{pkg}-{artifact}-installer.jar";
            }

            case "optifine":
            {
                var type = (optifineType ?? string.Empty).Trim();
                var patch = (optifinePatch ?? string.Empty).Trim();
                if (type.Length == 0 || patch.Length == 0)
                {
                    throw new LoaderSetupException(
                        Localization.Localizer.Tr("OptiFine 的版本数据里缺少 type / patch，拼不出下载地址"));
                }
                return $"{Bmclapi}/optifine/{mc}/{type}/{patch}";
            }

            default:
                throw new LoaderSetupException(Localization.Localizer.Tr(
                    "{name} 的安装器还没有做",
                    ("name", LoaderCatalog.LoaderNames.TryGetValue(key, out var n) ? n : key)));
        }
    }

    // 「只下载安装器」用的 HTTP 客户端（安装器动辄几 MB，超时给宽松点）。
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(DownloadManager.UserAgent);
        return client;
    }

    /// <summary>这一家有没有"安装器文件"可以单独下载。</summary>
    public static bool HasInstallerFile(string? key)
        => (key ?? string.Empty).ToLowerInvariant()
           is "forge" or "neoforge" or "optifine" or "cleanroom" or "liteloader" or "labymod";

    // LiteLoader 安装器还在 redist 上的那几版（build 号照官方目录）。
    private static readonly Dictionary<string, string> LiteLoaderRedistBuilds = new(StringComparer.Ordinal)
    {
        ["1.5.2"] = "01",
        ["1.6.2"] = "04",
        ["1.6.4"] = "01",
        ["1.7.2"] = "04",
        ["1.7.10"] = "04",
    };

    /// <summary>「只下载」那条路要的地址 + 文件名。</summary>
    private static bool TryDownloadOnlyTarget(
        string key,
        string mcVersion,
        string loaderVersion,
        string? optifineType,
        string? optifinePatch,
        string? channel,
        out string url,
        out string fileName)
    {
        url = string.Empty;
        fileName = string.Empty;

        switch (key)
        {
            case "liteloader":
            {
                if (!LiteLoaderRedistBuilds.TryGetValue(mcVersion, out var build))
                {
                    return false;
                }
                fileName = $"liteloader-installer-{mcVersion}-{build}.jar";
                url = $"https://dl.liteloader.com/redist/{mcVersion}/{fileName}";
                return true;
            }
            case "labymod":
            {
                var ch = string.IsNullOrEmpty(channel) ? LabyModCatalog.ProductionChannel : channel;
                var isSnapshot = string.Equals(ch, LabyModCatalog.SnapshotChannel, StringComparison.Ordinal);
                fileName = isSnapshot ? "LabyMod4SnapshotInstaller.jar" : "LabyMod4ProductionInstaller.jar";
                url = $"https://releases.labymod.net/api/v1/installer/{(isSnapshot ? "snapshot" : "production")}/java";
                return true;
            }
            case "cleanroom":
                fileName = InstallerFileName(key, mcVersion, loaderVersion);
                url = CleanroomCatalog.InstallerUrl(loaderVersion);
                return true;
            default:
            {
                // Forge / NeoForge / OptiFine：沿用完整安装那条路算出来的地址和文件名
                var fileVersion = key == "optifine" && !string.IsNullOrEmpty(optifinePatch)
                    ? optifinePatch
                    : loaderVersion;
                fileName = InstallerFileName(key, mcVersion, fileVersion);
                url = InstallerUrl(mcVersion, key, loaderVersion, optifineType, optifinePatch);
                return true;
            }
        }
    }

    /// <summary>「只下载」那条路会存成什么文件名（没有可下载文件的版本返回空串）。</summary>
    public static string DownloadInstallerName(
        string key, string mcVersion, string loaderVersion, string? channel = null)
    {
        try
        {
            return TryDownloadOnlyTarget(
                (key ?? string.Empty).ToLowerInvariant(), mcVersion, loaderVersion,
                null, null, channel, out _, out var fileName)
                ? fileName
                : string.Empty;
        }
        catch (LoaderSetupException)
        {
            return string.Empty;
        }
    }

    /// <summary>「只下载安装器」的结果。</summary>
    public sealed record InstallerDownload(bool Ok, bool NotSupported, string Path, string Message);

    /// <summary>**只把安装器 jar 下下来**：不跑 Java、不装版本、不碰 launcher_profiles。</summary>
    public static async Task<InstallerDownload> DownloadInstallerAsync(
        string key,
        string mcVersion,
        string loaderVersion,
        string destinationDir,
        string? optifineType = null,
        string? optifinePatch = null,
        string? channel = null,
        Action<string>? onProgress = null,
        CancellationToken token = default)
    {
        var lower = (key ?? string.Empty).ToLowerInvariant();
        if (!HasInstallerFile(lower))
        {
            return new InstallerDownload(false, true, string.Empty,
                Localization.Localizer.Tr(
                    "这家暂无安装器文件可下载（安装器版本跟 loader 版本对不上）"));
        }

        string url;
        string fileName;
        try
        {
            if (!TryDownloadOnlyTarget(
                    lower, mcVersion, loaderVersion, optifineType, optifinePatch, channel,
                    out url, out fileName))
            {
                return new InstallerDownload(false, true, string.Empty,
                    Localization.Localizer.Tr(
                        "这个版本没有可下载的安装器（{name} 只留了几版）",
                        ("name", LoaderCatalog.LoaderNames.TryGetValue(lower, out var title) ? title : key)));
            }
        }
        catch (LoaderSetupException ex)
        {
            return new InstallerDownload(false, true, string.Empty, ex.Message);
        }

        var target = Path.Combine(destinationDir, fileName);
        if (File.Exists(target))
        {
            return new InstallerDownload(true, false, target,
                Localization.Localizer.Tr("已下载到：{path}", ("path", target)));
        }

        try
        {
            Directory.CreateDirectory(destinationDir);
            onProgress?.Invoke(Localization.Localizer.Tr("正在下载安装器…"));
            var bytes = await Http.GetByteArrayAsync(url, token).ConfigureAwait(false);

            // 先写临时文件再改名：中途断了不会留半个 jar 冒充"已下载"
            var temp = target + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, token).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);

            return new InstallerDownload(true, false, target,
                Localization.Localizer.Tr("已下载到：{path}", ("path", target)));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or IOException or UnauthorizedAccessException)
        {
            return new InstallerDownload(false, false, string.Empty,
                Localization.Localizer.Tr("安装器没下下来：{err}", ("err", ex.Message)));
        }
    }

    /// <summary>安装器 jar 存成什么名字（放临时目录里）。</summary>
    public static string InstallerFileName(string? key, string mcVersion, string loaderVersion)
        => (key ?? string.Empty).ToLowerInvariant() switch
        {
            "forge" => $"forge-{mcVersion}-{loaderVersion}-installer.jar",
            "neoforge" => $"neoforge-{loaderVersion}-installer.jar",
            "optifine" => $"OptiFine-{mcVersion}-{loaderVersion}-installer.jar",
            _ => $"{key}-{loaderVersion}-installer.jar",
        };

    /// <summary>
    /// 安装器 jar 放哪：`&lt;游戏目录&gt;/.mosslight/installers/`；⚠️ **不自动删**：一个 5~6 MB，
    /// 名字里带着版本号，装过的不会再下一遍，但"装了一半失败"时留着它才有得查（重试也不用重下）。
    /// </summary>
    public static string InstallerDir(string mcDir)
        => Path.Combine(mcDir, ".mosslight", "installers");

    // launcher_profiles.json

    /// <summary>
    /// 游戏目录里没有 `launcher_profiles.json` 就造一份最小可用的；⚠️ 已存在就**一个字节都不动**：
    /// 那里面是用户在官方启动器里的档案，我们没资格改。
    /// </summary>
    public static bool EnsureLauncherProfiles(string mcDir)
    {
        var path = Path.Combine(mcDir, "launcher_profiles.json");
        if (File.Exists(path))
        {
            return false;
        }
        try
        {
            Directory.CreateDirectory(mcDir);
            File.WriteAllText(path, LauncherProfilesJson + "\n", new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LoaderSetupException(Localization.Localizer.Tr(
                "建不了 {path}（安装器要求有它）：{err}", ("path", path), ("err", ex.Message)));
        }
    }

    // 跑安装器

    /// <summary>
    /// 安装器的命令行参数（Forge / NeoForge 官方都是 `--installClient &lt;目录&gt;`）。
    /// </summary>
    public static List<string> InstallerArguments(string? key, string mcDir)
        => (key ?? string.Empty).ToLowerInvariant() switch
        {
            "forge" or "neoforge" => new List<string> { "--installClient", mcDir },
            // ⚠️ OptiFine 的安装器**不吃参数**：它把主类写成 Swing 窗口，
            // 但里面留了 `public static void main` —— 它调的是
            // `doInstall(Utils.getWorkingDirectory())`，也就是**认当前工作目录**。
            // 所以参数为空，靠 cwd / APPDATA 把它指到目标游戏目录。
            "optifine" => new List<string>(),
            _ => throw new LoaderSetupException(Localization.Localizer.Tr(
                "{name} 的安装器还没有做",
                ("name", LoaderCatalog.LoaderNames.TryGetValue(
                    (key ?? string.Empty).ToLowerInvariant(), out var n) ? n : key))),
        };

    /// <summary>这个 jar 里有没有某个类（决定 OptiFine 走无窗口入口还是开窗口）。</summary>
    public static bool HasClass(string jarPath, string dotted)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            var wanted = dotted.Replace('.', '/') + ".class";
            return zip.Entries.Any(e => e.FullName == wanted);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>跑安装器的完整命令行。</summary>
    public static List<string> InstallerCommand(string javaPath, string installerJar, string mcDir, string? key)
    {
        if ((key ?? string.Empty).ToLowerInvariant() == "optifine"
            && HasClass(installerJar, OptifineHeadlessClass))
        {
            return new List<string> { javaPath, "-cp", installerJar, OptifineHeadlessClass };
        }
        var cmd = new List<string> { javaPath, "-jar", installerJar };
        cmd.AddRange(InstallerArguments(key, mcDir));
        return cmd;
    }

    /// <summary>安装器的输出收集器。</summary>
    public sealed record InstallerRunResult(
        int ReturnCode,
        List<string> Lines,
        int Spam,
        string Last,
        string Hint,
        string Error,
        double Seconds);

    /// <summary>跑安装器，逐行收输出（**已经节流**，不会刷屏）。</summary>
    public static InstallerRunResult RunInstaller(
        string javaPath,
        string installerJar,
        string mcDir,
        string key = "forge",
        Action<string>? onLine = null,
        int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken token = default,
        string? logPath = null)
    {
        if (!File.Exists(installerJar))
        {
            throw new LoaderSetupException(Localization.Localizer.Tr(
                "安装器文件不见了：{path}", ("path", installerJar)));
        }

        var cmd = InstallerCommand(javaPath, installerJar, mcDir, key);
        var psi = new ProcessStartInfo
        {
            FileName = cmd[0],
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(installerJar))!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        for (var i = 1; i < cmd.Count; i++)
        {
            psi.ArgumentList.Add(cmd[i]);
        }

        // OptiFine：它自己 `getWorkingDirectory()` 在 Windows 上读 `%APPDATA%`，
        // 拼出 `<APPDATA>/.minecraft`。我们把 APPDATA 指到"游戏目录的上一级"，
        // 它就会装进我们要的那个 .minecraft 里。
        if ((key ?? string.Empty).ToLowerInvariant() == "optifine")
        {
            psi.Environment["APPDATA"] = Path.GetDirectoryName(Path.GetFullPath(mcDir)) ?? mcDir;
            if (cmd.Contains("-jar"))
            {
                // 兜底那条路（老版本没有 `optifine.Installer`）只能开窗口，
                // 得**明说**，不然用户看着一个窗口发呆
                onLine?.Invoke(Localization.Localizer.Tr(
                    "OptiFine 的安装器会弹出一个窗口，请点一下「Install」"));
            }
        }

        var started = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        var collector = new InstallerOutput(onLine);
        var gate = new object();

        // ⚠️ 安装器输出必须原样写进文件，不要只留过滤后的最后一句；真正原因常在前几行。
        StreamWriter? log = null;
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath!))!);
                log = new StreamWriter(logPath!, append: false, new UTF8Encoding(false))
                {
                    AutoFlush = true,
                };
                log.WriteLine($"# Mosslight 安装器日志");
                log.WriteLine($"# 时间   ：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
                log.WriteLine($"# 加载器 ：{key}");
                log.WriteLine($"# 命令   ：{string.Join(' ', cmd)}");
                log.WriteLine($"# 游戏目录：{mcDir}");
                log.WriteLine($"# 工作目录：{psi.WorkingDirectory}");
                log.WriteLine(new string('-', 72));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or ArgumentException or NotSupportedException)
            {
                log = null;   // 写不了日志不该让安装失败
            }
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (gate)
            {
                collector.Feed(e.Data);
                log?.WriteLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (gate)
            {
                collector.Feed(e.Data);
                log?.WriteLine(e.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                throw new LoaderSetupException(Localization.Localizer.Tr(
                    "起不了安装器（{java}）", ("java", javaPath)));
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // ⚠️ 超时按**整体**算：实测 Forge 1.20.1 要 200 秒
            var deadline = TimeSpan.FromSeconds(timeoutSeconds);
            while (!process.WaitForExit(500))
            {
                if (token.IsCancellationRequested)
                {
                    TryKill(process);
                    throw new OperationCanceledException(token);
                }
                if (started.Elapsed > deadline)
                {
                    TryKill(process);
                    throw new LoaderSetupException(Localization.Localizer.Tr(
                        "安装器超过 {n} 分钟没装完，先停下了", ("n", timeoutSeconds / 60)));
                }
            }
            // 等异步读取把管道里的尾巴收干净
            process.WaitForExit();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new LoaderSetupException(Localization.Localizer.Tr(
                "起不了安装器（{java}）：{err}", ("java", javaPath), ("err", ex.Message)));
        }
        finally
        {
            // ⚠️ 放 finally：失败 / 取消 / 超时那几条路也得把日志收掉，
            // 否则"装到一半炸了"恰恰是**最需要日志**的时候却没有文件。
            try
            {
                log?.Flush();
                log?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _ = ex;
            }
        }

        int rc;
        try
        {
            rc = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            rc = -1;
        }

        var result = collector.Result();
        var hint = FailureHint(result.Lines);
        return new InstallerRunResult(
            ReturnCode: rc,
            Lines: result.Lines,
            Spam: result.Spam,
            Last: result.Last,
            Hint: hint,
            // ⚠️ 只有**失败**时才给"原因"：成功时最后一行是
            // `Successfully installed client into launcher.`，当成错误显示会很好笑
            Error: rc == 0 ? string.Empty : hint,
            Seconds: started.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// 安装器日志的落点：
    /// `&lt;启动器目录&gt;\Logs\installer-&lt;加载器&gt;-&lt;游戏版本&gt;-&lt;时间&gt;.log`；⚠️
    /// 跟 `launcher.log` 放同一个 `Logs\` 目录：用户要找日志时只会想到那一个地方。
    /// </summary>
    public static string InstallerLogPath(string baseDir, string key, string mcVersion)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var safeKey = string.Join("-", (key ?? "loader").Split(Path.GetInvalidFileNameChars()));
        var safeMc = string.Join("-", (mcVersion ?? "mc").Split(Path.GetInvalidFileNameChars()));
        var dir = Path.Combine(string.IsNullOrWhiteSpace(baseDir) ? "." : baseDir, "Logs");
        return Path.Combine(dir, $"installer-{safeKey}-{safeMc}-{stamp}.log");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                      or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // 已经退出了 / 杀不掉，都无所谓
        }
    }

    public static string FailureHint(IReadOnlyList<string> lines)
    {
        var generic = new[] { "There was an error during installation" };
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var text = lines[i].Trim();
            if (text.Length == 0
                || InstallerOutput.SpamPrefixes.Any(p => text.StartsWith(p, StringComparison.Ordinal)))
            {
                continue;
            }
            if (generic.Any(g => text.Contains(g, StringComparison.Ordinal)))
            {
                continue;
            }
            return text;
        }
        return string.Empty;
    }

    // 装完校验 / 改名

    /// <summary>装完校验：这个版本目录**真的能用**吗。</summary>
    public static (bool Ok, string Why) VerifyInstalled(string mcDir, string versionId, string mcId = "")
    {
        _ = mcId;
        var folder = Path.Combine(mcDir, "versions", versionId);
        var path = Path.Combine(folder, versionId + ".json");
        if (!File.Exists(path))
        {
            return (false, Localization.Localizer.Tr("没有版本 JSON：{path}", ("path", path)));
        }

        JsonObject? data;
        try
        {
            data = JsonIO.ParseBytes(File.ReadAllBytes(path)) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException
                                      or UnauthorizedAccessException or NotSupportedException)
        {
            return (false, Localization.Localizer.Tr("版本 JSON 读不了：{err}", ("err", ex.Message)));
        }

        if (data is null || string.IsNullOrEmpty(data["id"]?.GetValue<string>()))
        {
            return (false, Localization.Localizer.Tr("版本 JSON 里没有 id"));
        }
        if (!string.IsNullOrEmpty(data["mainClass"]?.GetValue<string>()))
        {
            return (true, string.Empty);
        }

        var parent = data["inheritsFrom"]?.GetValue<string>() ?? string.Empty;
        if (parent.Length > 0 &&
            File.Exists(Path.Combine(mcDir, "versions", parent, parent + ".json")))
        {
            return (true, string.Empty);
        }
        if (parent.Length == 0)
        {
            return (false, Localization.Localizer.Tr("JSON 里既没有主类、也没有 inheritsFrom"));
        }
        return (false, Localization.Localizer.Tr(
            "它继承的原版 {mc} 还没装（{path} 不存在）", ("mc", parent), ("path", parent)));
    }

    public static string? FindInstalledVersion(
        string mcDir, string mcVersion, string loaderKey, string loaderVersion)
    {
        var key = (loaderKey ?? string.Empty).Trim().ToLowerInvariant();
        var mc = (mcVersion ?? string.Empty).Trim();
        var lv = (loaderVersion ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            return null;
        }

        var versionsDir = Path.Combine(mcDir, "versions");
        List<string> folders;
        try
        {
            if (!Directory.Exists(versionsDir))
            {
                return null;
            }
            folders = Directory.EnumerateDirectories(versionsDir)
                .Select(Path.GetFileName)
                .Where(x => !string.IsNullOrEmpty(x))
                .Select(x => x!)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // 先看我们的约定名：最常见，命中就不用扫一遍
        var exact = LoaderCatalog.VersionFolderName(mc, key, lv);
        if (folders.Contains(exact, StringComparer.OrdinalIgnoreCase) && VerifyInstalled(mcDir, exact).Ok)
        {
            return exact;
        }

        foreach (var folder in folders)
        {
            var (folderMc, folderKey, folderVersion) = LoaderCatalog.ParseVersionFolderName(folder);
            if (!string.Equals(folderMc, mc, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(folderKey, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 版本号可能写法略有差别（`47.4.10` / `47.4.10.0`）：前缀对上就算
            if (lv.Length > 0
                && !folderVersion.Equals(lv, StringComparison.OrdinalIgnoreCase)
                && !folderVersion.StartsWith(lv + ".", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (VerifyInstalled(mcDir, folder).Ok)
            {
                return folder;
            }
        }

        // ⚠️ 第二遍认安装器自己起的名字（如 `neoforge-20.4.253`）；必须校验目录名含加载器版本，避免覆盖同加载器的其他版本。
        if (lv.Length > 0)
        {
            var names = new[]
                {
                    LoaderCatalog.FolderNames.TryGetValue(key, out var folderName) ? folderName : key,
                    key,
                }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in folders)
            {
                if (!names.Any(name => folder.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase))
                    || !folder.Contains(lv, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (VerifyInstalled(mcDir, folder).Ok)
                {
                    return folder;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 把安装器起的名字改成我们要的名字，返回最终版本名；⚠️ 只在 `new_id` 和 `old_id` 不一样、而
    /// 且**目标还不存在**时才动；目标已存在就原样返回 `old_id`（宁可名字难看，也不能覆盖用户已有
    /// 的版本）。
    /// </summary>
    public static bool EnsureInstanceJar(string mcDir, string versionId, string? mcVersion = null)
    {
        var versionDir = Path.Combine(mcDir, "versions", versionId);
        var target = Path.Combine(versionDir, versionId + ".jar");
        if (File.Exists(target) || !Directory.Exists(versionDir))
        {
            return false;
        }

        var jsonPath = Path.Combine(versionDir, versionId + ".json");
        JsonObject? json = null;
        if (File.Exists(jsonPath))
        {
            try
            {
                json = JsonIO.ParseBytes(File.ReadAllBytes(jsonPath)) as JsonObject;
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                json = null;
            }
        }

        // 源：父版本（原版）的 jar；`mcVersion` 没给就从 JSON 的 inheritsFrom 读
        var parentId = (mcVersion ?? string.Empty).Trim();
        if (parentId.Length == 0)
        {
            parentId = json?["inheritsFrom"]?.GetValue<string>() ?? string.Empty;
        }
        if (parentId.Length == 0)
        {
            return false;
        }

        var source = Path.Combine(mcDir, "versions", parentId, parentId + ".jar");
        if (!File.Exists(source))
        {
            return false;
        }

        try
        {
            File.Copy(source, target, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        // JSON 的 `jar` 指回自己（不指的话启动时还是会去找父版本那个）
        if (json is not null)
        {
            json["jar"] = versionId;
            try
            {
                File.WriteAllText(jsonPath, json.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }), new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = ex;
            }
        }

        return true;
    }

    public static string RenameVersion(string mcDir, string oldId, string newId)
    {
        oldId = (oldId ?? string.Empty).Trim();
        newId = (newId ?? string.Empty).Trim();
        if (oldId.Length == 0 || newId.Length == 0 || oldId == newId)
        {
            return oldId;
        }

        var versions = Path.Combine(mcDir, "versions");
        var src = Path.Combine(versions, oldId);
        var dst = Path.Combine(versions, newId);
        if (!Directory.Exists(src) || Directory.Exists(dst) || File.Exists(dst))
        {
            return oldId;
        }

        try
        {
            Directory.Move(src, dst);

            var oldJson = Path.Combine(dst, oldId + ".json");
            var newJson = Path.Combine(dst, newId + ".json");
            if (File.Exists(oldJson))
            {
                var data = JsonIO.ParseBytes(File.ReadAllBytes(oldJson)) as JsonObject ?? new JsonObject();
                data["id"] = newId;
                foreach (var field in new[] { "jar", "inheritsFrom" })
                {
                    if (data[field]?.GetValue<string>() == oldId)
                    {
                        data[field] = newId;
                    }
                }
                File.WriteAllText(newJson,
                    data.ToJsonString(new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }) + "\n", new UTF8Encoding(false));
                if (!string.Equals(newJson, oldJson, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(oldJson);
                }
            }

            var oldJar = Path.Combine(dst, oldId + ".jar");
            var newJar = Path.Combine(dst, newId + ".jar");
            if (File.Exists(oldJar) && !File.Exists(newJar))
            {
                File.Move(oldJar, newJar);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.Text.Json.JsonException)
        {
            throw new LoaderSetupException(Localization.Localizer.Tr(
                "改名失败（{old} → {new}）：{err}",
                ("old", oldId), ("new", newId), ("err", ex.Message)));
        }

        return newId;
    }

    /// <summary>这个 jar 是安装器吗（装完顺手检查一下，别把半个 jar 当真）。</summary>
    public static bool IsInstallerJar(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var names = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
            return names.Contains("install_profile.json") || names.Contains("version.json");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // 替安装器先把它的库下好

    /// <summary>安装器**自己要下的那些库** → 变成我们下载引擎的任务列表。</summary>
    public static List<InstallTask> InstallerLibraryTasks(string installerJar, string mcDir)
    {
        var tasks = new List<InstallTask>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ⚠️ 安装器里的 `install_profile.json` 和 `version.json` 两份库清单都要读，二者不完全重叠，缺一份会漏启动库。
        foreach (var entryName in new[] { "install_profile.json", "version.json" })
        {
            var profile = ReadJsonEntry(installerJar, entryName);
            if (profile?["libraries"] is not JsonArray libraries)
            {
                continue;
            }

            foreach (var node in libraries)
            {
                if (node is not JsonObject lib)
                {
                    continue;
                }

                var artifact = (lib["downloads"] as JsonObject)?["artifact"] as JsonObject;
                var url = artifact?["url"]?.GetValue<string>();
                var rel = artifact?["path"]?.GetValue<string>();
                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(rel))
                {
                    continue;
                }
                // 两份清单里重复的（大多数）只下一次
                if (!seen.Add(rel!))
                {
                    continue;
                }

                tasks.Add(new InstallTask(
                    Url: url!,
                    Path: Path.Combine(mcDir, "libraries", rel!.Replace('/', Path.DirectorySeparatorChar)),
                    Sha1: artifact?["sha1"]?.GetValue<string>() ?? string.Empty,
                    Size: LongOf(artifact?["size"]),
                    Label: Path.GetFileName(rel!),
                    Kind: Localization.Localizer.Tr("库")));
            }
        }

        return tasks;
    }

    /// <summary>从安装器 zip 里读一份 JSON（读不出来就 null，不让安装失败）。</summary>
    private static JsonObject? ReadJsonEntry(string archivePath, string entryName)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            var entry = zip.GetEntry(entryName);
            if (entry is null)
            {
                return null;
            }
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
                                      or System.Text.Json.JsonException or UnauthorizedAccessException
                                      or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// 读体积（`size`）；⚠️ 有些安装器把 `size` 写成字符串，直接 `GetValue&lt;long&gt;()` 会抛，
    /// 这里两种都认，认不出就当 0。
    /// </summary>
    private static long LongOf(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return 0;
        }
        try
        {
            return value.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.Number => value.GetValue<long>(),
                System.Text.Json.JsonValueKind.String
                    => long.TryParse(value.GetValue<string>(), out var parsed) ? parsed : 0,
                _ => 0,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return 0;
        }
    }
}
/// <summary>收安装器的输出：**节流** + 挑出错在哪。</summary>
public sealed class InstallerOutput
{
    private static readonly string[] SpamPrefixesArray =
    {
        "Patching ", "  Patching ",
        "Reading patch ", "  Reading patch ",
        "Data  ", "  Data  ",
        "Cant Find Class",
        // OptiFine 那个安装器（4000 行里大部分是它）：一条一条报"这个类跟原来一样"
        "Same: ",
    };

    /// <summary>刷屏行的前缀名单（LoaderInstaller.FailureHint 也用同一份）。</summary>
    public static IReadOnlyList<string> SpamPrefixes => SpamPrefixesArray;

    private static readonly Regex ListingRe =
        new(@"^[A-Za-z_$][\w$]*(?:/[\w$.]+)+$", RegexOptions.Compiled);

    // 心跳：就算一直在刷屏，也每 N 行报一次进度（让界面知道它还活着）。
    private const int HeartbeatEvery = 200;

    private const int ForwardLimit = 300;

    private readonly Action<string>? _onLine;

    public InstallerOutput(Action<string>? onLine)
    {
        _onLine = onLine;
    }

    public List<string> Lines { get; } = new();

    public int Spam { get; private set; }

    public int Total { get; private set; }

    public int Forwarded { get; private set; }

    public bool Capped { get; private set; }

    public string Last { get; private set; } = string.Empty;

    /// <summary>这行是不是"一个文件/类一行"那种列表项。</summary>
    public static bool LooksLikeListing(string line)
    {
        var text = (line ?? string.Empty).Trim();
        return text.Length > 0 && ListingRe.IsMatch(text);
    }

    /// <summary>是不是刷屏行（前缀名单或形状）。</summary>
    public static bool LooksLikeSpam(string line)
    {
        var text = line ?? string.Empty;
        return SpamPrefixesArray.Any(p => text.StartsWith(p, StringComparison.Ordinal))
               || LooksLikeListing(text);
    }

    public void Feed(string? line)
    {
        line = (line ?? string.Empty).TrimEnd();
        if (line.Length == 0)
        {
            return;
        }
        Last = line;
        Lines.Add(line);
        Total++;

        var spam = LooksLikeSpam(line);
        if (spam)
        {
            Spam++;
        }

        if (_onLine is null)
        {
            return;
        }

        // 心跳按**总行数**算（不是只按刷屏行）—— 万一刷屏行一条都没被名单
        // 认出来，心跳也得照发，不然界面看着像死了
        if (Total % HeartbeatEvery == 0)
        {
            _onLine(Localization.Localizer.Tr("还在这装…（已处理 {n} 行）", ("n", Total)));
            return;
        }
        if (spam)
        {
            return;
        }
        if (Forwarded >= ForwardLimit)
        {
            if (!Capped)
            {
                Capped = true;      // 只说一次，别把这句话本身变成刷屏
                _onLine(Localization.Localizer.Tr("安装器的输出太多了，后面只报进度"));
            }
            return;
        }
        Forwarded++;
        _onLine(line);
    }

    public (List<string> Lines, int Spam, string Last) Result() => (Lines, Spam, Last);
}
