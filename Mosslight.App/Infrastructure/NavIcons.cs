using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Mosslight.App.Infrastructure;

/// <summary>
/// 导航行前面那个小图标（矢量，跟文字一起变色）。
///
/// ## 图标从哪来
/// 图形数据在 `Themes\NavIcons.xaml`（**生成物**，别手改）：生成器是
/// `tools\gen_nav_icons.py`，图形数据来自 **Lucide**（ISC 许可）。
/// 哪个 key 配哪个图标见下面那张表 —— 映射是照 PCL2 的导航栏选的，
/// 理由写在生成器里。
///
/// ## 为什么是 `Path` + `Stroke`，不是图片
/// 这二十来个图标全是"24×24 画布 + 描边 + 2px 线宽"的线稿：
///   · 矢量 → 125% / 150% 缩放下不糊（位图在这个工程里吃过大亏，见
///     `Assets\icons\version\README.md` 那节"为什么图标会糊"）
///   · 描边 → 颜色能跟着行的 `Foreground` 走（悬停变亮、选中变主题色、禁用变灰），
///     位图做不到这件事（要么多做几套图，要么就是死的）
///
/// ## 两个容易踩的地方
/// ⚠️ **别设 `Fill`**：这些是线稿，填上就是一坨实心块。
/// ⚠️ 尺寸交给 <see cref="Viewbox"/> 缩：图形坐标是 24×24 的，直接设
///    `Path.Width` 不会缩放图形本身（`Path` 的 Width 只是布局尺寸）。
/// </summary>
internal static class NavIcons
{
    /// <summary>
    /// 导航 key → `Themes\NavIcons.xaml` 里那个 Geometry 资源。
    /// 认不出来的 key 返回 null（那一行就只有文字，不报错）。
    /// </summary>
    private static readonly Dictionary<string, string> Resources = new(StringComparer.Ordinal)
    {
        // 主侧栏
        ["home"] = "Icon.Play",
        ["download"] = "Icon.Download",
        ["personalize"] = "Icon.Palette",
        ["settings"] = "Icon.Settings",
        // 「更多」：PCL 那边是田字格，这里复用 Grid2x2（同一套 Lucide 线稿里最接近的）
        ["more"] = "Icon.Grid2x2",

        // 下载页左栏：版本
        ["minecraft"] = "Icon.Boxes",

        // 下载页左栏：社区资源
        ["mods"] = "Icon.Puzzle",
        ["modpacks"] = "Icon.Package",
        ["datapacks"] = "Icon.FileArchive",
        ["resourcepacks"] = "Icon.Layers",
        ["shaders"] = "Icon.Sparkles",
        ["worlds"] = "Icon.Globe",
        ["favorites"] = "Icon.Heart",

        // 下载页左栏：安装包
        ["install_minecraft"] = "Icon.Package",
        ["optifine"] = "Icon.Gauge",
        ["forge"] = "Icon.Anvil",
        ["neoforge"] = "Icon.Cat",
        ["cleanroom"] = "Icon.FlaskConical",
        ["fabric"] = "Icon.Scroll",
        ["legacy_fabric"] = "Icon.Scroll",
        ["quilt"] = "Icon.Grid2x2",
        ["labymod"] = "Icon.Box",
        ["liteloader"] = "Icon.Egg",

        // 不是导航行：浮层提示（toast）左边那个「成功」小图标
        ["toast"] = "Icon.CheckCircle",

        // ⚠️ 标题栏左上角那个品牌图标**不在**这里：它要跟着「个性化 → 强调色」变色，
        //    只能用 `{DynamicResource AccentBrush}` 绑，所以直接写在 MainWindow.xaml
        //    的 BrandHost 里（结构照 Create 的 24×24 Canvas 那套）。想换图形改那边的
        //    `Data="{StaticResource Icon.Box}"` 即可。
    };

    /// <summary>
    /// 一个导航图标。尺寸给的是**显示尺寸**（逻辑像素）；调用方自己决定要不要
    /// 留出右边距（返回的是裸图标，不做排版）。
    /// </summary>
    /// <param name="key">导航 key（见 <see cref="Resources"/>）。</param>
    /// <param name="size">显示边长（逻辑像素）。18 跟 14px 的字站在一起刚好。</param>
    /// <param name="strokeWidth">
    /// 线宽，**按图标自己 24×24 的画布算**（不是屏幕像素）。
    /// 2 = Lucide 原生的粗细，实际画出来是 `2 × size / 24 ≈ 1.5`；参考图那种
    /// 略壮的分量用 2.5（18px 下 ≈ 1.9）。
    /// </param>
    /// <param name="stroke">
    /// 指定线色。给了就**不**再跟着祖先 `Control` 的 `Foreground` 走 —— 浮层提示
    /// 那种"固定用强调色"的地方要的是这一档（它不在某个导航行里，祖先的
    /// Foreground 是正文色，画出来是灰的）。
    /// </param>
    public static FrameworkElement? Create(string key, double size = 18, double strokeWidth = 2.5, Brush? stroke = null)
    {
        if (!Resources.TryGetValue(key, out var resourceKey)
            || Application.Current is not { } app
            || app.TryFindResource(resourceKey) is not Geometry geometry)
        {
            return null;
        }

        var path = new Path
        {
            Data = geometry,
            StrokeThickness = strokeWidth,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            // ⚠️ 不设 Fill（见类注释）
            // ⚠️ 也不设 Stretch：拉伸会按**这张图自己的边界**缩，于是"画得满"的图标
            //    （puzzle）和"留白多"的图标（package）显示出来一大一小 —— 看着像
            //    图标没对齐，其实是缩放基准不一样。缩放交给外面那个固定 24 的 Canvas。
        };

        // 颜色跟着**所在的那一行**走：悬停 / 选中 / 禁用都是控件自己改 Foreground，
        // 图标跟上就自动一致了，不用为每个状态各写一套触发器。
        if (stroke is not null)
        {
            path.Stroke = stroke;
        }
        else
        {
            path.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1),
                FallbackValue = Brushes.Gray,
            });
        }

        // ⚠️ 中间那层 Canvas（固定 24×24）是关键：这些图形的坐标系就是 24×24，
        //    让 Viewbox 去缩**这个画布**，所有图标的缩放比例和线宽才会一致。
        //    直接把 Path 塞进 Viewbox 的话，Viewbox 会按各自图形的边界缩 ——
        //    同样 18px，有的图标会显得比别的大一圈。
        var canvas = new Canvas
        {
            Width = CanvasSize,
            Height = CanvasSize,
            Children = { path },
        };

        return new Viewbox
        {
            Child = canvas,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
    }

    /// <summary>图标自己的画布边长（Lucide 的 viewBox 就是 0 0 24 24）。</summary>
    private const double CanvasSize = 24;

    /// <summary>
    /// 一整行：`[图标] 文字`。没有图标配置、或者资源没加载出来时，
    /// 退化成**只有文字** —— 少个图标能看，空一行不能看。
    /// </summary>
    /// <param name="key">导航 key。</param>
    /// <param name="text">行文字（已经本地化好的）。</param>
    /// <param name="size">图标显示边长。</param>
    /// <param name="gap">图标和文字之间的间距。</param>
    public static object Row(string key, string text, double size = 18, double gap = 10)
    {
        var label = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (Create(key, size) is not { } icon)
        {
            return label;
        }

        icon.Margin = new Thickness(0, 0, gap, 0);
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(icon);
        panel.Children.Add(label);
        return panel;
    }
}
