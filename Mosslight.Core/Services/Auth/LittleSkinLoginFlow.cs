using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Mosslight.Core.Services.Auth;

/// <summary>一次 LittleSkin 登录的结果（不管成没成都不会抛异常）。</summary>
public sealed record LittleSkinLoginResult(
    bool Ok,
    string Error,
    OAuthTokenResult? Token,
    IReadOnlyList<MinecraftProfile> Profiles)
{
    public static LittleSkinLoginResult Fail(string error)
        => new(false, error, null, Array.Empty<MinecraftProfile>());
}

/// <summary>
/// 授权码模式的**桌面端闭环**：开浏览器 → 本机回调服务器接住 `?code=` → 换 token → 拉角色；⚠️ 用
/// TcpListener 而不是 `HttpListener`：后者在 Windows 上注册 `http://127.0.0.1:端口/` 需要 URL ACL
/// （普通用户会 Access denied，还要求提权）。
/// </summary>
public sealed class LittleSkinLoginFlow
{
    // 回调服务器最多等这么久，超时就当用户取消了。
    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromMinutes(5);

    private readonly LittleSkinOptions _options;
    private readonly LittleSkinAuthService _service;
    private readonly Action<string> _log;

    public LittleSkinLoginFlow(LittleSkinOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _service = new LittleSkinAuthService(options);
        _log = message =>
        {
            try
            {
                Directory.CreateDirectory(options.LogDir);
                File.AppendAllText(Path.Combine(options.LogDir, "auth.log"),
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or NotSupportedException or System.Security.SecurityException)
            {
                // 日志写不了不影响登录
            }
        };
    }

    public async Task<LittleSkinLoginResult> RunAsync(
        Action<string>? status = null,
        CancellationToken cancel = default)
    {
        var callback = ParseCallback(_options.RedirectUri, out var port, out var path);
        if (callback is null)
        {
            return LittleSkinLoginResult.Fail($"回调地址不合法：{_options.RedirectUri}");
        }

        var state = Guid.NewGuid().ToString("N");
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
        }
        catch (SocketException ex)
        {
            _log($"回调端口 {port} 占用：{ex.Message}");
            return LittleSkinLoginResult.Fail($"本机端口 {port} 被占用了，关掉占用的程序再试。");
        }

        try
        {
            var url = _service.GetAuthorizeUrl(state);
            status?.Invoke("正在打开浏览器…");
            if (!TryOpenBrowser(url))
            {
                return LittleSkinLoginResult.Fail("打不开浏览器，请手动访问授权地址：" + url);
            }

            status?.Invoke("等待浏览器授权…");
            var code = await WaitForCodeAsync(listener, path, state, cancel).ConfigureAwait(false);
            if (code is null)
            {
                return LittleSkinLoginResult.Fail("授权超时或被取消。");
            }

            status?.Invoke("正在换取登录凭据…");
            var token = await _service.ExchangeTokenAsync(code, cancel).ConfigureAwait(false);
            if (!token.Ok)
            {
                return LittleSkinLoginResult.Fail(token.Error ?? "换取 token 失败。");
            }

            status?.Invoke("正在读取角色…");
            // 授权已经成功，但角色接口偶尔会抖：给它 5 秒 + 重试一次。
            // 超时/失败要给"网络问题"的明确提示，不能只丢一句空列表。
            var profiles = await FetchProfilesAsync(token.AccessToken, status, cancel).ConfigureAwait(false);
            if (profiles is null)
            {
                return LittleSkinLoginResult.Fail("授权成功，但读取角色超时（5 秒）。请检查网络后重试。");
            }
            _log($"登录成功：{profiles.Count} 个角色");
            return new LittleSkinLoginResult(true, string.Empty, token, profiles);
        }
        finally
        {
            // 浏览器那边还在渲染"授权成功"页面：立刻关端口会变成 ERR_CONNECTION_REFUSED，
            // 玩家会以为授权失败了。多留 1.2 秒再收摊。
            try
            {
                await Task.Delay(1200).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 取消/关闭无所谓
            }
            try
            {
                listener.Stop();
            }
            catch (SocketException)
            {
                // 停不掉也无所谓
            }
        }
    }

