using System.Threading;

namespace Mosslight.Core.Services.Engine;

/// <summary>下载进度的**广播点**：现在在下一批什么、每下到哪了。</summary>
public static class DownloadMonitor
{
    private static DownloadSnapshot? _last;
    private static DownloadManager? _owner;

    /// <summary>最近一份快照（没有下载在跑时是 null）。</summary>
    public static DownloadSnapshot? Last => Volatile.Read(ref _last);

    /// <summary>
    /// 开窗前调一下：把上一次的残留清掉；⚠️ 不清的话，窗口一打开会先显示**上一批**的结果（100% /
    /// 全部完成），然后才被新一批的第一个快照顶掉 —— 看着像"刚点开始就已经下完了"。
    /// </summary>
    public static void Reset()
    {
        Volatile.Write(ref _last, null);
        Volatile.Write(ref _owner, null);
    }

    /// <summary>取消**当前这一批**（下载窗口上那颗「取消下载」用的）。</summary>
    public static bool CancelAll()
    {
        var owner = Volatile.Read(ref _owner);
        if (owner is null)
        {
            return false;
        }
        owner.CancelAll();
        return true;
    }

    /// <summary>
    /// 管理器每做一次快照就往这儿放一份；⚠️ 生产代码里只有 DownloadManager.Snapshot 该调它。
    /// </summary>
    public static void Publish(DownloadManager owner, DownloadSnapshot snapshot)
    {
        Volatile.Write(ref _owner, owner);
        Volatile.Write(ref _last, snapshot);
    }
}
