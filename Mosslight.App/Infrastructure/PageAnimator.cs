using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Mosslight.Core.Animation;
using Mosslight.Core.Settings;

namespace Mosslight.App.Infrastructure;

/// <summary>一次入场里"单个元素"要播的东西：起手位移、位移动画、淡入动画、错峰间隔。</summary>
public sealed class SlideInPlan
{
    /// <summary>已经乘过速度倍率的那一档预设。</summary>
    public required AnimationPreset Preset { get; init; }

    /// <summary>水平（左右）还是垂直（上下）。</summary>
    public required bool Horizontal { get; init; }

    public required bool Fade { get; init; }

    /// <summary>起手位移（**带符号**，已经是 WPF 坐标系的值）。</summary>
    public required double StartOffset { get; init; }

    /// <summary>整条轨迹的总时长（PCL 那档可能比预设表里的 duration 长）。</summary>
    public required double DurationMs { get; init; }

    /// <summary>相邻两张卡片错开多久。</summary>
    public required double IntervalMs { get; init; }

    /// <summary>位移动画（模板；贴到元素上时要 Clone() 一份，见 AnimateIn 的说明）。</summary>
    public required DoubleAnimationBase Slide { get; init; }

    /// <summary>淡入动画（Fade 为 false 时是 null —— 这时**一个透明动画都不建**）。</summary>
    public DoubleAnimationBase? FadeAnimation { get; init; }

    /// <summary>
    /// 位移是不是关键帧动画（回弹 / 弹簧 / PCL 三种形态是；Simple 是一条缓动曲线）。
    /// </summary>
    public required bool KeyFramed { get; init; }

    /// <summary>量出来的过冲像素（0 = 这条轨迹不会冲过头）。</summary>
    public required double OvershootPx { get; init; }
}

/// <summary>列表/卡片的入场动画：错峰滑入（可选淡入）。</summary>
public static class PageAnimator
{
    /// <summary>
    /// 给容器里的每个孩子挂上入场动画； 动画结束**必须把 RenderTransform 留着**：如果动画结束后把
    /// 它清掉，元素会先"跳"回原位一帧（`FillBehavior.Stop` 的经典坑）。
    /// </summary>
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

    /// <summary>只给容器里**从开始**的孩子挂动画。</summary>
    public static void AnimateInFrom(Panel host, AppSettings settings, int firstIndex, bool forVersions = false)
    {
        var plan = BuildPlan(settings, forVersions);
        if (plan is null) return;
        Apply(host, plan, firstIndex);
    }

    /// <summary>按配置算一份入场计划；**总开关关掉时返回 null**。</summary>
    public static SlideInPlan? BuildPlan(AppSettings settings, bool forVersions = false)
    {
        if (!settings.AnimationsEnabled) return null;

        // ⚠️ 离屏截图模式下一个动画都不建：窗口从不 Show，WPF 动画时钟不会 tick，元素会永远停在起点。
        if (App.ScreenshotMode) return null;

        var preset = forVersions
            ? AnimationPresets.ForVersions(settings.AnimationPreset)
            : AnimationPresets.Preset(settings.AnimationPreset);
        var scaled = AnimationPresets.Scaled(preset, AnimationPresets.SpeedFactor(settings.AnimationSpeed));
        return BuildPlan(scaled, settings.AnimationFade);
    }

    /// <summary>
    /// 按一档**已经乘过速度倍率**的预设算计划（探针 / 自检直接调这个，不经过配置）。
    /// </summary>
    public static SlideInPlan BuildPlan(AnimationPreset scaled, bool fade)
    {
        var horizontal = scaled.Direction == AnimationDirection.Horizontal;
        var timeline = SlideInTrajectory.Timeline(scaled);

        // ⚠️ 水平方向起点要取负值，统一通过 SlideInTrajectory.ToCoordinate 换算，不要在各处另抄正负号。
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
    /// 透明度那一条；⚠️ 时长**必须**跟 SlideInTrajectory.OpacityAt 用同一份算法。
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

    /// <summary>把一份计划贴到容器的每个孩子上（错峰按 SlideInPlan.IntervalMs × 序号）。</summary>
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
            // 元素显示的是**基础值**，不先摆好就会在错峰等待期里闪一下"已经就位"的样子。
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