    /// <summary>拿角色：5 秒超时 + 失败重试一次。</summary>
    private async Task<IReadOnlyList<MinecraftProfile>?> FetchProfilesAsync(
        string accessToken, Action<string>? status, CancellationToken cancel)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var profiles = await _service.GetProfilesAsync(accessToken, timeout.Token).ConfigureAwait(false);
                return profiles;
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                _log($"读取角色第 {attempt} 次超时（5 秒）");
                status?.Invoke(attempt == 1 ? "读取角色超时，正在重试…" : "读取角色超时…");
            }
        }
        return null;
    }

    /// <summary>等回调；拿到 code 返回，超时/取消返回 null。</summary>
    private async Task<string?> WaitForCodeAsync(
        TcpListener listener, string path, string state, CancellationToken cancel)
    {
        var deadline = DateTimeOffset.Now + CallbackTimeout;
        while (DateTimeOffset.Now < deadline && !cancel.IsCancellationRequested)
        {
            if (!listener.Pending())
            {
                await Task.Delay(120, cancel).ConfigureAwait(false);
                continue;
            }

            using var client = await listener.AcceptTcpClientAsync(cancel).ConfigureAwait(false);
            using var stream = client.GetStream();
            var requestLine = await ReadRequestLineAsync(stream, cancel).ConfigureAwait(false);
            var query = ParseQuery(requestLine, path, out var matched);
            if (!matched)
            {
                await RespondAsync(stream, 404, "Not found", cancel).ConfigureAwait(false);
                continue;
            }

            var code = query.GetValueOrDefault("code");
            var gotState = query.GetValueOrDefault("state");
            var error = query.GetValueOrDefault("error");

            if (!string.IsNullOrEmpty(error))
            {
                await RespondAsync(stream, 200, "授权被拒绝，可以关闭这个页面了。", cancel).ConfigureAwait(false);
                _log($"授权被拒绝：{error}");
                return null;
            }
            if (!string.Equals(gotState, state, StringComparison.Ordinal))
            {
                // state 对不上：可能是别人构造的请求，直接忽略
                await RespondAsync(stream, 400, "state 不匹配", cancel).ConfigureAwait(false);
                _log("回调 state 不匹配，已忽略");
                continue;
            }
            if (string.IsNullOrEmpty(code))
            {
                await RespondAsync(stream, 400, "缺少 code", cancel).ConfigureAwait(false);
                continue;
            }

            await RespondAsync(stream, 200,
                "授权成功！可以关闭这个页面，回到启动器继续。", cancel).ConfigureAwait(false);
            return code;
        }
        return null;
    }

    private static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancel)
    {
        var buffer = new byte[1];
        var line = new StringBuilder();
        for (var i = 0; i < 4096; i++)
        {
            var read = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            var ch = (char)buffer[0];
            if (ch == '\n')
            {
                break;
            }
            if (ch != '\r')
            {
                line.Append(ch);
            }
        }
        return line.ToString();
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string text, CancellationToken cancel)
    {
        var html = $"<!doctype html><meta charset=\"utf-8\"><title>Mosslight</title>" +
                   $"<body style=\"font-family:sans-serif;background:#1b1d23;color:#eee;padding:40px\">{text}</body>";
        var bytes = Encoding.UTF8.GetBytes(html);
        var head = $"HTTP/1.1 {status} OK\r\nContent-Type: text/html; charset=utf-8\r\n" +
                   $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancel).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancel).ConfigureAwait(false);
        await stream.FlushAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>从 `GET /callback?code=..&amp;state=.. HTTP/1.1` 里取出参数。</summary>
    private static Dictionary<string, string> ParseQuery(string requestLine, string path, out bool matched)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        matched = false;
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return result;
        }
        var target = parts[1];
        var q = target.IndexOf('?');
        var targetPath = q >= 0 ? target[..q] : target;
        if (!string.Equals(targetPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }
        matched = true;
        if (q < 0)
        {
            return result;
        }
        foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            result[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return result;
    }

    /// <summary>`http://127.0.0.1:25566/callback` → 端口 + 路径。</summary>
    private static string? ParseCallback(string redirectUri, out int port, out string path)
    {
        port = 0;
        path = "/callback";
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri))
        {
            return null;
        }
        if (uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)
        {
            return null;
        }
        port = uri.Port;
        path = uri.AbsolutePath;
        return redirectUri;
    }

    private static bool TryOpenBrowser(string url)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or System.IO.FileNotFoundException)
        {
            return false;
        }
    }
}
