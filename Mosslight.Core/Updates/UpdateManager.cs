using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosslight.Core.Updates;

/// <summary>一个可用更新（从 Release 的 JSON 里解析出来的那一份）。</summary>
public sealed record UpdateInfo(
    string Tag,
    Version Version,
    string ZipName,
    string ZipUrl,
    long ZipSize,
    string? Sha256,
    string ReleaseUrl,
    bool IsForced = false,
    string Notes = "");

/// <summary>检查结果。<see cref="Available"/> 为 false 时 <see cref="Info"/> 是 null。</summary>
public sealed record UpdateCheckResult(bool Available, UpdateInfo? Info, string Message);

/// <summary>下载 + 校验的结果。</summary>
public sealed record UpdateDownloadResult(bool Ok, string ZipPath, string Message);

/// <summary>
/// 自动更新：检查 → 下载 → SHA256 校验 → 交给 PowerShell 脚本替换 → 脚本把启动器拉起来。
///
/// ## 为什么替换要甩给脚本
/// 程序在跑的时候没法覆盖自己的 exe（文件锁）。脚本里先 `WaitForExit` 再解压，
/// 然后重启，最后把自己删掉 —— 这是 Windows 上不借助额外安装器最稳的做法。
///
/// ## 三层安全闸
///   1. `MOSSLIGHT_DISABLE_UPDATE` 环境变量 = "1" / "true" → 整个功能关掉；
///   2. 只认**独立安装目录**：跑在 `%TEMP%` 之类的路径下就拒绝更新（防"把 exe 拷到
///      临时目录试一下"结果把更新覆盖到别处）；
///   3. 下载完必须 SHA256 对得上 GitHub Release 里记的 digest，对不上直接删包。
///
/// ⚠️ 这里所有网络 / 文件操作都不往外抛异常：失败只返回带说明的结果，调用方记日志。
/// </summary>
public sealed class UpdateManager
{
    /// <summary>把这个环境变量设成 `1` / `true`（忽略大小写）就彻底关掉自动更新。</summary>
    public const string DisableEnvVar = "MOSSLIGHT_DISABLE_UPDATE";

