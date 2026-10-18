namespace Mosslight.Core.Services.Engine;

//  ⚠️ 存进 config.json 用的是**字符串**（`download_source`），不是枚举序号：
//  ⚠️ 双流并发只对"有镜像映射"的地址有意义（见 DownloadManager.MirrorPairs）；
// 设计说明见 docs/design-notes.md#mosslight-core-services-engine-downloadsourcemode-cs

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
