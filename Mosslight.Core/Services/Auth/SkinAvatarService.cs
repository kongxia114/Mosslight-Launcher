using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosslight.Core.Services.Auth;

/// <summary>从第三方皮肤站把角色皮肤拉下来，存成启动器认的 `skins/&lt;档案名&gt;.png`。</summary>
public static class SkinAvatarService
{
    private const string UserAgentText = "Mosslight/2.0 (+https://github.com/kongxia114/Mosslight-Launcher)";

    /// <summary>本地皮肤放多久算"过期"。</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(6);

    /// <summary>这个账户的本地皮肤要不要重新拉：没有 / 超过 RefreshAfter 都要。</summary>
    public static bool NeedsRefresh(string configDir, string accountName)
    {
        var path = SkinPathFor(configDir, accountName);
        if (!File.Exists(path))
        {
            return true;
        }
        return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > RefreshAfter;
    }

    /// <summary>按 TTL 判断要不要刷，需要才真去拉。</summary>
    public static async Task<(bool Ok, string Message)> RefreshIfStaleAsync(
        Account account, string? configDir = null, bool force = false, CancellationToken token = default)
    {
        var dir = configDir ?? Path.Combine(AppContext.BaseDirectory, "Mosslight");
        if (!force && !NeedsRefresh(dir, account.Name))
        {
            return (true, "还新鲜，跳过");
        }
        return await TryFetchAndSaveAsync(account, dir, token).ConfigureAwait(false);
    }

    /// <summary>批量刷新（账户页打开 / 用户点"刷新头像"）。</summary>
    public static async Task<int> RefreshAllAsync(
        IEnumerable<Account> accounts, string? configDir = null, bool force = false,
        CancellationToken token = default)
    {
        var dir = configDir ?? Path.Combine(AppContext.BaseDirectory, "Mosslight");
        var updated = 0;
        foreach (var account in accounts)
        {
            if (account.Kind != AccountKind.ThirdParty || account.AuthServer.Length == 0)
            {
                continue;
            }
            if (!force && !NeedsRefresh(dir, account.Name))
            {
                continue;
            }
            var result = await TryFetchAndSaveAsync(account, dir, token).ConfigureAwait(false);
            if (result.Ok)
            {
                updated++;
            }
        }
        return updated;
    }

    /// <summary>皮肤放哪：跟 PlayerAvatars.CustomSkinDirName 一致（`/skins/.png`）。</summary>
    public static string SkinPathFor(string configDir, string accountName)
        => Path.Combine(configDir, "skins", accountName + ".png");

    /// <summary>拉一次皮肤并落盘。</summary>
    public static async Task<(bool Ok, string Message)> TryFetchAndSaveAsync(
        Account account, string? configDir = null, CancellationToken token = default)
    {
        if (account.Kind != AccountKind.ThirdParty || account.AuthServer.Length == 0)
        {
            return (false, "不是第三方账户");
        }
        if (account.Uuid.Length == 0)
        {
            return (false, "账户没有 UUID");
        }

        var dir = configDir ?? Path.Combine(AppContext.BaseDirectory, "Mosslight");
        var uuid = account.Uuid.Replace("-", string.Empty).ToLowerInvariant();
        var profileUrl = $"{account.AuthServer.TrimEnd('/')}/sessionserver/session/minecraft/profile/{uuid}";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentText);

            var profileJson = await http.GetStringAsync(profileUrl, token).ConfigureAwait(false);
            var skinUrl = ParseSkinUrl(profileJson);
            if (skinUrl is null)
            {
                return (false, "这个角色没有设置皮肤（或服务器没返回 textures）");
            }

            var bytes = await http.GetByteArrayAsync(skinUrl, token).ConfigureAwait(false);
            if (bytes.Length < 64 || bytes[0] != 0x89 || bytes[1] != 0x50)
            {
                return (false, "下载到的皮肤不是 PNG");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(SkinPathFor(dir, account.Name))!);
            var path = SkinPathFor(dir, account.Name);
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, token).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
            return (true, path);
        }
        catch (HttpRequestException ex)
        {
            return (false, $"拉皮肤失败：{ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return (false, "拉皮肤超时");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            return (false, $"保存皮肤失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 从 session 响应里挖皮肤地址：`properties[].value` 是 base64 的 JSON，里面
    /// `textures.SKIN.url` 才是真的 PNG 地址。
    /// </summary>
    public static string? ParseSkinUrl(string profileJson)
    {
        try
        {
            if (JsonNode.Parse(profileJson) is not JsonObject root
                || root["properties"] is not JsonArray properties)
            {
                return null;
            }
            foreach (var node in properties)
            {
                if (node is not JsonObject property
                    || !string.Equals(property["name"]?.GetValue<string>(), "textures", StringComparison.Ordinal))
                {
                    continue;
                }
                var base64 = property["value"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(base64))
                {
                    continue;
                }
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                if (JsonNode.Parse(decoded)?["textures"]?["SKIN"]?["url"]?.GetValue<string>() is { Length: > 0 } url)
                {
                    return url;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
        return null;
    }
}
