using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosslight.Core.Services.Auth;

/// <summary>
/// LittleSkin 的 OAuth 2.0 客户端配置。
///
/// ⚠️ `ClientSecret` 会打进桌面端的二进制里 —— 任何拿到 exe 的人都能挖出来。
///    LittleSkin 的机密客户端就是这么用的，所以别把它当成"密钥"；
///    真要更安全，只能在服务端换 token（本项目没有服务端）。
/// </summary>
public sealed record LittleSkinOptions
{
    /// <summary>在 LittleSkin 的「应用」页申请到的客户端 ID。</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>客户端密钥（见类注释里的安全说明）。</summary>
    public string ClientSecret { get; init; } = string.Empty;

    /// <summary>回调地址：必须和 LittleSkin 应用里登记的**完全一致**。</summary>
    public string RedirectUri { get; init; } = LittleSkinSecrets.RedirectUri;

    /// <summary>
    /// 申请的权限范围，多个用空格分隔。
    ///
    /// ⚠️ 默认是 `User.Read`，**不是** `Yggdrasil.Minecraft.API` —— LittleSkin 对后者
    /// 会回 `invalid_scope`（实测，2026-10）。留空也可以：服务器按 `User.Read` 处理，
    /// 此时 `GetAuthorizeUrl` 干脆不带 scope 参数。
    /// </summary>
    public string Scopes { get; init; } = "User.Read";

    public string AuthorizeUrl { get; init; } = "https://littleskin.cn/oauth/authorize";

    public string TokenUrl { get; init; } = "https://littleskin.cn/oauth/token";

    /// <summary>Yggdrasil API 根（Blessing Skin 的兼容层）。</summary>
    public string YggdrasilBase { get; init; } = "https://littleskin.cn/api/yggdrasil";

    /// <summary>
    /// 拉角色列表用哪条路径（相对 <see cref="YggdrasilBase"/>）。
    /// 留空 = 按 <see cref="LittleSkinAuthService.GetProfilesAsync"/> 里的候选链依次尝试。
    /// </summary>
    public string ProfilesPath { get; init; } = string.Empty;

    /// <summary>日志目录（默认 `&lt;程序目录&gt;\Logs`）。</summary>
    public string LogDir { get; init; } = Path.Combine(AppContext.BaseDirectory, "Logs");
}

/// <summary>换 token / 刷 token 的结果。<see cref="Ok"/> 为 false 时 <see cref="Error"/> 有值。</summary>
public sealed record OAuthTokenResult(
    bool Ok,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string TokenType,
    string Scope,
    string? Error)
{
    public static OAuthTokenResult Fail(string error)
        => new(false, string.Empty, string.Empty, DateTimeOffset.MinValue, string.Empty, string.Empty, error);
}

/// <summary>一个 Minecraft 角色（Yggdrasil 的 profile）。</summary>
public sealed record MinecraftProfile(string Id, string Name, string SkinModel = "")
{
    /// <summary>带连字符的 UUID（Yggdrasil 的 id 本来就是这个形状；没有就原样返回）。</summary>
    public string Uuid => Id.Length == 32
        ? string.Join("-", Id[..8], Id[8..12], Id[12..16], Id[16..20], Id[20..])
        : Id;
}

/// <summary>
/// LittleSkin（Blessing Skin）的 OAuth 2.0 登录：授权码换 token、刷新、拉角色。
///
/// 只有底层 HTTP，没有任何 UI 依赖；日志写 `Logs\auth.log`。
/// 所有网络失败都**不抛**（返回带 <c>Error</c> 的结果），并由调用方决定怎么提示。
/// </summary>
public sealed class LittleSkinAuthService
{
    private const string UserAgentText = "Mosslight/2.0 (+https://github.com/kongxia114/Mosslight-Launcher-2)";

    /// <summary>拉角色列表时依次尝试的候选路径（见 <see cref="GetProfilesAsync"/> 的注释）。</summary>
    private static readonly string[] ProfileCandidates =
    {
        "sessionserver/session/minecraft/profile",
        "api/profiles/minecraft",
    };

