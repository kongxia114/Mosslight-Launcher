namespace Mosslight.App.Infrastructure;

public static class ScrollLoader
{
    public const double DefaultThreshold = 64;

    /// <summary>要不要现在续取。</summary>
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
