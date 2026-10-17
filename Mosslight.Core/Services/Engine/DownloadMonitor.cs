using System.Threading;

namespace Mosslight.Core.Services.Engine;

/// <summary>
/// 下载进度的**广播点**：现在在下一批什么、每下到哪了。
///
/// ## 为什么需要这么个东西
/// 2.0 的 <see cref="DownloadManager"/> 是**每个服务各建一个**（装原版 / 装加载器 /
/// 下模组 / 装整合包各一条路，都在方法里 new 出来的局部变量），界面能拿到的只有
/// `LaunchProgress`（一句话 + 一个百分比）。可下载窗口要的是**每个文件那一行**
/// （`[库] netty-buffer-4.2.15.Final.jar   0 B / 363.20 KB`）。
///
/// 1.0.0 是窗口直接握着 `manager` 引用；这边服务层把 manager 藏在方法里，
/// 于是加一层薄薄的转接：管理器每次 `Snapshot()` 顺手往这儿放一份，
/// 窗口自己定时来取。**不暴露 manager 本身**，也不要求服务层改签名。
///
/// ## 只有一格
/// ⚠️ 同一时刻只该有**一批**在跑（界面上一次只装一个东西）。真并发两批时，
///    后一批会盖掉前一批 —— 窗口显示最新的那批，够用了；
///    要做多批并列的话，这里得换成"按 id 的字典"，别硬塞进一个字段。
/// ⚠️ 只读不阻塞：`Last` 拿的是**不可变记录**（`DownloadSnapshot`），
///    窗口随便读，读到半新半旧也不会崩。
/// </summary>
public static class DownloadMonitor
{
    private static DownloadSnapshot? _last;
    private static DownloadManager? _owner;

    /// <summary>最近一份快照（没有下载在跑时是 null）。</summary>
    public static DownloadSnapshot? Last => Volatile.Read(ref _last);

    /// <summary>
    /// 开窗前调一下：把上一次的残留清掉。
    ///
    /// ⚠️ 不清的话，窗口一打开会先显示**上一批**的结果（100% / 全部完成），
    ///    然后才被新一批的第一个快照顶掉 —— 看着像"刚点开始就已经下完了"。
    /// </summary>
    public static void Reset()
    {
        Volatile.Write(ref _last, null);
        Volatile.Write(ref _owner, null);
    }

    /// <summary>
    /// 取消**当前这一批**（下载窗口上那颗「取消下载」用的）。
    /// 没有在跑的东西时返回 false（界面据此把按钮禁掉）。
    /// </summary>
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
    /// 管理器每做一次快照就往这儿放一份。
    ///
    /// ⚠️ 生产代码里只有 <see cref="DownloadManager.Snapshot"/> 该调它。
    ///    公开出来是给**自动化截图**用的（`--page downloadwindow` 灌一份假数据，
    ///    好让那个窗口在不真下载的情况下也能被截图验一遍）。
    /// </summary>
    public static void Publish(DownloadManager owner, DownloadSnapshot snapshot)
    {
        Volatile.Write(ref _owner, owner);
        Volatile.Write(ref _last, snapshot);
    }
}
