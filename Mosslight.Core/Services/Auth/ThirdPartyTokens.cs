using System.Text;

namespace Mosslight.Core.Services.Auth;

/// <summary>第三方账户的 token 续期。</summary>
public static class ThirdPartyTokens
{
    /// <summary>距离过期不到这么久就续。</summary>
    public static readonly TimeSpan RefreshWindow = TimeSpan.FromHours(24);

    /// <summary>该不该续：只有"知道过期时间且快到了"才续，避免每次启动都打一次服务器。</summary>
    public static bool NeedsRefresh(Account account)
        => account.Kind == AccountKind.ThirdParty
           && account.AuthServer.Length > 0
           && account.TokenExpiresAt is { } expiresAt
           && expiresAt - DateTimeOffset.Now < RefreshWindow;

    public static async Task<(Account Account, string? Error)> EnsureFreshAsync(
        Account account, CancellationToken token = default)
    {
        if (!NeedsRefresh(account))
        {
            return (account, null);
        }

        if (account.RefreshToken.Length > 0)
        {
            var service = new LittleSkinAuthService(new LittleSkinOptions
            {
                ClientId = LittleSkinSecrets.ClientId,
                ClientSecret = LittleSkinSecrets.ClientSecret,
                RedirectUri = LittleSkinSecrets.RedirectUri,
            });
            var result = await service.RefreshTokenAsync(account.RefreshToken, token).ConfigureAwait(false);
            if (!result.Ok)
            {
                return (account, result.Error ?? "刷新 token 失败");
            }
            return (account with
            {
                AccessToken = result.AccessToken,
                RefreshToken = result.RefreshToken.Length > 0 ? result.RefreshToken : account.RefreshToken,
                TokenExpiresAt = result.ExpiresAt == DateTimeOffset.MinValue ? account.TokenExpiresAt : result.ExpiresAt,
            }, null);
        }

        if (account.ClientToken.Length > 0)
        {
            var service = new CustomYggdrasilService();
            var result = await service.RefreshAsync(account.AuthServer, account.AccessToken,
                account.ClientToken, token).ConfigureAwait(false);
            if (!result.Ok)
            {
                return (account, result.Error ?? "刷新 token 失败");
            }
            return (account with
            {
                AccessToken = result.AccessToken,
                ClientToken = result.ClientToken.Length > 0 ? result.ClientToken : account.ClientToken,
            }, null);
        }

        return (account, "没有可用的续期凭据（refresh_token / client_token 都为空）");
    }
}
