using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mosslight.Core.Appearance;
using Mosslight.Core.Settings;
using Mosslight.Core.Theme;

namespace Mosslight.App.Infrastructure;

/// <summary>
/// 背景画布：铺一张背景图（或纯色），上面压一层"压暗"。
///
/// 移植自 1.0.0 的 `ui/widgets/background_canvas.py`。
///
/// ## 几个必须照搬的点
/// 1. **主窗口那层要不透明**（用页面色）：铺法是「居中 / 适应」时图以外的地方
///    这里是不画的，露出来的就是窗口底色。露成黑色或穿到桌面都很难看。
/// 2. **压暗只对有图时生效**，alpha 直接用 0~255（范围 0~200，不是百分比）。
/// 3. 纯色背景（bg_color）**只在没有图的时候才画** —— 有图时颜色是被忽略的
///    （1.0.0 的 `appearance.get_bg()` 注释里写着「纯色才生效」）。
/// 4. 图被删了 / 挪走了：**保留配置里的值但这次不用它**（用户把文件放回来就恢复）。
///
/// ⚠️ 这个控件要铺满可用区域并**裁剪**（`ClipToBounds`）：「居中」模式下原图
///    比窗口大时，超出的部分必须裁掉，不然会盖到侧边栏上面去。
/// </summary>
public sealed class BackgroundHost : Grid
{
    private readonly Image _image = new();
    private readonly Border _dim = new();

    /// <summary>最近一次应用的配置（改窗口大小时要重算铺法）。</summary>
    private AppSettings? _settings;

    /// <summary>设计说明见 docs/design-notes.md#mosslight-app-infrastructure-backgroundhost-cs</summary>
    private const double LightVeilScale = 0.5;

    public BackgroundHost()
    {
        ClipToBounds = true;
        _image.Stretch = Stretch.UniformToFill;
        _image.SnapsToDevicePixels = true;
        _dim.Background = Brushes.Black;
        _dim.Visibility = Visibility.Collapsed;
        _dim.IsHitTestVisible = false;
        Children.Add(_image);
        Children.Add(_dim);
        SizeChanged += (_, _) => UpdateImageLayout();
    }

    /// <summary>按当前配置重画（启动时、改完背景设置后都调它）。</summary>
    public void Apply(AppSettings settings)
    {
        _settings = settings;
        var imagePath = settings.BackgroundImage;
        var usableImage = !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath);

        if (usableImage)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                // 一次性读进来（OnLoad）：不然 WPF 会一直握着文件句柄，
                // 用户在背景设置里换图时会因为"文件被占用"删不掉旧图
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                _image.Source = bitmap;
                _image.Visibility = Visibility.Visible;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
            {
                // 图坏了（不是图片、路径怪）就当没设 —— 不该让启动器起不来
                _image.Source = null;
                _image.Visibility = Visibility.Collapsed;
                usableImage = false;
            }
        }
        else
        {
            _image.Source = null;
            _image.Visibility = Visibility.Collapsed;
        }

        // ⚠️ 页面色这一支**必须走 SetResourceReference**，不能
        // 设计说明见 docs/design-notes.md#mosslight-app-infrastructure-backgroundhost-cs
        if (usableImage || !ColorMath.IsHex(settings.BackgroundColor))
        {
            SetResourceReference(BackgroundProperty, "BgPageBrush");
        }
        else
        {
            // 用户自己挑的纯色，跟主题无关，直接给一个刷子（局部值会盖掉资源引用）
            Background = new SolidColorBrush(ThemeApplier.ParseColor(settings.BackgroundColor));
        }

        var dim = settings.BackgroundDim;
        if (usableImage && dim > 0)
        {
            // alpha 直接用（1.0.0 就是这么干的，范围 0~200，不是百分比）
            var alpha = (byte)Math.Clamp(dim, 0, 255);

            // ⚠️ 蒙层颜色**跟主题走**，不能写死黑色。
            // ⚠️ 浅色主题要**打折**。同样 alpha 下白蒙层比黑蒙层刺眼得多 ——
            // 设计说明见 docs/design-notes.md#mosslight-app-infrastructure-backgroundhost-cs
            var dark = ThemeApplier.CurrentMode == PaletteMode.Dark;
            if (!dark)
            {
                alpha = (byte)Math.Clamp(Math.Round(alpha * LightVeilScale), 0, 255);
            }

            // ⚠️ 颜色是**运行时取的**，所以换主题时必须重跑本方法 ——
            //    调用方见 PersonalizePage.OnThemeSelected。
            var veil = ThemeApplier.ColorOfBrush("BgPageBrush");
            _dim.Background = new SolidColorBrush(Color.FromArgb(alpha, veil.R, veil.G, veil.B));
            _dim.Visibility = Visibility.Visible;
        }
        else
        {
            _dim.Visibility = Visibility.Collapsed;
        }

        UpdateImageLayout();
    }

    /// <summary>把「Windows 选择适合度」那五种铺法落到 WPF 的 Stretch/对齐上。</summary>
    private void UpdateImageLayout()
    {
        var mode = _settings?.BackgroundMode ?? BackgroundModes.Default;

        switch (mode)
        {
            case BackgroundModes.Fill:
                // 等比放大铺满，多出来的裁掉（不留黑边）
                _image.Stretch = Stretch.UniformToFill;
                _image.HorizontalAlignment = HorizontalAlignment.Center;
                _image.VerticalAlignment = VerticalAlignment.Center;
                break;
            case BackgroundModes.Fit:
                // 等比缩放到整张图都能看见，不够的地方留边
                _image.Stretch = Stretch.Uniform;
                _image.HorizontalAlignment = HorizontalAlignment.Center;
                _image.VerticalAlignment = VerticalAlignment.Center;
                break;
            case BackgroundModes.Stretch:
                // 不管比例直接拉满（会变形）
                _image.Stretch = Stretch.Fill;
                _image.HorizontalAlignment = HorizontalAlignment.Stretch;
                _image.VerticalAlignment = VerticalAlignment.Stretch;
                break;
            case BackgroundModes.Span:
                // 横向铺满、纵向居中裁切（对应多显示器"横跨所有显示"）
                _image.Stretch = Stretch.UniformToFill;
                _image.HorizontalAlignment = HorizontalAlignment.Center;
                _image.VerticalAlignment = VerticalAlignment.Center;
                break;
            case BackgroundModes.Center:
            default:
                // 原图大小放中间，四周留边（比窗口大就裁掉）
                _image.Stretch = Stretch.None;
                _image.HorizontalAlignment = HorizontalAlignment.Center;
                _image.VerticalAlignment = VerticalAlignment.Center;
                break;
        }
    }
}
