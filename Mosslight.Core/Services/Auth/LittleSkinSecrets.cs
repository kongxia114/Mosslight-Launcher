using Mosslight.Core.Configuration;

namespace Mosslight.Core.Services.Auth;

/// <summary>
/// LittleSkin OAuth 的客户端凭据 —— 只是 BuildSecrets 的一层薄封装；⚠️ 仓库里永远是空的：
/// `IsConfigured` 为 false 时界面要把登录按钮灰掉并说明原因。
/// </summary>
public static class LittleSkinSecrets
{
    /// <summary>回调地址必须和 LittleSkin 应用里登记的完全一致。</summary>
    public const string RedirectUri = "http://127.0.0.1:25566/callback";

    public static string ClientId => BuildSecrets.Get(SecretKeys.LittleSkinClientId);

    public static string ClientSecret => BuildSecrets.Get(SecretKeys.LittleSkinClientSecret);

    /// <summary>两个值都有才算配好了。</summary>
    public static bool IsConfigured
        => BuildSecrets.Has(SecretKeys.LittleSkinClientId)
           && BuildSecrets.Has(SecretKeys.LittleSkinClientSecret);

    /// <summary>按当前凭据造一份 LittleSkinOptions（日志默认写 `Logs\auth.log`）。</summary>
    public static LittleSkinOptions CreateOptions(string? logDir = null) => new()
    {
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        RedirectUri = RedirectUri,
        LogDir = logDir ?? Path.Combine(AppContext.BaseDirectory, "Logs"),
    };
}