    private const string Owner = "kongxia114";
    private const string Repo = "Mosslight-Launcher-2";
    private const string LatestApi = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    private static readonly string[] TempSegments = { @"\temp\", @"\tmp\", @"\cache\", @"\windows\temp\" };

    private readonly string _baseDir;
    private readonly Version _currentVersion;
    private readonly Action<string> _log;
    private readonly HttpClient _http;

    public UpdateManager(string baseDir, Version? currentVersion = null, Action<string>? log = null)
    {
        _baseDir = Path.GetFullPath(baseDir);
        _currentVersion = Normalize(currentVersion ?? Assembly.GetExecutingAssembly().GetName().Version);
        _log = log ?? (_ => { });
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mosslight-Launcher-Updater/1.0");
    }

    /// <summary>更新包下载到哪儿：`&lt;启动器目录&gt;\Temp\Update\`。</summary>
    public string DownloadDir => Path.Combine(_baseDir, "Temp", "Update");

    // ============================================================
    //  一、环境变量拦截
    // ============================================================

    /// <summary>
    /// 自动更新是不是被环境变量关掉了。`1` / `true`（忽略大小写）为关。
    /// </summary>
    public static bool IsDisabledByEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(DisableEnvVar);
        return value is not null
               && (value.Trim().Equals("1", StringComparison.Ordinal)
                   || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    // ============================================================
    //  二、环境校验（防串档）
    // ============================================================

    /// <summary>
    /// 这个目录是不是"装启动器的地方"。`%TEMP%` / `%TMP%` / `\Temp\` / 盘符根目录都不算：
    /// 那些地方被当成正式安装目录的话，更新会覆盖到临时副本上去。
    /// </summary>
    public static bool IsSafeInstallDir(string baseDir, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            reason = "启动器目录是空的";
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(baseDir).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "启动器目录不是合法路径";
            return false;
        }

        // 盘符根（`D:\`）—— 更新会把文件铺一整个分区
        if (full.Length <= 2)
        {
            reason = "启动器跑在盘符根目录，不适合自动更新";
            return false;
        }

        var probe = "\\" + full.ToLowerInvariant() + "\\";
        foreach (var segment in TempSegments)
        {
            if (probe.Contains(segment, StringComparison.Ordinal))
            {
                reason = "启动器跑在临时目录里，拒绝自动更新";
                return false;
            }
        }

        foreach (var temp in new[] { Path.GetTempPath(), Environment.GetEnvironmentVariable("TEMP") })
        {
            if (string.IsNullOrWhiteSpace(temp))
            {
                continue;
            }
            var normalized = Path.GetFullPath(temp).TrimEnd('\\');
            if (full.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            {
                reason = "启动器跑在系统临时目录里，拒绝自动更新";
                return false;
            }
        }

        return true;
    }

    // ============================================================
    //  三、检查
    // ============================================================

    /// <summary>请求 GitHub 的 `releases/latest`，跟当前版本比一比。</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken token = default)
    {
        if (IsDisabledByEnvironment())
        {
            return new UpdateCheckResult(false, null, "自动更新已被环境变量禁用");
        }
        if (!IsSafeInstallDir(_baseDir, out var reason))
        {
            return new UpdateCheckResult(false, null, reason);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(false, null,
                    $"查询更新失败：HTTP {(int)response.StatusCode}");
            }
            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return ParseRelease(json, _currentVersion);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or UriFormatException or InvalidOperationException)
        {
            return new UpdateCheckResult(false, null, $"查询更新失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 解析 Release JSON（纯函数，自检用）。挑资产时优先"小包"
    /// （`...-win64.zip`，不带 `selfcontained`），没有才退回第一个 zip。
    /// </summary>
    public static UpdateCheckResult ParseRelease(string json, Version currentVersion)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException ex)
        {
            return new UpdateCheckResult(false, null, $"更新清单不是合法 JSON：{ex.Message}");
        }
        if (root is null)
        {
            return new UpdateCheckResult(false, null, "更新清单是空的");
        }

        var tag = root["tag_name"]?.GetValue<string>() ?? string.Empty;
        if (!TryParseVersion(tag, out var version))
        {
            return new UpdateCheckResult(false, null, $"认不出发布版本号：{tag}");
        }

        JsonObject? picked = null;
        foreach (var node in root["assets"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject asset)
            {
                continue;
            }
            var name = asset["name"]?.GetValue<string>() ?? string.Empty;
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (name.Contains("selfcontained", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            picked = asset;
            break;
        }
        if (picked is null)
        {
            return new UpdateCheckResult(false, null, "这个发布里没有能用的 zip 更新包");
        }

        var zipName = picked["name"]?.GetValue<string>() ?? "update.zip";
        var zipUrl = picked["browser_download_url"]?.GetValue<string>() ?? string.Empty;
        if (zipUrl.Length == 0)
        {
            return new UpdateCheckResult(false, null, "更新包没有下载地址");
        }

        // GitHub 给的是 `sha256:xxxx`；老资产可能是 null —— 那种情况在下载后拒绝更新
        var digest = picked["digest"]?.GetValue<string>();
        var sha256 = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest["sha256:".Length..]
            : null;

        // `v2.10.2-force`：强制更新（弹窗不给「稍后」，关窗 = 退出）
        var info = new UpdateInfo(
            tag,
            version,
            zipName,
            zipUrl,
            picked["size"]?.GetValue<long>() ?? 0,
            sha256,
            root["html_url"]?.GetValue<string>() ?? string.Empty,
            IsForced: tag.Contains("-force", StringComparison.OrdinalIgnoreCase),
            Notes: root["body"]?.GetValue<string>() ?? string.Empty);

        return version > Normalize(currentVersion)
            ? new UpdateCheckResult(true, info, $"发现新版本 {tag}（当前 {currentVersion}）")
            : new UpdateCheckResult(false, info, $"已经是最新版本（{currentVersion}）");
    }

    /// <summary>`v2.10.2` / `2.10.2` / `v2.10.2-beta` → <see cref="Version"/>。</summary>
    public static bool TryParseVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }
        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }
        var dash = text.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0)
        {
            text = text[..dash];
        }
        return Version.TryParse(text, out var parsed) && (version = Normalize(parsed)) is not null;
    }

