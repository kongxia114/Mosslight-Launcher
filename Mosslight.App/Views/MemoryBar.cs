using System.Windows;
using System.Windows.Media;

namespace Mosslight.App.Views;

/// <summary>
/// 内存占用条。移植自 1.0.0 的 `ui/widgets/memory_bar.py`。
///
/// 四段，从左到右：
///
///     [系统已用][游戏最多占用][JVM 开销][剩余]
///
/// 外加一条**红线刻度**（物理内存的 60%）—— 越过它就容易开始换页，
/// 表现是"越玩越卡"，比崩溃更烦人。
///
/// ## 为什么是自绘而不是几个 Border 拼起来
/// 1.0.0 那边自绘的理由是"QSS 只能给一个控件一种底色"；WPF 其实能用 Grid + 星号列拼，
/// 但那个做法要在每次数值变化时重算列宽、还要自己裁剪圆角，代码量更大也更难读。
/// 这里跟 1.0.0 一样走 <see cref="OnRender"/>：一次画完，圆角靠
/// <see cref="DrawingContext.PushClip"/> 裁。
///
/// ## 颜色为什么不写死
/// 1.0.0 是从调色板现取（`border_control` / `accent` / `warn` / `bg_elevated` / `danger`）。
/// 这里落成 5 个 <see cref="Brush"/> 依赖属性，由 XAML 用 <c>{DynamicResource}</c> 填 ——
/// 于是换主题/换强调色会自动跟着变，不用任何人手写 <c>InvalidateVisual</c>。
/// 全部标了 <see cref="FrameworkPropertyMetadataOptions.AffectsRender"/>，
/// 资源一变 WPF 自己就会重画。
/// </summary>
public sealed class MemoryBar : FrameworkElement
{
    /// <summary>物理内存总量（MB）。&lt;= 0 = 读不出来，只画一条空轨道。</summary>
    public static readonly DependencyProperty TotalMbProperty = DependencyProperty.Register(
        nameof(TotalMb), typeof(int), typeof(MemoryBar),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>系统已经用掉的（MB）—— 这部分不是我们占的。</summary>
    public static readonly DependencyProperty UsedMbProperty = DependencyProperty.Register(
        nameof(UsedMb), typeof(int), typeof(MemoryBar),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>游戏堆上限（MB），就是 <c>-Xmx</c>。</summary>
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

    /// <summary>没被占的那一段（1.0.0 是 <c>bg_elevated</c>）。</summary>
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

    /// <summary>一次把四段数值设好（跟 1.0.0 的 <c>set_values()</c> 一一对应）。</summary>
    public void SetValues(int totalMb, int usedMb, int heapMb, int overheadMb)
    {
        TotalMb = totalMb;
        UsedMb = usedMb;
        HeapMb = heapMb;
        OverheadMb = overheadMb;
    }

    /// <summary>
    /// 想画多高就给多高（XAML 里 Height=22，跟 1.0.0 的 <c>BAR_HEIGHT</c> 一致）；
    /// 宽度跟着父级拉伸。
    /// </summary>
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

        // ⚠️ 圆角矩形**必须**用 RectangleGeometry，不要拿四段 ArcTo 手拼。
        // 设计说明见 docs/design-notes.md#mosslight-app-views-memorybar-cs
        var geometry = new RectangleGeometry(rect, radius, radius);
        geometry.Freeze();

        // 轨道底色（1.0.0 是 bg_elevated）
        dc.DrawGeometry(EmptyBrush, null, geometry);

        if (TotalMb <= 0)
        {
            // 读不出系统内存 —— 只画空轨道，不假装知道占用（1.0.0 同样提前返回）
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
