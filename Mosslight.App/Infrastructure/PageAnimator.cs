using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Mosslight.Core.Animation;
using Mosslight.Core.Settings;

namespace Mosslight.App.Infrastructure;

/// <summary>
/// 列表/卡片的入场动画：错峰滑入（可选淡入）。
///
/// 移植自 1.0.0 的 `ui/widgets/slide_in.py` + `core/anim_prefs.py`。
///
/// ## 为什么 WPF 这边"看着像"却简单得多
/// Python 那边要自己算曲线、自己挂 `QGraphicsOpacityEffect`（渲染到离屏缓冲再合成，
/// 是真正的开销来源），所以它专门有个「动效总开关」把整层动画控件摘掉。
/// WPF 的 `TranslateTransform` + `DoubleAnimation` 是**合成线程**做的，
/// 开销小得多 —— 但**总开关照样要生效**：关掉时这里直接不建任何动画，
/// 而不是"时长为 0"（时长为 0 仍然会建时钟、仍然会走一遍属性系统）。
///
/// ## 跟 Python 一致的三条
/// 1. 一张卡片滑完的时间 = `duration`，相邻两张错开 `interval`；
/// 2. 速度档把 duration 和 interval **一起**乘（只放慢单张会让整体断断续续）；
/// 3. 淡入是单独一档，关掉就只有位移（省掉一次透明图层合成）。
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
        if (!settings.AnimationsEnabled)
        {
            // 总开关关掉 = **根本不建动画**。不要退化成"时长为 0"：
            // 那样每个元素还是会挂一次时钟，等于关了个寂寞。
            return;
        }

        var preset = forVersions
            ? AnimationPresets.ForVersions(settings.AnimationPreset)
            : AnimationPresets.Preset(settings.AnimationPreset);
        var scaled = AnimationPresets.Scaled(preset, AnimationPresets.SpeedFactor(settings.AnimationSpeed));
        var fade = settings.AnimationFade;

        var horizontal = scaled.Direction == AnimationDirection.Horizontal;
        var interval = TimeSpan.FromMilliseconds(scaled.IntervalMs);
        var duration = new Duration(TimeSpan.FromMilliseconds(scaled.DurationMs));
        var easing = EasingFactory.Create(scaled.Ease);

        for (var i = 0; i < host.Children.Count; i++)
        {
            if (host.Children[i] is not FrameworkElement child) continue;

            var offset = scaled.OffsetPx;

            // ⚠️ 水平方向的起点要取**负**值。
            //    `OffsetPx` 的语义是"起点离终点多远"（见 AnimationPresets 的注释）：
            //    水平 = 起点在终点的**左边**、垂直 = 起点在终点的**下边**。
            //    WPF 的 TranslateTransform.X 正数往右，所以水平那一档要写成 -offset，
            //    否则「左侧滑入」会变成从右边滑进来 —— 跟预设名字、跟 1.0.0 的
            //    `SlideInRow._apply()`（那边是 `child.move(-offset, 0)`）正好相反。
            //    垂直方向 +offset 是对的（下边 → 往上升），别一起改。
            var start = horizontal ? -offset : offset;

            var translate = new TranslateTransform(
                horizontal ? start : 0,
                horizontal ? 0 : start);
            child.RenderTransform = translate;
            child.Opacity = fade ? 0.0 : 1.0;

            var begin = TimeSpan.FromMilliseconds(scaled.IntervalMs * i);

            var slide = new DoubleAnimation
            {
                From = start,
                To = 0,
                Duration = duration,
                BeginTime = begin,
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd,
            };
            var property = horizontal
                ? TranslateTransform.XProperty
                : TranslateTransform.YProperty;
            translate.BeginAnimation(property, slide);

            if (fade)
            {
                var appear = new DoubleAnimation
                {
                    From = 0.0,
                    To = 1.0,
                    Duration = duration,
                    BeginTime = begin,
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.HoldEnd,
                };
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