    private static Version Normalize(Version? version)
    {
        if (version is null)
        {
            return new Version(0, 0, 0, 0);
        }
        return new Version(
            Math.Max(0, version.Major),
            Math.Max(0, version.Minor),
            Math.Max(0, version.Build),
            Math.Max(0, version.Revision));
    }

    // ============================================================
    //  四、下载 + SHA256 校验
    // ============================================================

    /// <summary>
    /// 把更新包下到 `Temp\Update\`，然后算 SHA256 跟 Release 里记的比对。
    /// 对不上就把包删掉（宁可这次不更新，也不执行来路不明的 exe）。
    /// </summary>
    public async Task<UpdateDownloadResult> DownloadAndVerifyAsync(
        UpdateInfo info, IProgress<int>? progress = null, CancellationToken token = default)
    {
        if (!IsSafeInstallDir(_baseDir, out var reason))
        {
            return new UpdateDownloadResult(false, string.Empty, reason);
        }
        if (string.IsNullOrWhiteSpace(info.Sha256))
        {
            return new UpdateDownloadResult(false, string.Empty,
                "这个发布没有提供 SHA256，为安全起见不更新");
        }

        var target = Path.Combine(DownloadDir, info.ZipName);
        try
        {
            Directory.CreateDirectory(DownloadDir);
            using var response = await _http
                .GetAsync(info.ZipUrl, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateDownloadResult(false, string.Empty,
                    $"下载更新包失败：HTTP {(int)response.StatusCode}");
            }

            var total = response.Content.Headers.ContentLength ?? info.ZipSize;
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var file = File.Create(target))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    done += read;
                    if (total > 0)
                    {
                        progress?.Report((int)(done * 100 / total));
                    }
                }
            }

            var actual = Sha256OfFile(target);
            if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(target);
                return new UpdateDownloadResult(false, string.Empty,
                    $"SHA256 对不上（期望 {info.Sha256[..Math.Min(12, info.Sha256.Length)]}…，实际 {actual[..12]}…），已丢弃");
            }

            return new UpdateDownloadResult(true, target, $"已下载并通过校验：{info.ZipName}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                       or UnauthorizedAccessException or NotSupportedException)
        {
            TryDelete(target);
            return new UpdateDownloadResult(false, string.Empty, $"下载更新包失败：{ex.Message}");
        }
    }

    /// <summary>文件的 SHA256（小写十六进制）。</summary>
    public static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就留着（下次启动会覆盖）
        }
    }

    // ============================================================
    //  五、自我替换
    // ============================================================

    /// <summary>
    /// 把更新脚本放到 `Temp\Update\` 并启动它。脚本会等当前进程退出、解压覆盖、
    /// 再启动新的 exe，最后删掉自己和更新包。
    /// </summary>
    public bool TryStartApply(string zipPath, out string message)
    {
        message = string.Empty;
        try
        {
            var scriptPath = Path.Combine(DownloadDir, "apply-update.ps1");
            Directory.CreateDirectory(DownloadDir);
            File.WriteAllText(scriptPath, LoadScript());

            var exeName = Path.GetFileName(Environment.ProcessPath) is { Length: > 0 } name
                ? name
                : "Mosslight.exe";
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = _baseDir,
            };
            foreach (var arg in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                         "-File", $"\"{scriptPath}\"",
                         "-ProcessId", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                         "-BaseDir", $"\"{_baseDir}\"",
                         "-ZipPath", $"\"{zipPath}\"",
                         "-ExeName", $"\"{exeName}\"",
                     })
            {
                psi.ArgumentList.Add(arg);
            }

            Process.Start(psi);
            _log($"更新脚本已启动：{scriptPath}");
            message = "更新脚本已启动，启动器会关闭并自动替换到新版本";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            message = $"启动更新脚本失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>取出内嵌的替换脚本（`Updates\Apply-Update.ps1`）。</summary>
    public static string LoadScript()
    {
        var assembly = typeof(UpdateManager).Assembly;
        var name = Array.Find(assembly.GetManifestResourceNames(),
            n => n.EndsWith("Apply-Update.ps1", StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            throw new InvalidOperationException("内嵌的更新脚本没找到（Apply-Update.ps1）");
        }
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