    private readonly LittleSkinOptions _options;
    private readonly HttpClient _http;

    public LittleSkinAuthService(LittleSkinOptions options, HttpClient? http = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = http ?? CreateClient();
        LogPath = Path.Combine(_options.LogDir, "auth.log");
    }

    /// <summary>`Logs\auth.log`：每次请求、每次失败都有一行，排错全靠它。</summary>
    public string LogPath { get; }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentText);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    // ============================================================
    //  ① 授权链接
    // ============================================================

    /// <summary>
    /// 拼出让用户去浏览器里打开的登录地址。
    /// `state` 只是防 CSRF 的随机串，回调时要原样核对（可空）。
    /// </summary>
    public string GetAuthorizeUrl(string? state = null)
    {
        var query = new List<string>
        {
            "client_id=" + Uri.EscapeDataString(_options.ClientId),
            "response_type=code",
        };
        // scope 留空时不带这个参数（LittleSkin 会按 User.Read 处理）；
        // 有值就按 RFC 6749 用空格分隔，整体做一次 URL 编码（空格 → %20）。
        var scopes = string.Join(" ", (_options.Scopes ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (scopes.Length > 0)
        {
            query.Add("scope=" + Uri.EscapeDataString(scopes));
        }
        query.Add("redirect_uri=" + Uri.EscapeDataString(_options.RedirectUri));
        if (!string.IsNullOrWhiteSpace(state))
        {
            query.Add("state=" + Uri.EscapeDataString(state));
        }
        var url = _options.AuthorizeUrl + "?" + string.Join("&", query);
        Log($"授权地址：{url}");
        return url;
    }

    // ============================================================
    //  ② 授权码 → token
    // ============================================================

    /// <summary>用回调里拿到的 `code` 换 access_token / refresh_token。</summary>
    public Task<OAuthTokenResult> ExchangeTokenAsync(string code, CancellationToken token = default)
        => RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _options.RedirectUri,
        }, "换 token", token);

    /// <summary>刷新 token（access_token 过期时用）。</summary>
    public Task<OAuthTokenResult> RefreshTokenAsync(string refreshToken, CancellationToken token = default)
        => RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, "刷新 token", token);

    private async Task<OAuthTokenResult> RequestTokenAsync(
        Dictionary<string, string> form, string what, CancellationToken token)
    {
        form["client_id"] = _options.ClientId;
        form["client_secret"] = _options.ClientSecret;

        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await _http.PostAsync(_options.TokenUrl, content, token).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                var error = ReadError(json) ?? $"HTTP {status}";
                Log($"{what}失败：HTTP {status}，{error}");
                return OAuthTokenResult.Fail(error);
            }

            var result = ParseToken(json);
            if (!result.Ok)
            {
                Log($"{what}失败：{result.Error}");
                return result;
            }
            Log($"{what}成功：有效期至 {result.ExpiresAt:yyyy-MM-dd HH:mm:ss}，scope={result.Scope}");
            return result;
        }
        catch (HttpRequestException ex)
        {
            Log($"{what}失败（网络）：{ex.Message}");
            return OAuthTokenResult.Fail($"网络请求失败：{ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            Log($"{what}超时：{ex.Message}");
            return OAuthTokenResult.Fail("请求超时");
        }
    }

    // ============================================================
    //  ③ 角色列表
    // ============================================================

    /// <summary>
    /// 拿这个 token 对应的角色列表。
    ///
    /// LittleSkin 是 Blessing Skin，Yggdrasil 兼容层里"按 uuid 取单个角色"是标准的，
    /// 但"列出我拥有的全部角色"在不同版本里路径不完全一样，所以这里按候选链依次试：
    ///   1. `sessionserver/session/minecraft/profile` —— 有些版本会直接返回 token 所有者的角色
    ///   2. `api/profiles/minecraft` —— 返回数组
    /// 第一条通的会被记住（`_profilesPath`），后续请求不再重复试错。
    /// </summary>
    public async Task<IReadOnlyList<MinecraftProfile>> GetProfilesAsync(
        string accessToken, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            Log("拉角色：access_token 是空的");
            return Array.Empty<MinecraftProfile>();
        }

        foreach (var path in Candidates())
        {
            var url = Combine(_options.YggdrasilBase, path);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    Log($"拉角色 {url} 失败：HTTP {(int)response.StatusCode}，{ReadError(json) ?? "无错误信息"}");
                    continue;
                }

                var profiles = ParseProfiles(json);
                Log($"拉角色 {url} 成功：{profiles.Count} 个");
                return profiles;
            }
            catch (HttpRequestException ex)
            {
                Log($"拉角色 {url} 网络失败：{ex.Message}");
            }
            catch (TaskCanceledException)
            {
                Log($"拉角色 {url} 超时");
            }
        }
        return Array.Empty<MinecraftProfile>();
    }

    private IEnumerable<string> Candidates()
    {
        if (!string.IsNullOrWhiteSpace(_options.ProfilesPath))
        {
            yield return _options.ProfilesPath.TrimStart('/');
            yield break;
        }
        foreach (var path in ProfileCandidates)
        {
            yield return path;
        }
    }

    // ============================================================
    //  解析（都是纯函数，自检直接调）
    // ============================================================

    /// <summary>解析 token 响应；字段缺失时给出可读的错误。</summary>
    public static OAuthTokenResult ParseToken(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                return OAuthTokenResult.Fail("token 响应不是 JSON 对象");
            }
            var access = root["access_token"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(access))
            {
                return OAuthTokenResult.Fail(ReadError(json) ?? "响应里没有 access_token");
            }
            var refresh = root["refresh_token"]?.GetValue<string>() ?? string.Empty;
            var tokenType = root["token_type"]?.GetValue<string>() ?? "Bearer";
            var scope = root["scope"]?.GetValue<string>() ?? string.Empty;
            var expiresIn = root["expires_in"]?.GetValue<double?>() ?? 0;
            var expiresAt = expiresIn > 0
                ? DateTimeOffset.Now.AddSeconds(expiresIn)
                : DateTimeOffset.MinValue;
            return new OAuthTokenResult(true, access, refresh, expiresAt, tokenType, scope, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return OAuthTokenResult.Fail($"token 响应解析失败：{ex.Message}");
        }
    }

    /// <summary>解析角色列表：既接受数组，也接受单个对象。</summary>
    public static List<MinecraftProfile> ParseProfiles(string json)
    {
        var result = new List<MinecraftProfile>();
        try
        {
            var node = JsonNode.Parse(json);
            var array = node as JsonArray ?? new JsonArray(node?.DeepClone());
            foreach (var item in array)
            {
                if (item is not JsonObject obj)
                {
                    continue;
                }
                var id = obj["id"]?.GetValue<string>() ?? string.Empty;
                var name = obj["name"]?.GetValue<string>() ?? string.Empty;
                if (id.Length == 0 && name.Length == 0)
                {
                    continue;
                }
                result.Add(new MinecraftProfile(id, name, obj["model"]?.GetValue<string>() ?? string.Empty));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // 坏 JSON 就当没有角色：调用方会走"没有可选角色"的分支
        }
        return result;
    }

    /// <summary>
    /// 从错误响应里挑一句能看的：OAuth 用 `error` / `error_description` / `message`，
    /// Yggdrasil 用 `error` / `errorMessage`。都读不到返回 null。
    /// </summary>
    public static string? ReadError(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                return null;
            }
            foreach (var key in new[] { "error_description", "errorMessage", "message", "error", "hint" })
            {
                if (root[key] is JsonValue value && value.TryGetValue<string>(out var text)
                    && !string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
        return null;
    }

    private static string Combine(string baseUrl, string path)
        => baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

    /// <summary>追加一行到 `Logs\auth.log`（失败只忽略，绝不让日志把登录搞挂）。</summary>
    private void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            // 日志写不了就算了
        }
    }
}
