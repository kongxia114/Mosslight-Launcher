using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
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

/// <summary>
/// 「刚更新完、还没给用户看」的那一份记录（`Temp\pending_update.json`）。
/// 只在"下载校验通过 → 拉起替换脚本"这中间存在，用户点掉弹窗后立刻删除。
/// </summary>
public sealed record PendingUpdate(string Version, string Tag, string Notes, bool IsForced)
{
    /// <summary>落盘用的形状（JSON 字段名写死，别跟着 C# 属性名漂）。</summary>
    internal sealed class Payload
    {
        [System.Text.Json.Serialization.JsonPropertyName("version")] public string? Version { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("tag")] public string? Tag { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("notes")] public string? Notes { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("forced")] public bool Forced { get; set; }
    }
}

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

    /// <summary>更新说明最多显示多少行（再多就用 `…` 收尾）。</summary>
    public const int MaxNoteLines = 24;

    private const string Owner = "kongxia114";
    private const string Repo = "Mosslight-Launcher-2";
    private const string ReleasesApi = $"https://api.github.com/repos/{Owner}/{Repo}/releases";
    private const string LatestApi = $"{ReleasesApi}/latest";

    private static readonly string[] TempSegments = { @"\temp\", @"\tmp\", @"\cache\", @"\windows\temp\" };

    private readonly string _baseDir;
    private readonly Version _currentVersion;
    private readonly Action<string> _log;

    /// <summary>只用来问 GitHub API：10 秒超时，超时就放弃（不挡任何东西，本来就是后台跑的）。</summary>
    private readonly HttpClient _api;

    /// <summary>下载更新包：给足时间（小包 1.4 MB，但别让慢网卡死）。</summary>
    private readonly HttpClient _download;

    public UpdateManager(string baseDir, Version? currentVersion = null, Action<string>? log = null)
    {
        _baseDir = Path.GetFullPath(baseDir);
        _currentVersion = Normalize(currentVersion ?? Assembly.GetExecutingAssembly().GetName().Version);
        _log = log ?? (_ => { });
        _api = CreateClient(TimeSpan.FromSeconds(10));
        _download = CreateClient(TimeSpan.FromMinutes(5));
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        // GitHub 要求带 User-Agent（不带会被 403），Accept 指明要 v3 的 JSON
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mosslight-Launcher-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
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
            using var response = await _api.SendAsync(request, token).ConfigureAwait(false);
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
    /// 解析 Release JSON（纯函数，自检用）。只认 `...-win64.zip` —— 发布就只有这一个包。
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

        // 发布只有**一个**包：`Mosslight-2.0-<tag>-win64.zip`，所以直接找它。
        JsonObject? picked = null;
        foreach (var node in root["assets"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject asset)
            {
                continue;
            }
            var name = asset["name"]?.GetValue<string>() ?? string.Empty;
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                || !name.Contains("win64", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            picked = asset;
            break;
        }
        if (picked is null)
        {
            return new UpdateCheckResult(false, null, "这个发布里没有 win64 的 zip 更新包");
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
            Notes: CleanReleaseNotes(root["body"]?.GetValue<string>()));

        return version > Normalize(currentVersion)
            ? new UpdateCheckResult(true, info, $"发现新版本 {tag}（当前 {currentVersion}）")
            : new UpdateCheckResult(false, info, $"已经是最新版本（{currentVersion}）");
    }

    /// <summary>
    /// 把 Release 正文削成"纯更新日志"：
    ///   · 遇到 `---` 分割线、或者 `## 下载` / `## Download` 标题 → 这一行及后面全丢；
    ///   · 去掉首尾空行；
    ///   · 超过 <see cref="MaxNoteLines"/> 行就截断，末尾补一个 `…`。
    ///
    /// 为什么要削：发布说明后半段是"下哪个包、装不装运行时、多大体积"那套话。
    /// 用户在更新弹窗里只想知道"这版改了什么"，那些是下载页的事。
    /// </summary>
    public static string CleanReleaseNotes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var kept = new List<string>();
        foreach (var line in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed is "---" or "***" or "___")
            {
                break;
            }
            if (trimmed.StartsWith("## 下载", StringComparison.Ordinal)
                || trimmed.StartsWith("## Download", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            kept.Add(line.TrimEnd());
        }

        while (kept.Count > 0 && kept[^1].Trim().Length == 0)
        {
            kept.RemoveAt(kept.Count - 1);
        }

        if (kept.Count > MaxNoteLines)
        {
            kept = kept.Take(MaxNoteLines).ToList();
            kept.Add("…");
        }

        return string.Join("\n", kept).Trim();
    }

    /// <summary>
    /// 记录的上次运行版本比当前低 → 说明刚更新完（记录为空 = 全新安装，不算）。
    /// </summary>
    public static bool IsJustUpdated(Version current, string? lastRunVersion)
        => Version.TryParse(lastRunVersion, out var last) && Normalize(current) > Normalize(last);

    /// <summary>
    /// 按 tag 取某个 Release 的正文（"更新成功"弹窗要展示这一版的更新内容）。
    /// 拿不到就返回空串：那只是一句说明，不该因为网络问题让提示消失。
    /// </summary>
    public async Task<string> FetchNotesAsync(string tag, CancellationToken token = default)
    {
        foreach (var candidate in new[] { tag, tag.TrimStart('v', 'V') })
        {
            try
            {
                using var response = await _api
                    .GetAsync($"{ReleasesApi}/tags/{Uri.EscapeDataString(candidate)}", token)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }
                var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                return JsonNode.Parse(json) is JsonObject obj
                    ? CleanReleaseNotes(obj["body"]?.GetValue<string>())
                    : string.Empty;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                return string.Empty;
            }
        }
        return string.Empty;
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
            using var response = await _download
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

            // 大文件哈希算在后台线程：别让 UI 线程在 1.4 MB 上停一下
            var actual = await Task.Run(() => Sha256OfFile(target), token).ConfigureAwait(false);
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
    //  四·五、待展示缓存（更新完第一次启动要弹的那份）
    // ============================================================

    /// <summary>待展示文件放在 `&lt;启动器目录&gt;\Temp\pending_update.json`。</summary>
    public string PendingFilePath => Path.Combine(_baseDir, "Temp", "pending_update.json");

    /// <summary>
    /// 把"更新完要给用户看的这一份"写进缓存。**必须在拉起替换脚本之前调** ——
    /// 脚本一跑程序就退了，后面没机会写。
    /// </summary>
    public bool TryWritePendingUpdate(UpdateInfo info, out string error)
    {
        error = string.Empty;
        try
        {
            var path = PendingFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var payload = new PendingUpdate.Payload
            {
                Version = info.Version.ToString(),
                Tag = info.Tag,
                Notes = info.Notes,
                Forced = info.IsForced,
            };
            var json = JsonSerializer.Serialize(payload, PendingJsonOptions);
            // 先写临时文件再改名：写到一半断电也不会留下半截 JSON
            var temp = path + ".tmp";
            File.WriteAllText(temp, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 读待展示缓存。**任何形式的坏数据都返回 null**（文件不在、JSON 碎了、
    /// 字段类型被手改坏、版本号认不出来）—— 这里绝不能让启动流程抛异常。
    /// </summary>
    public static PendingUpdate? ReadPendingUpdate(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            var payload = JsonSerializer.Deserialize<PendingUpdate.Payload>(text, PendingJsonOptions);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Version))
            {
                return null;
            }
            return new PendingUpdate(
                payload.Version.Trim(),
                payload.Tag?.Trim() ?? string.Empty,
                payload.Notes ?? string.Empty,
                payload.Forced);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or JsonException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>删掉待展示缓存（用户已经看过弹窗了）。删不掉就留着 —— 下次启动还会弹一次。</summary>
    public static void DeletePendingUpdate(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            // 删不掉不影响使用：最多下次再弹一次
        }
    }

    private static readonly JsonSerializerOptions PendingJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,     // 手改过的文件大小写不一致也能读
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        // 正文里全是中文和 markdown：不转义，人打开文件也能看懂
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ============================================================
    //  四·六、真实 exe 名 / 解压到 staging
    // ============================================================



    /// <summary>脚本替换失败时写的标记文件：下次启动读它弹「自动更新失败」。</summary>
    public string ErrorMarkerPath => Path.Combine(_baseDir, "Temp", "update_error.json");

    /// <summary>
    /// 当前**正在运行**的那个 exe 叫什么 —— 用户可能把它改名成 `Moss.exe`，
    /// 所以任何地方都不能写死 `Mosslight.exe`。
    /// </summary>
    public static string GetRealExeName()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = System.Reflection.Assembly.GetExecutingAssembly().Location;
            }
            var name = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ? "Mosslight.exe" : name;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                       or ArgumentException or IOException)
        {
            return "Mosslight.exe";
        }
    }

    /// <summary>
    /// 用 **C# 的 <see cref="ZipFile"/>** 把更新包解到 `Temp\Update\staged`。
    ///
    /// 为什么不在脚本里 `Expand-Archive`：那个 cmdlet 对"文件被占用 / 目录只读"给的
    /// 报错又慢又不具体，实测经常是**静默失败**（脚本照样往下走、程序照样重启，
    /// 版本还是旧的）。在这里解压还有个好处：解压失败时主进程还活着，能把原因写进日志。
    /// </summary>
    public UpdateDownloadResult ExtractToStaging(string zipPath)
    {
        try
        {
            if (!File.Exists(zipPath))
            {
                return new UpdateDownloadResult(false, string.Empty, "更新包不见了（可能被杀软删了）");
            }

            if (Directory.Exists(NewVersionDir))
            {
                Directory.Delete(NewVersionDir, recursive: true);
            }
            Directory.CreateDirectory(NewVersionDir);
            // 绝对路径 + overwrite：解压到独立的 Temp\NewVersionFiles\，不碰安装目录
            ZipFile.ExtractToDirectory(Path.GetFullPath(zipPath), Path.GetFullPath(NewVersionDir),
                overwriteFiles: true);

            var exe = Directory.EnumerateFiles(NewVersionDir, "*.exe", SearchOption.AllDirectories)
                .FirstOrDefault(p => Path.GetFileName(p).Equals("Mosslight.exe", StringComparison.OrdinalIgnoreCase))
                ?? Directory.EnumerateFiles(NewVersionDir, "*.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exe is null)
            {
                return new UpdateDownloadResult(false, string.Empty, "更新包里没有 exe");
            }

            return new UpdateDownloadResult(true, exe, $"已解压到 {NewVersionDir}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidDataException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            return new UpdateDownloadResult(false, string.Empty, $"解压失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 读"上次替换失败"的标记。没有 / 坏了都返回 null —— 这只是个提示，不该影响启动。
    /// </summary>
    public static string? ReadErrorMarker(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            var node = JsonNode.Parse(text) as JsonObject;
            var message = node?["error"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(message) ? "（脚本没写清原因，详见 Logs\\update_error.log）" : message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public static void DeleteErrorMarker(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            // 删不掉就下次再提示一遍
        }
    }

    // ============================================================
    //  五、自我替换（.bat + xcopy）
    // ============================================================

    /// <summary>解压出来的新版本文件放这儿（bat 从这里 xcopy 到安装目录）。</summary>
    public string NewVersionDir => Path.Combine(_baseDir, "Temp", "NewVersionFiles");

    /// <summary>替换脚本放这儿。</summary>
    private string ApplyBatPath => Path.Combine(_baseDir, "Temp", "Update", "apply.bat");

    /// <summary>覆盖日志（bat 写的，给人看的）。</summary>
    public string ApplyLogPath => Path.Combine(_baseDir, "Logs", "apply_update.log");

    /// <summary>
    /// 清掉上一次更新留下的东西：`Temp\Update\` 整个目录 + `Temp\NewVersionFiles\`。
    ///
    /// 启动时和"点更新之前"都调一次：残留的 zip / 解压了一半的目录如果被下一次更新用到，
    /// 就会出现"文件覆盖了、版本却没变"这种最难受的串档。
    /// </summary>
    public bool TryClearUpdateCache(out string message)
    {
        message = string.Empty;
        try
        {
            foreach (var dir in new[] { Path.Combine(_baseDir, "Temp", "Update"), NewVersionDir })
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            message = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 生成 `apply.bat` 的正文（纯函数，自检用）。
    ///
    /// ⚠️ 路径全部用**绝对路径**注入：不能用 `%~dp0` 去拼 —— bat 自己躺在
    ///    `Temp\Update\` 里，`%~dp0Temp\NewVersionFiles` 会变成
    ///    `Temp\Update\Temp\NewVersionFiles`（这正是一类"看起来跑完了、
    ///    其实什么都没覆盖"的来源）。
    /// ⚠️ exe 名字用**当前真实进程名**注入：用户可能把 Mosslight.exe 改成了 Moss.exe。
    /// </summary>
    /// <summary>
    /// 生成 `apply.bat` 的正文（纯函数，自检用）。
    ///
    /// ⚠️ 路径全部用**绝对路径**注入：不能用 `%~dp0` 去拼 —— bat 自己躺在
    ///    `Temp\Update\` 里，`%~dp0Temp\NewVersionFiles` 会变成
    ///    `Temp\Update\Temp\NewVersionFiles`（这正是一类"看起来跑完了、
    ///    其实什么都没覆盖"的来源）。
    /// ⚠️ exe 名字用**当前真实进程名**注入：用户可能把 Mosslight.exe 改成了 Moss.exe。
    /// ⚠️ 正文**纯 ASCII + CRLF**：cmd 在默认代码页（936）下读带中文的 .bat 会把
    ///    引号吃掉，而 LF-only 的 `if (...)` 块也会被解析错（两条都踩过）。
    ///    日志里要写路径，英文照样看得懂。
    /// </summary>
    public static string BuildApplyScript(string exeName)
    {
        var target = exeName.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            target = "Mosslight.exe";
        }

        var script = new List<string>
        {
            "@echo off",
            "setlocal",
            // 日志用 UTF-8 写：bat 正文是纯 ASCII，这行本身不会带来解析风险，
            // 但能让含中文的路径在 apply_update.log 里正常显示
            "chcp 65001 >nul",
            // APP_DIR = bat 所在目录（Temp\Update）往上两级 = 安装目录。
            // 用 %%~fI 规范化成完整路径，绕开"路径里有中文"这个编码坑。
            "set \"APP_DIR=%~dp0..\\..\"",
            "for %%I in (\"%APP_DIR%\") do set \"APP_DIR=%%~fI\"",
            $"set \"TARGET_NAME={target}\"",
            "set \"SRC=%APP_DIR%\\Temp\\NewVersionFiles\"",
            "set \"LOG=%APP_DIR%\\Logs\\apply_update.log\"",
            "set \"ERRMARK=%APP_DIR%\\Temp\\update_error.json\"",
            "set \"TARGET=%APP_DIR%\\%TARGET_NAME%\"",
            "",
            "if not exist \"%APP_DIR%\\Logs\" mkdir \"%APP_DIR%\\Logs\"",
            "echo [%DATE% %TIME%] update start: %SRC% -^> %APP_DIR% >> \"%LOG%\"",
            "",
            "echo Waiting for the launcher to exit...",
            "timeout /t 3 /nobreak >nul",
            "",
            "if not exist \"%SRC%\\*\" (",
            "    echo [%DATE% %TIME%] ERROR: source folder missing: %SRC% >> \"%LOG%\"",
            "    echo {\"error\":\"source folder missing, see Logs/apply_update.log\"} > \"%ERRMARK%\"",
            "    goto :done",
            ")",
            "",
            "echo [%DATE% %TIME%] xcopy start >> \"%LOG%\"",
            "xcopy /Y /E /Q \"%SRC%\\*.*\" \"%APP_DIR%\\\" >> \"%LOG%\" 2>&1",
            "if errorlevel 1 (",
            "    echo [%DATE% %TIME%] ERROR: xcopy failed with %ERRORLEVEL% >> \"%LOG%\"",
            "    echo {\"error\":\"xcopy failed, see Logs/apply_update.log\"} > \"%ERRMARK%\"",
            "    goto :done",
            ")",
            "echo [%DATE% %TIME%] xcopy done >> \"%LOG%\"",
            "",
            "if /I not \"%TARGET_NAME%\"==\"Mosslight.exe\" (",
            "    if exist \"%APP_DIR%\\Mosslight.exe\" (",
            "        move /Y \"%APP_DIR%\\Mosslight.exe\" \"%TARGET%\" >> \"%LOG%\" 2>&1",
            "        echo [%DATE% %TIME%] renamed Mosslight.exe to %TARGET_NAME% >> \"%LOG%\"",
            "    )",
            ")",
            "",
            ":done",
            "echo [%DATE% %TIME%] starting %TARGET% >> \"%LOG%\"",
            "start \"\" \"%TARGET%\"",
            "rd /S /Q \"%SRC%\" >nul 2>&1",
            "del \"%ERRMARK%\" >nul 2>&1",
            "echo [%DATE% %TIME%] cleanup done >> \"%LOG%\"",
            "(goto) 2>nul & del \"%~f0\"",
        };
        return string.Join("\r\n", script) + "\r\n";
    }

    /// <summary>
    /// 写替换脚本：用**系统 ANSI 代码页**（简中机器上是 936），不能用 UTF-8 ——
    /// cmd 按 ANSI 读 .bat，UTF-8 的中文会被解成乱码。
    /// </summary>
    private static void WriteApplyScript(string path, string text)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var codePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        var encoding = codePage > 0
            ? Encoding.GetEncoding(codePage)
            : new UTF8Encoding(false);
        File.WriteAllText(path, text, encoding);
    }

    /// <summary>
    /// 生成并启动 `apply.bat`：等进程退出 → xcopy 覆盖 → 用真实 exe 名重启 → 清理。
    /// 日志写在 `Logs\apply_update.log`（bat 里 `>>` 追加）。
    /// </summary>
    public bool TryStartApply(out string message)
    {
        message = string.Empty;
        try
        {
            var exeName = GetRealExeName();
            Directory.CreateDirectory(Path.GetDirectoryName(ApplyBatPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(ApplyLogPath)!);
            WriteApplyScript(ApplyBatPath, BuildApplyScript(exeName));

            var psi = new ProcessStartInfo("cmd.exe")
            {
                // 让人能看见这个窗口：更新只有几秒，"静默失败"才是最糟的体验
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = _baseDir,
                Arguments = $"/c \"{ApplyBatPath}\"",
            };
            Process.Start(psi);
            _log($"替换脚本已启动：{ApplyBatPath}（exe={exeName}）");
            message = "替换脚本已启动，启动器会关闭并在几秒后以新版本回来";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            message = $"启动替换脚本失败：{ex.Message}";
            return false;
        }
    }
}

