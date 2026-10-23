using System.Collections.Generic;

namespace Mosslight.Core.Configuration;

/// <summary>
/// 编译期注入的凭据 + 环境变量兜底；⚠️ 桌面端二进制里的任何密钥都能被提取 —— 这个类只保证"别把它
/// 写进仓库"。
/// </summary>
public static class BuildSecrets
{
    // ⚠️ CI 会整段替换成带值的字典字面量（键统一小写点号）。
    private static readonly Dictionary<string, string> Injected =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>拿一个凭据：先编译期注入，再环境变量；都没有返回空串（不抛异常）。</summary>
    public static string Get(string key)
    {
        if (Injected.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
        return Environment.GetEnvironmentVariable(EnvName(key))?.Trim() ?? string.Empty;
    }

    /// <summary>有没有配：空串 / 全空白都算没有。</summary>
    public static bool Has(string key) => Get(key).Length > 0;

    /// <summary>`littleskin.client_id` → `MOSSLIGHT_LITTLESKIN_CLIENT_ID`。</summary>
    public static string EnvName(string key)
        => "MOSSLIGHT_" + key.Replace('.', '_').Replace('-', '_').ToUpperInvariant();
}

/// <summary>已经用到的 / 预留给将来的键。</summary>
public static class SecretKeys
{
    public const string LittleSkinClientId = "littleskin.client_id";
    public const string LittleSkinClientSecret = "littleskin.client_secret";

    /// <summary>
    /// 微软登录（**预留**）：Azure 应用注册的客户端 ID，公共客户端不需要 secret。
    /// </summary>
    public const string MicrosoftClientId = "microsoft.client_id";

    /// <summary>微软登录（**预留**）：租户，默认 `consumers`。</summary>
    public const string MicrosoftTenant = "microsoft.tenant";
}
