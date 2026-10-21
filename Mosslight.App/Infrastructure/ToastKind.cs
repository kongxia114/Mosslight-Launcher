namespace Mosslight.App.Infrastructure;

/// <summary>
/// 浮层提示（toast）的语气。决定图标 / 描边 / 底部那根进度条的颜色：
/// 成功用主色，信息偏蓝，警告偏黄，错误偏红。
/// </summary>
public enum ToastKind
{
    /// <summary>做成了某件事（收藏、下载完成）。</summary>
    Success,

    /// <summary>只是说一句信息，没有做成也没失败。</summary>
    Info,

    /// <summary>提醒一句可能出问题的事，但没拦着。</summary>
    Warning,

    /// <summary>真的失败了。</summary>
    Error,
}
