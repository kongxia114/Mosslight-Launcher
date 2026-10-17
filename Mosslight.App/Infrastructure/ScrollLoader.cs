namespace Mosslight.App.Infrastructure;

/// <summary>
/// "滑到底就续取下一页"那个判断（详情页的版本分组 + 搜索结果列表共用）。
///
/// ## 为什么单独抽出来
/// 它是纯算术，但**错了不会报错**：阈值算反了表现成"永远不加载"、
/// 少了"视口还没量出来"那一条表现成"一打开就把 400 多组全铺出来"（实测踩过）。
/// 抽成一个纯函数，自检就能把几种边界直接钉住；界面那边只剩"挂事件 + 调它"。
/// （公开也是为这个：自检项目直接引用 App，要能调到它才钉得住。）
///
/// ## 视口高度是 0 的时候为什么**不能**加载
/// 详情页刚建出来、或者面板还没显示时，`ScrollViewer.ViewportHeight` 是 0。
/// 这时候"内容 ≤ 视口"恒成立，按补满一屏的逻辑就会一路铺到最后一组 ——
/// 那正是"打开详情页卡一下"的原因。
/// </summary>
public static class ScrollLoader
{
    /// <summary>
    /// 离底部还有这么远（逻辑像素）就开始取下一页。
    ///
    /// ⚠️ **故意留得很小**（2026-10-18 用户反馈"滑到底加载有点暴躁"）：
    ///    原来给的是 160，等于还剩一个半分组就开加载 —— 用户只是滚了一下
    ///    想看看下面有什么，列表就自己长出来了，看着像界面在抢操作。
    ///    现在压到 **64**（差不多一个折叠标题的高度）：**真滑到底了**才续。
    ///    宁可让用户多滚半屏，也不要在没到底的时候自己动。
    /// </summary>
    public const double DefaultThreshold = 64;

    /// <summary>
    /// 要不要现在续取。
    /// </summary>
    /// <param name="offset">当前竖向滚动位置。</param>
    /// <param name="viewport">视口高度。</param>
    /// <param name="extent">内容总高度。</param>
    /// <param name="threshold">提前量（默认 <see cref="DefaultThreshold"/>）。</param>
    public static bool ShouldLoadMore(
        double offset, double viewport, double extent, double threshold = DefaultThreshold)
    {
        // 还没布局（离屏 / 面板隐藏）：量出来的都是 0，此刻不能下任何结论
        if (viewport <= 0 || extent <= 0)
        {
            return false;
        }

        // 内容还不够一屏：滚动条都没出来，用户永远触发不了"到底" —— 直接续
        if (extent <= viewport)
        {
            return true;
        }

        return offset + viewport >= extent - threshold;
    }
}
