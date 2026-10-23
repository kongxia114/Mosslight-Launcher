using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosslight.Core.Services.Auth;

/// <summary>Yggdrasil 的一个角色。</summary>
public sealed record YggdrasilProfile(string Id, string Name)
{
    /// <summary>带连字符的 UUID（Yggdrasil 返回的是 32 位无连字符）。</summary>
    public string Uuid => Id.Length == 32
        ? string.Join("-", Id[..8], Id[8..12], Id[12..16], Id[16..20], Id[20..])
        : Id;
}

/// <summary>密码登录（Yggdrasil `authenticate`）的结果。</summary>
public sealed record YggdrasilAuthResult(
    bool Ok,
    string AccessToken,
    string ClientToken,
    YggdrasilProfile? SelectedProfile,
    IReadOnlyList<YggdrasilProfile> AvailableProfiles,
    string Error)
{
    public static YggdrasilAuthResult Fail(string error)
        => new(false, string.Empty, string.Empty, null, Array.Empty<YggdrasilProfile>(), error);
}

/// <summary>标准 Yggdrasil 认证（第三方皮肤站 / 自建服务器）。</summary>
public sealed class CustomYggdrasilService
{
    private const string UserAgentText = "Mosslight/2.0 (+https://github.com/kongxia114/Mosslight-Launcher-2)";

    private readonly HttpClient _http;
    private readonly string _logPath;

    public CustomYggdrasilService(string? logDir = null, HttpClient? http = null)
    {
        var dir = logDir ?? Path.Combine(AppContext.BaseDirectory, "Logs");
        _logPath = Path.Combine(dir, "auth.log");
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        if (http is null)
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentText);
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }

    public string LogPath => _logPath;

    /// <summary>用用户名 / 密码向 `{baseUrl}/authserver/authenticate` 换 token。</summary>
    public async Task<YggdrasilAuthResult> AuthenticateWithPasswordAsync(
        string baseUrl, string username, string password, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return YggdrasilAuthResult.Fail("认证服务器地址不合法");
        }
        if (string.IsNullOrWhiteSpace(username))
        {
            return YggdrasilAuthResult.Fail("请填写用户名（邮箱）");
        }
        if (string.IsNullOrEmpty(password))
        {
            return YggdrasilAuthResult.Fail("请填写密码");
        }

        var endpoint = Combine(uri.ToString(), "authserver/authenticate");
        var payload = new JsonObject
        {
            ["agent"] = new JsonObject
            {
                ["name"] = "Minecraft",
                ["version"] = 1,
            },
            ["username"] = username.Trim(),
            ["password"] = password,
            ["clientToken"] = Guid.NewGuid().ToString("N"),
            ["requestUser"] = false,
        };

        try
        {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(endpoint, content, token).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var error = ReadError(json) ?? $"HTTP {(int)response.StatusCode}";
                Log($"密码登录失败（{endpoint}）：{error}");
                return YggdrasilAuthResult.Fail(error);
            }

            var result = ParseAuth(json);
            Log(result.Ok
                ? $"密码登录成功（{endpoint}）：角色 {result.AvailableProfiles.Count} 个"
                : $"密码登录响应异常（{endpoint}）：{result.Error}");
            return result;
        }
        catch (HttpRequestException ex)
        {
            Log($"密码登录网络失败（{endpoint}）：{ex.Message}");
            return YggdrasilAuthResult.Fail($"连不上认证服务器：{ex.Message}");
        }
        catch (TaskCanceledException)
        {
            Log($"密码登录超时（{endpoint}）");
            return YggdrasilAuthResult.Fail("认证服务器响应超时");
        }
    }

    /// <summary>用 refresh 的方式续期（有的服务器只认 clientToken + accessToken 校验）。</summary>
    public async Task<YggdrasilAuthResult> RefreshAsync(
        string baseUrl, string accessToken, string clientToken, CancellationToken token = default)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return YggdrasilAuthResult.Fail("认证服务器地址不合法");
        }
        var endpoint = Combine(uri.ToString(), "authserver/refresh");
        var payload = new JsonObject
        {
            ["accessToken"] = accessToken,
            ["clientToken"] = clientToken,
            ["requestUser"] = false,
        };
        try
        {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(endpoint, content, token).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return YggdrasilAuthResult.Fail(ReadError(json) ?? $"HTTP {(int)response.StatusCode}");
            }
            return ParseAuth(json);
        }
        catch (HttpRequestException ex)
        {
            Log($"续期失败（{endpoint}）：{ex.Message}");
            return YggdrasilAuthResult.Fail($"连不上认证服务器：{ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return YggdrasilAuthResult.Fail("认证服务器响应超时");
        }
    }

    // 解析（纯函数，自检直接调）

    public static YggdrasilAuthResult ParseAuth(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                return YggdrasilAuthResult.Fail("认证响应不是 JSON 对象");
            }

            var access = root["accessToken"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(access))
            {
                return YggdrasilAuthResult.Fail(ReadError(json) ?? "响应里没有 accessToken");
            }

            var client = root["clientToken"]?.GetValue<string>() ?? string.Empty;
            var selected = ReadProfile(root["selectedProfile"]);
            var available = new List<YggdrasilProfile>();
            if (root["availableProfiles"] is JsonArray array)
            {
                foreach (var item in array)
                {
                    var profile = ReadProfile(item);
                    if (profile is not null)
                    {
                        available.Add(profile);
                    }
                }
            }
            if (available.Count == 0 && selected is not null)
            {
                available.Add(selected);
            }

            return new YggdrasilAuthResult(true, access, client, selected, available, string.Empty);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return YggdrasilAuthResult.Fail($"认证响应解析失败：{ex.Message}");
        }
    }

    private static YggdrasilProfile? ReadProfile(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }
        var id = obj["id"]?.GetValue<string>() ?? string.Empty;
        var name = obj["name"]?.GetValue<string>() ?? string.Empty;
        return id.Length == 0 && name.Length == 0 ? null : new YggdrasilProfile(id, name);
    }

    /// <summary>
    /// Yggdrasil 的错误字段是 `error` + `errorMessage`；OAuth 型服务器可能是 `message`。
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
            foreach (var key in new[] { "errorMessage", "message", "error_description", "error" })
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

    private void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            // 日志写不了不影响登录
        }
    }
}
