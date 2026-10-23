using System.Windows;
using System.Windows.Media;

namespace Mosslight.App.Views;

public sealed class MemoryBar : FrameworkElement
{
    /// <summary>物理内存总量（MB）。</summary>
    public static readonly DependencyProperty TotalMbProperty = DependencyProperty.Register(
        nameof(TotalMb), typeof(int), typeof(MemoryBar),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>系统已经用掉的（MB）—— 这部分不是我们占的。</summary>
    public static readonly DependencyProperty UsedMbProperty = DependencyProperty.Register(
        nameof(UsedMb), typeof(int), typeof(MemoryBar),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>游戏堆上限（MB），就是 -Xmx。</summary>
    public static readonly DependencyProperty HeapMbProperty = DependencyProperty.Register(
        nameof(HeapMb), typeof(int), typeof(MemoryBar),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>JVM 堆之外的开销（MB，估算）。</summary>
    public static readonly DependencyProperty OverheadMbProperty = DependencyProperty.Register(
        nameof(OverheadMb), typeof(int), typeof(MemoryBar),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UsedBrushProperty = DependencyProperty.Register(
        nameof(UsedBrush), typeof(Brush), typeof(MemoryBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HeapBrushProperty = DependencyProperty.Register(
        nameof(HeapBrush), typeof(Brush), typeof(MemoryBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OverheadBrushProperty = DependencyProperty.Register(
        nameof(OverheadBrush), typeof(Brush), typeof(MemoryBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>没被占的那一段。</summary>
    public static readonly DependencyProperty EmptyBrushProperty = DependencyProperty.Register(
        nameof(EmptyBrush), typeof(Brush), typeof(MemoryBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>60% 那条红线。</summary>
    public static readonly DependencyProperty DangerBrushProperty = DependencyProperty.Register(
        nameof(DangerBrush), typeof(Brush), typeof(MemoryBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public int TotalMb
    {
        get => (int)GetValue(TotalMbProperty);
        set => SetValue(TotalMbProperty, value);
    }

    public int UsedMb
    {
        get => (int)GetValue(UsedMbProperty);
        set => SetValue(UsedMbProperty, value);
    }

    public int HeapMb
    {
        get => (int)GetValue(HeapMbProperty);
        set => SetValue(HeapMbProperty, value);
    }

    public int OverheadMb
    {
        get => (int)GetValue(OverheadMbProperty);
        set => SetValue(OverheadMbProperty, value);
    }

    public Brush? UsedBrush
    {
        get => (Brush?)GetValue(UsedBrushProperty);
        set => SetValue(UsedBrushProperty, value);
    }

    public Brush? HeapBrush
    {
        get => (Brush?)GetValue(HeapBrushProperty);
        set => SetValue(HeapBrushProperty, value);
    }

    public Brush? OverheadBrush
    {
        get => (Brush?)GetValue(OverheadBrushProperty);
        set => SetValue(OverheadBrushProperty, value);
    }

    public Brush? EmptyBrush
    {
        get => (Brush?)GetValue(EmptyBrushProperty);
        set => SetValue(EmptyBrushProperty, value);
    }

    public Brush? DangerBrush
    {
        get => (Brush?)GetValue(DangerBrushProperty);
        set => SetValue(DangerBrushProperty, value);
    }

    /// <summary>一次把四段数值设好。</summary>
    public void SetValues(int totalMb, int usedMb, int heapMb, int overheadMb)
    {
        TotalMb = totalMb;
        UsedMb = usedMb;
        HeapMb = heapMb;
        OverheadMb = overheadMb;
    }

    /// <summary>想画多高就给多高宽度跟着父级拉伸。</summary>
    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, 22);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        // 0.5 的缩进对应 Python 的 `rect.adjusted(0.5, 0.5, -0.5, -0.5)`：
        // 让 1px 的描边/红线落在像素中心上，不会被抗锯齿糊成两条
        var rect = new Rect(0.5, 0.5, width - 1, height - 1);
        var radius = rect.Height / 2;

        // ⚠️ 圆角矩形必须用 RectangleGeometry；用 ArcTo 手拼会被 SVG 规则放大半径，画出巨大鼓包。
        var geometry = new RectangleGeometry(rect, radius, radius);
        geometry.Freeze();

        dc.DrawGeometry(EmptyBrush, null, geometry);

        if (TotalMb <= 0)
        {
            return;
        }

        // 画到圆角里面去 —— 不裁剪的话方块段会盖住两端的圆角
        dc.PushClip(geometry);
        try
        {
            var unit = rect.Width / TotalMb;
            var x = rect.Left;
            foreach (var (value, brush) in new[]
                     {
                         (UsedMb, UsedBrush),
                         (HeapMb, HeapBrush),
                         (OverheadMb, OverheadBrush),
                     })
            {
                var segment = Math.Max(0, value) * unit;
                if (segment <= 0 || brush is null)
                {
                    continue;
                }
                dc.DrawRectangle(brush, null, new Rect(x, rect.Top, segment, rect.Height));
                x += segment;
            }

            // 60% 那条红线：游戏最多占用越过去就该考虑调小。
            // ⚠️ 比例从 SystemMemory 取，界面上别的地方（版本设置页 / 提示文案）也用同一个常量
            var limitX = rect.Left + rect.Width * Core.Services.Engine.SystemMemory.PageRiskRatio;
            if (limitX < rect.Right && DangerBrush is not null)
            {
                var pen = new Pen(DangerBrush, 2);
                pen.Freeze();
                dc.DrawLine(pen, new Point(limitX, rect.Top), new Point(limitX, rect.Bottom));
            }
        }
        finally
        {
            dc.Pop();
        }
    }
}
