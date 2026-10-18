using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Mosslight.Core.Animation;
using Mosslight.Core.Settings;

namespace Mosslight.App.Infrastructure;

/// <summary>
/// 一次入场里"单个元素"要播的东西：起手位移、位移动画、淡入动画、错峰间隔。
///
/// 为什么要有这个中间层（而不是像早先那样在 <see cref="PageAnimator.AnimateIn"/> 里
/// 现攒一个 <see cref="DoubleAnimation"/>）：形态一多，"一条 From/To 曲线"就装不下了 ——
/// 三段回弹、阻尼正弦、PCL 双位移都得**按时间取样**才能生成关键帧，而"取出来的点对不对"
/// 必须能被自检和探针**单独**验一遍。把它拆出来之后：
///   · <see cref="PageAnimator.AnimateIn"/> 只负责"把计划贴到每个孩子身上"；
///   · 自检 / 探针可以直接拿 <see cref="Slide"/> 里的关键帧逐点比 Python 的轨迹。
/// </summary>
public sealed class SlideInPlan
{
    /// <summary>已经乘过速度倍率的那一档预设。</summary>
    public required AnimationPreset Preset { get; init; }

    /// <summary>水平（左右）还是垂直（上下）。</summary>
    public required bool Horizontal { get; init; }

    /// <summary>要不要淡入。</summary>
    public required bool Fade { get; init; }

    /// <summary>起手位移（**带符号**，已经是 WPF 坐标系的值）。</summary>
    public required double StartOffset { get; init; }

    /// <summary>整条轨迹的总时长（PCL 那档可能比预设表里的 duration 长）。</summary>
    public required double DurationMs { get; init; }

    /// <summary>相邻两张卡片错开多久。</summary>
    public required double IntervalMs { get; init; }

    /// <summary>位移动画（模板；贴到元素上时要 <c>Clone()</c> 一份，见 AnimateIn 的说明）。</summary>
    public required DoubleAnimationBase Slide { get; init; }

    /// <summary>淡入动画（<see cref="Fade"/> 为 false 时是 null —— 这时**一个透明动画都不建**）。</summary>
    public DoubleAnimationBase? FadeAnimation { get; init; }

    /// <summary>位移是不是关键帧动画（回弹 / 弹簧 / PCL 三种形态是；Simple 是一条缓动曲线）。</summary>
    public required bool KeyFramed { get; init; }

    /// <summary>量出来的过冲像素（0 = 这条轨迹不会冲过头）。</summary>
    public required double OvershootPx { get; init; }
}

/// <summary>
/// 列表/卡片的入场动画：错峰滑入（可选淡入）。
///
/// 移植自 1.0.0 的 `ui/widgets/slide_in.py` + `core/anim_prefs.py`。
///
/// ## 轨迹算法在哪
/// 全部在 Core 的 <see cref="SlideInTrajectory"/> 里（纯函数、不依赖 WPF），
/// 这个类只负责"把那条轨迹变成 WPF 能播的动画"：
///
///   · <see cref="AnimationStyle.Simple"/> —— 一条 `DoubleAnimation` + 缓动曲线
///     （跟 1.0.0 的 `_track(offset, 0, duration, ease)` 一一对应，也是**最快**的一条路：
///      WPF 内置曲线能走合成线程）；
///   · 其余三种形态 —— `DoubleAnimationUsingKeyFrames`，关键帧的值就是
///     <see cref="SlideInTrajectory.DisplacementAt"/> 按时间取样出来的点。
///     为什么不用"自己写一条 IEasingFunction"：WPF 的缓动函数一旦是托管代码，
///     动画就**不能**再走合成线程（每次求值都要回调 UI 线程）；而关键帧是纯数据，
///     照样独立。取样步长 8ms + 分段边界 + 极值点，误差远小于半个像素（自检里量了）。
///
/// ## 跟 Python 一致的三条
/// 1. 一张卡片滑完的时间 = 预设的 duration（PCL 是慢轨时长），相邻两张错开 interval；
/// 2. 速度档把 duration 和 interval **一起**乘（只放慢单张会让整体断断续续）；
/// 3. 淡入是单独一档，关掉就只有位移。
///
/// ## 总开关
/// 关掉时 <see cref="BuildPlan(AppSettings, bool)"/> **返回 null** ——
/// 连计划都不建，更不会有动画对象、时钟、关键帧。不要退化成"时长为 0"：
/// 那样每个元素还是会挂一次时钟，等于关了个寂寞。
/// </summary>
public static class PageAnimator
{
    /// <summary>
    /// 给容器里的每个孩子挂上入场动画。
    ///
    /// ⚠️ 动画结束**必须把 RenderTransform 留着**：如果动画结束后把它清掉，
    /// 元素会先"跳"回原位一帧（`FillBehavior.Stop` 的经典坑）。这里用默认的
    /// `HoldEnd`，并且把初始位移设成终值 0 之外的值由动画自己收回去。
    /// </summary>
    /// <param name="host">要动画的容器（它的直接孩子会依次入场）。</param>
    /// <param name="settings">配置（总开关、淡入、风格、速度都从这里读）。</param>
    /// <param name="forVersions">版本列表那种"一次出来很多张"的场合，用更短的参数。</param>
    public static void AnimateIn(Panel host, AppSettings settings, bool forVersions = false)
    {
        var plan = BuildPlan(settings, forVersions);
        if (plan is null)
        {
            // 总开关关掉 = **根本不建动画**（见类注释）
            return;
        }
        Apply(host, plan);
    }

