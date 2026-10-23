using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Mosslight.App.Infrastructure;

/// <summary>导航行前面那个小图标（矢量，跟文字一起变色）。</summary>
internal static class NavIcons
{
    // 导航 key → `Themes\NavIcons.xaml` 里那个 Geometry 资源。
    private static readonly Dictionary<string, string> Resources = new(StringComparer.Ordinal)
    {
        // 主侧栏
        ["home"] = "Icon.Play",
        ["download"] = "Icon.Download",
        ["personalize"] = "Icon.Palette",
        ["settings"] = "Icon.Settings",
        // 「更多」：PCL 那边是田字格，这里复用 Grid2x2（同一套 Lucide 线稿里最接近的）
        ["more"] = "Icon.Grid2x2",

        // 「更多 → 帮助与反馈」那几行（跟名单共用同一个行模板，图标走这里）
        ["help"] = "Icon.CircleHelp",
        ["feedback"] = "Icon.MessageCircle",
        // 「新功能投票」用"星星"：跟光影包那个 sparkles 是同一段图形，
        // 但这里单给一个"想法"的 key，别让"光影"的语义串到"投票"上。
        ["idea"] = "Icon.Sparkles",

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
        // 只能用 `{DynamicResource AccentBrush}` 绑，所以直接写在 MainWindow.xaml
        // 的 BrandHost 里（结构照 Create 的 24×24 Canvas 那套）。想换图形改那边的
        // `Data="{StaticResource Icon.Box}"` 即可。
    };

    /// <summary>一个导航图标。</summary>
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
            // （puzzle）和"留白多"的图标（package）显示出来一大一小 —— 看着像
            // 图标没对齐，其实是缩放基准不一样。缩放交给外面那个固定 24 的 Canvas。
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
        // 让 Viewbox 去缩**这个画布**，所有图标的缩放比例和线宽才会一致。
        // 直接把 Path 塞进 Viewbox 的话，Viewbox 会按各自图形的边界缩 ——
        // 同样 18px，有的图标会显得比别的大一圈。
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

    // 图标自己的画布边长（Lucide 的 viewBox 就是 0 0 24 24）。
    private const double CanvasSize = 24;

    /// <summary>一整行：`[图标] 文字`。</summary>
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
