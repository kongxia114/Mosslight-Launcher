using System.IO;

namespace Mosslight.Core.Services.Auth;

/// <summary>
/// LittleSkin OAuth 的客户端凭据。
///
/// ## 仓库里这份是**空占位**
/// 真值只在两个地方：
///   1. **CI 发版时**：`.github/workflows/release.yml` 在 build 之前用 Secrets 覆盖生成这个文件
///      （仓库里始终是空的，谁 clone 都拿不到 secret）；
///   2. **本地调试时**：设环境变量 `MOSSLIGHT_LITTLESKIN_CLIENT_ID` /
///      `MOSSLIGHT_LITTLESKIN_CLIENT_SECRET`，不用改任何文件。
///
/// ⚠️ 别把真值填进仓库这份文件 —— 它会被提交、被镜像、被搜索到。
/// ⚠️ 桌面端二进制里的 secret **一定能被提取**：它是"防止路人随便调"的门槛，不是密码学意义上的密钥。
/// </summary>
public static class LittleSkinSecrets
{
    /// <summary>CI 覆盖这个常量；本地留空，走环境变量。</summary>
    private const string BuildClientId = "";

    /// <summary>CI 覆盖这个常量；本地留空，走环境变量。</summary>
    private const string BuildClientSecret = "";

    /// <summary>回调地址必须和 LittleSkin 应用里登记的完全一致。</summary>
    public const string RedirectUri = "http://127.0.0.1:25566/callback";

    public static string ClientId => Value(BuildClientId, "MOSSLIGHT_LITTLESKIN_CLIENT_ID");

    public static string ClientSecret => Value(BuildClientSecret, "MOSSLIGHT_LITTLESKIN_CLIENT_SECRET");

    /// <summary>两个值都有才算配好了；没配好时界面应该明确说"没配置"，而不是让用户点了没反应。</summary>
    public static bool IsConfigured => ClientId.Length > 0 && ClientSecret.Length > 0;

    /// <summary>按当前凭据造一份 <see cref="LittleSkinOptions"/>（日志默认写 `Logs\auth.log`）。</summary>
    public static LittleSkinOptions CreateOptions(string? logDir = null) => new()
    {
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        RedirectUri = RedirectUri,
        LogDir = logDir ?? Path.Combine(AppContext.BaseDirectory, "Logs"),
    };

    private static string Value(string buildValue, string envName)
    {
        if (!string.IsNullOrWhiteSpace(buildValue))
        {
            return buildValue;
        }
        return Environment.GetEnvironmentVariable(envName)?.Trim() ?? string.Empty;
    }
}