    /// <summary>
    /// 只给容器里**从 <paramref name="firstIndex"> 开始**的孩子挂动画。
    ///
    /// 为什么需要它：折叠组是**分页**建的（一次 60 行，底下挂「加载更多」）。
    /// 点「加载更多」时如果整个容器重播一遍，上面已经入过场的那几页会跟着再滑一次 ——
    /// 看着像整组被刷新了。错峰序号也从这一段的**第一个**重新起算。
    ///
    /// 1.0.0 的 `collapsible_group.py` 就是这么做的：每次 `_build_page()` 现造一个
    /// `StaggerReveal` 只装新加的那几行（见那边的 `_stagger_for_rows()`）。
    /// </summary>
    public static void AnimateInFrom(Panel host, AppSettings settings, int firstIndex, bool forVersions = false)
    {
        var plan = BuildPlan(settings, forVersions);
        if (plan is null) return;
        Apply(host, plan, firstIndex);
    }

    /// <summary>
    /// 按配置算一份入场计划；**总开关关掉时返回 null**。
    ///
    /// 单独暴露出来有两个用处：自检能直接断言"关掉时是 null（不是时长 0）"，
    /// 探针能拿到真正会播的那条动画去逐帧求值比对。调用方的语义没变 ——
    /// <see cref="AnimateIn"/> 还是原来那个签名、还是那个行为。
    /// </summary>
    public static SlideInPlan? BuildPlan(AppSettings settings, bool forVersions = false)
    {
        if (!settings.AnimationsEnabled) return null;

        // ⚠️ 离屏截图模式（`--screenshot`）下**一个动画都不建**。
        // 设计说明见 docs/design-notes.md#mosslight-app-infrastructure-pageanimator-cs
        if (App.ScreenshotMode) return null;

        var preset = forVersions
            ? AnimationPresets.ForVersions(settings.AnimationPreset)
            : AnimationPresets.Preset(settings.AnimationPreset);
        var scaled = AnimationPresets.Scaled(preset, AnimationPresets.SpeedFactor(settings.AnimationSpeed));
        return BuildPlan(scaled, settings.AnimationFade);
    }

    /// <summary>按一档**已经乘过速度倍率**的预设算计划（探针 / 自检直接调这个，不经过配置）。</summary>
    public static SlideInPlan BuildPlan(AnimationPreset scaled, bool fade)
    {
        var horizontal = scaled.Direction == AnimationDirection.Horizontal;
        var timeline = SlideInTrajectory.Timeline(scaled);

        // ⚠️ 水平方向的起点要取**负**值。
        // 设计说明见 docs/design-notes.md#mosslight-app-infrastructure-pageanimator-cs
        var start = SlideInTrajectory.ToCoordinate(scaled, scaled.OffsetPx);

        var slide = BuildSlideAnimation(scaled, horizontal, start, timeline);
        var fadeAnimation = fade ? BuildFadeAnimation(scaled, timeline) : null;

        return new SlideInPlan
        {
            Preset = scaled,
            Horizontal = horizontal,
            Fade = fade,
            StartOffset = start,
            DurationMs = timeline.TotalMs,
            IntervalMs = scaled.IntervalMs,
            Slide = slide,
            FadeAnimation = fadeAnimation,
            KeyFramed = slide is DoubleAnimationUsingKeyFrames,
            OvershootPx = AnimationRebound.Measure(scaled).OvershootPx,
        };
    }

    /// <summary>
    /// 位移那一条。
    ///
    /// Simple 形态（含 `elastic` / `back:` 那两档 —— 在 1.0.0 里它们是**缓动曲线**不是形态）
    /// 走单条 `DoubleAnimation`：起点 → 0、曲线由 `EasingFactory` 给，和 1.0.0 的
    /// `_track(offset, 0, duration, curve)` 逐字对应。
    ///
    /// 其余三种形态的轨迹**不是一条曲线能表达的**，所以按时间取样成关键帧：
    ///   · Bounce —— 三段顺序动画（滑到位 / 冲过头 / 收回），中间两个拐点是断点；
    ///   · Spring —— 前段滑到位、后段阻尼正弦；
    ///   · Pcl    —— 两条并行轨道的**和**（`_sync_offset()` 里那句 total = fast + slow）。
    /// </summary>
    private static DoubleAnimationBase BuildSlideAnimation(
        AnimationPreset scaled, bool horizontal, double start, SlideInTimeline timeline)
    {
        if (scaled.Style == AnimationStyle.Simple)
        {
            return new DoubleAnimation
            {
                From = start,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(timeline.TotalMs)),
                EasingFunction = EasingFactory.Create(scaled.Ease),
                FillBehavior = FillBehavior.HoldEnd,
            };
        }

