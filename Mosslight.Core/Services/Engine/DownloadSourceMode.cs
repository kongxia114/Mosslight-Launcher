namespace Mosslight.Core.Services.Engine;

// 「下载源」策略：auto（镜像优先、官方兜底）/ official / mirror / dual（双流取先完成者）。
// ⚠️ config.json 里存字符串 `download_source` 而不是枚举序号，否则中间插入新值会静默改变策略。
// ⚠️ dual 只对存在镜像映射的地址有意义，其余地址退化为普通单源。

public enum DownloadSourceMode
{
    /// <summary>镜像优先、官方兜底（默认）。</summary>
    Auto = 0,

    Official = 1,

    /// <summary>只走 BMCLAPI（没被镜像的地址照旧直连）。</summary>
    Mirror = 2,

    /// <summary>镜像和官方同时下，谁先完成用谁。</summary>
    Dual = 3,
}

/// <summary>DownloadSourceMode 的字符串读写。</summary>
public static class DownloadSourceModes
{
    public const string AutoKey = "auto";
    public const string OfficialKey = "official";
    public const string MirrorKey = "mirror";
    public const string DualKey = "dual";

    /// <summary>配置文件里的字符串 → 策略。</summary>
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
