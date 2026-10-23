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
/// 背景画布：铺一张背景图（或纯色），上面压一层"压暗"；⚠️ 这个控件要铺满可用区域并**裁剪**（
/// `ClipToBounds`）：「居中」模式下原图比窗口大时，超出的部分必须裁掉，不然会盖到侧边栏上面去。
/// </summary>
public sealed class BackgroundHost : Grid
{
    private readonly Image _image = new();
    private readonly Border _dim = new();

    // 最近一次应用的配置（改窗口大小时要重算铺法）。
    private AppSettings? _settings;

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

        // ⚠️ 页面色必须用 SetResourceReference 绑定资源键，不能缓存 FindResource / 索引器取到的刷子对象，否则换主题不会刷新。
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
            var alpha = (byte)Math.Clamp(dim, 0, 255);

            // ⚠️ 蒙层颜色必须跟主题走：用页面色，深色主题等于压暗，浅色主题等于提亮。
            // ⚠️ 浅色主题 alpha 要按 0.5 折算，白蒙层按原值会把画面推得过亮。
            var dark = ThemeApplier.CurrentMode == PaletteMode.Dark;
            if (!dark)
            {
                alpha = (byte)Math.Clamp(Math.Round(alpha * LightVeilScale), 0, 255);
            }

            // ⚠️ 颜色是**运行时取的**，所以换主题时必须重跑本方法 ——
            // 调用方见 PersonalizeSection.OnThemeSelected。
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