        var keyFrames = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromMilliseconds(timeline.TotalMs)),
            FillBehavior = FillBehavior.HoldEnd,
        };
        foreach (var timeMs in SlideInTrajectory.KeyTimes(scaled))
        {
            // 线性插值是刻意的：曲线本身已经在取样点里了，再套一条缓动等于把轨迹改两遍
            keyFrames.KeyFrames.Add(new LinearDoubleKeyFrame(
                SlideInTrajectory.ToCoordinate(scaled, SlideInTrajectory.DisplacementAt(scaled, timeMs)),
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(timeMs))));
        }
        return keyFrames;
    }

    /// <summary>
    /// 透明度那一条。
    ///
    ///   · PCL —— 照 `_build_pcl()`：0 → 1、`max(60, min(100, fast_ms))` 毫秒、OutCubic；
    ///   · 其余形态 —— 2.0 既有行为（1.0.0 那边非 PCL 形态**压根不淡入**，见
    ///     SlideInTrajectory 的类注释第 3 条）：时长 = **整条轨迹 × 预设的 FadeRatio**、
    ///     曲线 = 该档自己的缓动。
    ///
    /// ⚠️ 时长**必须**跟 <see cref="SlideInTrajectory.OpacityAt"/> 用同一份算法。
    ///    自检里那条"工厂造出来的位移/透明动画跟 Core 的轨迹逐点一致"就是抓这个的 ——
    ///    两处各写一遍（比如这里忘了乘 FadeRatio）编译能过、动画照播，
    ///    只有把两边逐点拉出来比才看得出"预览的淡入比自检算的长一倍"。
    /// </summary>
    private static DoubleAnimationBase BuildFadeAnimation(AnimationPreset scaled, SlideInTimeline timeline)
    {
        var isPcl = scaled.Style == AnimationStyle.Pcl;
        var durationMs = isPcl
            ? timeline.FadeMs
            : Math.Max(1.0, timeline.TotalMs * scaled.FadeRatio);
        return new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            // PCL 那条固定是 Qt 的 OutCubic；其余形态沿用该档自己的缓动（2.0 既有行为）
            EasingFunction = EasingFactory.Create(isPcl ? "cubic" : scaled.Ease),
            FillBehavior = FillBehavior.HoldEnd,
        };
    }

    /// <summary>
    /// 把一份计划贴到容器的每个孩子上（错峰按 <see cref="SlideInPlan.IntervalMs"/> × 序号）。
    /// </summary>
    /// <param name="firstIndex">
    /// 从第几个孩子开始挂（默认 0 = 全部）。见 <see cref="AnimateInFrom"/>：
    /// 折叠组"加载更多"时只给新加的那几行挂，而且错峰序号从这一段的第一个重新起算。
    /// </param>
    public static void Apply(Panel host, SlideInPlan plan, int firstIndex = 0)
    {
        var property = plan.Horizontal
            ? TranslateTransform.XProperty
            : TranslateTransform.YProperty;

        var start = Math.Max(0, firstIndex);
        for (var i = start; i < host.Children.Count; i++)
        {
            if (host.Children[i] is not FrameworkElement child) continue;

            var translate = new TranslateTransform(
                plan.Horizontal ? plan.StartOffset : 0,
                plan.Horizontal ? 0 : plan.StartOffset);
            child.RenderTransform = translate;

            var begin = TimeSpan.FromMilliseconds(plan.IntervalMs * (i - start));
            var slide = (DoubleAnimationBase)plan.Slide.Clone();
            slide.BeginTime = begin;
            translate.BeginAnimation(property, slide);

            // ⚠️ 先把它按"起手状态"摆好（透明 / 偏移）再挂动画：动画的 BeginTime 没到之前
            //    元素显示的是**基础值**，不先摆好就会在错峰等待期里闪一下"已经就位"的样子。
            child.Opacity = plan.Fade ? 0.0 : 1.0;
            if (plan.FadeAnimation is { } fadeTemplate)
            {
                var appear = (DoubleAnimationBase)fadeTemplate.Clone();
                appear.BeginTime = begin;
                child.BeginAnimation(UIElement.OpacityProperty, appear);
            }
        }
    }

    /// <summary>把动画留下的状态清干净（切页面 / 重建列表前调，免得元素停在半路）。</summary>
    public static void Reset(Panel host)
    {
        foreach (var item in host.Children)
        {
            if (item is not FrameworkElement child) continue;
            child.BeginAnimation(UIElement.OpacityProperty, null);
            child.Opacity = 1.0;
            if (child.RenderTransform is TranslateTransform translate)
            {
                translate.BeginAnimation(TranslateTransform.XProperty, null);
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.X = 0;
                translate.Y = 0;
            }
        }
    }
}
