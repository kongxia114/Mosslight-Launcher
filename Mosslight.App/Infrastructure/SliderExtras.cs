using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Mosslight.App.Infrastructure;

/// <summary>滑块右侧"预警区"占总宽的比例（0 = 不要，0.4 = 最右 40% 是黄区）。</summary>
public static class SliderExtras
{
    public static readonly DependencyProperty WarningRatioProperty =
        DependencyProperty.RegisterAttached(
            "WarningRatio", typeof(double), typeof(SliderExtras),
            new PropertyMetadata(0.0));

    public static void SetWarningRatio(DependencyObject element, double value)
        => element.SetValue(WarningRatioProperty, value);

    public static double GetWarningRatio(DependencyObject element)
        => (double)element.GetValue(WarningRatioProperty);
}

/// <summary>比例 × 实际宽度 → 预警区像素宽。返回 0 时给 0（NaN 会让 Border 撑满）。</summary>
public sealed class RatioToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2
            || values[0] is not double ratio
            || values[1] is not double width)
        {
            return 0d;
        }
        var result = ratio * width;
        return double.IsNaN(result) || double.IsInfinity(result) || result <= 0 ? 0d : result;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
