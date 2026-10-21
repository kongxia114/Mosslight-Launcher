using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mosslight.App.Infrastructure;

/// <summary>
/// 按钮按下去时轻微缩一下（96%）再弹回来 —— 纯视觉反馈，不改任何业务状态。
///
/// 用法：XAML 里 `infra:PressFeedback.IsEnabled="True"`。
/// 只动 RenderTransform，不碰 Layout（不会引起重排，滚动列表里也安全）。
/// </summary>
public static class PressFeedback
{
    private const double PressedScale = 0.96;
    private const int DurationMs = 90;

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
        element.PreviewMouseLeftButtonDown += (_, _) => Animate(element, PressedScale);
        element.PreviewMouseLeftButtonUp += (_, _) => Animate(element, 1);
        element.MouseLeave += (_, _) => Animate(element, 1);
        element.LostMouseCapture += (_, _) => Animate(element, 1);
        element.PreviewTouchDown += (_, _) => Animate(element, PressedScale);
        element.PreviewTouchUp += (_, _) => Animate(element, 1);
    }

    private static void Animate(FrameworkElement element, double target)
    {
        if (element.RenderTransform is not ScaleTransform scale)
        {
            return;
        }
        var animation = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(DurationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }
}
