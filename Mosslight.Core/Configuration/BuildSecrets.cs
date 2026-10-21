using System.Collections.Generic;

namespace Mosslight.Core.Configuration;

/// <summary>
/// 编译期注入的凭据 + 环境变量兜底。
///
/// ## 仓库里这份是**空占位**
/// 真值只来自两个地方，都不进 git：
///   1. **CI 发版时**：`.github/workflows/release.yml` 在 build 之前用 Secrets 覆盖
///      <c>Injected</c> 字典（`tools/make-build-secrets.ps1` 生成）；
///   2. **本地调试时**：环境变量 `MOSSLIGHT_&lt;KEY&gt;`（点号换下划线、全大写）。
///
/// ⚠️ 桌面端二进制里的任何密钥都能被提取 —— 这个类只保证"别把它写进仓库"。
/// ⚠️ **不**读磁盘上的明文文件：那比编进 exe 更糟。
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

/// <summary>
/// 已经用到的 / 预留给将来的键。集中在这里是为了**别拼错**，也方便搜"都用过哪些凭据"。
///
/// 「预留」= 代码和 CI 里都有这个键的位置，但 GitHub 上还没建对应的 secret；
/// 没建时 `Get()` 返回空串，功能按"未配置"处理即可，不会报错。
/// </summary>
public static class SecretKeys
{
    public const string LittleSkinClientId = "littleskin.client_id";
    public const string LittleSkinClientSecret = "littleskin.client_secret";

    /// <summary>微软登录（**预留**）：Azure 应用注册的客户端 ID，公共客户端不需要 secret。</summary>
    public const string MicrosoftClientId = "microsoft.client_id";

    /// <summary>微软登录（**预留**）：租户，默认 `consumers`。</summary>
    public const string MicrosoftTenant = "microsoft.tenant";
}
