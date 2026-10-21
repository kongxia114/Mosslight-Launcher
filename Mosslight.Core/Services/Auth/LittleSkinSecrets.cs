using Mosslight.Core.Configuration;

namespace Mosslight.Core.Services.Auth;

/// <summary>
/// LittleSkin OAuth 的客户端凭据 —— 只是 <see cref="BuildSecrets"/> 的一层薄封装。
///
/// 真值来自（按优先级）：
///   1. CI 发版时用 Secrets 生成的 <c>BuildSecrets.Injected</c>；
///   2. 环境变量 `MOSSLIGHT_LITTLESKIN_CLIENT_ID` / `MOSSLIGHT_LITTLESKIN_CLIENT_SECRET`。
///
/// ⚠️ 仓库里永远是空的：`IsConfigured` 为 false 时界面要把登录按钮灰掉并说明原因。
/// ⚠️ 桌面端二进制里的 secret 一定能被提取：它只是"防止路人随便调"的门槛。
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

    /// <summary>按当前凭据造一份 <see cref="LittleSkinOptions"/>（日志默认写 `Logs\auth.log`）。</summary>
    public static LittleSkinOptions CreateOptions(string? logDir = null) => new()
    {
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        RedirectUri = RedirectUri,
        LogDir = logDir ?? Path.Combine(AppContext.BaseDirectory, "Logs"),
    };
}
