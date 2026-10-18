namespace Mosslight.Core.Services.Engine;

// ============================================================================
//  「下载源」策略：设置 → 下载 里那四个选项（2026-10 用户要求加的）
//
//  | 值       | 行为 |
//  |----------|------|
//  | auto     | **默认**：镜像（BMCLAPI）优先、失败换官方；镜像连续失败会熔断 60 秒 |
//  | official | 只用官方源（Mojang / Forge / NeoForge 直连） |
//  | mirror   | 只用 BMCLAPI；**它没镜像的地址**（Modrinth / CurseForge / Quilt meta 等）照旧直连 |
//  | dual     | 双流并发：同一个文件同时从镜像和官方下，谁先完成用谁 |
//
//  ⚠️ 存进 config.json 用的是**字符串**（`download_source`），不是枚举序号：
//     以后在枚举中间插一个值，序号型的配置就会静默变成另一个策略。
//  ⚠️ 双流并发只对"有镜像映射"的地址有意义（见 DownloadManager.MirrorPairs）；
//     没有镜像的地址它退化成普通单源，不会凭空多出一条流。
// ============================================================================

/// <summary>下载源策略。</summary>
public enum DownloadSourceMode
{
    /// <summary>镜像优先、官方兜底（默认）。</summary>
    Auto = 0,

    /// <summary>只走官方源。</summary>
    Official = 1,

    /// <summary>只走 BMCLAPI（没被镜像的地址照旧直连）。</summary>
    Mirror = 2,

    /// <summary>镜像和官方同时下，谁先完成用谁。</summary>
    Dual = 3,
}

/// <summary><see cref="DownloadSourceMode"/> 的字符串读写。</summary>
public static class DownloadSourceModes
{
    public const string AutoKey = "auto";
    public const string OfficialKey = "official";
    public const string MirrorKey = "mirror";
    public const string DualKey = "dual";

    /// <summary>配置文件里的字符串 → 策略。认不出来的（老配置 / 手改坏了）按默认 <c>auto</c>。</summary>
    public static DownloadSourceMode Parse(string? key)
        => (key ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            OfficialKey => DownloadSourceMode.Official,
            MirrorKey => DownloadSourceMode.Mirror,
            DualKey => DownloadSourceMode.Dual,
            _ => DownloadSourceMode.Auto,
        };

    /// <summary>策略 → 配置文件里的字符串。</summary>
    public static string ToKey(DownloadSourceMode mode) => mode switch
    {
        DownloadSourceMode.Official => OfficialKey,
        DownloadSourceMode.Mirror => MirrorKey,
        DownloadSourceMode.Dual => DualKey,
        _ => AutoKey,
    };
}
