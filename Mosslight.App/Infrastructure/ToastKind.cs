namespace Mosslight.App.Infrastructure;

/// <summary>浮层提示（toast）的语气。</summary>
public enum ToastKind
{
    /// <summary>做成了某件事（收藏、下载完成）。</summary>
    Success,

    /// <summary>只是说一句信息，没有做成也没失败。</summary>
    Info,

    /// <summary>提醒一句可能出问题的事，但没拦着。</summary>
    Warning,

    Error,
}
