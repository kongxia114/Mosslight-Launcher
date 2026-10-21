using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mosslight.App.Infrastructure;

/// <summary>
/// 通用的「按压」与「悬停微缩放」反馈。
///
/// 两个附加属性都只动 `RenderTransform`，**不碰 Layout**（不触发重排，
/// 列表里、滚动容器里用都安全）。用法：
///
/// ```xml
/// <Button infra:PressFeedback.IsEnabled="True"
///         infra:HoverLift.IsEnabled="True" />
/// ```
///
/// 也可以挂在 Style 的 Setter 里，让一整类按钮都用上。
/// </summary>
public static class PressFeedback
{
    private const double PressedScale = 0.96;
    private const int PressDurationMs = 90;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(PressFeedback),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true)
        {
            return;
        }

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        if (element.RenderTransform is not ScaleTransform)
        {
            element.RenderTransform = new ScaleTransform(1, 1);
        }

        // 用 Preview 而不是 Click：Click 只在"按下又在里面松开"时才有，
        // 而按压反馈应该在手指刚按下去的那一刻就给。
        element.PreviewMouseLeftButtonDown += (_, _) => Animate(element, PressedScale, PressDurationMs);
        element.PreviewMouseLeftButtonUp += (_, _) => Animate(element, 1, PressDurationMs);
        element.MouseLeave += (_, _) => Animate(element, 1, PressDurationMs);
        element.LostMouseCapture += (_, _) => Animate(element, 1, PressDurationMs);
        element.PreviewTouchDown += (_, _) => Animate(element, PressedScale, PressDurationMs);
        element.PreviewTouchUp += (_, _) => Animate(element, 1, PressDurationMs);
    }

    internal static void Animate(FrameworkElement element, double target, int durationMs)
    {
        if (element.RenderTransform is not ScaleTransform scale)
        {
            return;
        }
        var animation = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }
}

/// <summary>
/// 悬停时轻微放大（1.015）—— 只给"大块头"用（主按钮、启动按钮）；
/// 列表项放大容易跟相邻元素打架，那些只给按压反馈。
/// </summary>
public static class HoverLift
{
    private const double HoverScale = 1.015;
    private const int DurationMs = 140;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(HoverLift),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true)
        {
            return;
        }

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        if (element.RenderTransform is not ScaleTransform)
        {
            element.RenderTransform = new ScaleTransform(1, 1);
        }

        element.MouseEnter += (_, _) => PressFeedback.Animate(element, HoverScale, DurationMs);
        element.MouseLeave += (_, _) => PressFeedback.Animate(element, 1, DurationMs);
    }
}

/// <summary>页面切换时的入场动画：淡入 + 轻微上移（190ms，ease-out）。</summary>
public static class PageTransition
{
    private const int DurationMs = 190;
    private const double OffsetY = 10;

    public static void PlayIn(FrameworkElement page)
    {
        page.Opacity = 0;
        var translate = new TranslateTransform(0, OffsetY);
        page.RenderTransform = translate;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(DurationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var slide = new DoubleAnimation(OffsetY, 0, TimeSpan.FromMilliseconds(DurationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        page.BeginAnimation(UIElement.OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }
}
