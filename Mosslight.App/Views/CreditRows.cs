using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mosslight.Core.Localization;

namespace Mosslight.App.Views;

/// <summary>
/// 「关于与鸣谢」那种一行的渲染：头像 + 名字 / 说明 + 右侧按钮。
///
/// 抽出来是因为**两张名单（关于 / 特别鸣谢）和帮助链接那一块**用的是同一个形状 ——
/// 三份手写的 Grid 迟早会走形（行高、按钮宽度、兜底头像各改各的）。
/// </summary>
internal static class CreditRows
{
    /// <summary>行高 / 头像直径（PCL 那边头像 32、行距一档；这里略大方一点）。</summary>
    private const double AvatarSize = 36;

    /// <summary>右侧按钮的最小宽度（PCL 那排按钮是齐边的一列，宽度不够会参差不齐）。</summary>
    private const double ButtonWidth = 132;

    /// <param name="host">拿来解析主题资源（刷子 / 按钮样式）的宿主控件。</param>
    /// <param name="onAction">点了按钮（参数是这一条数据）；null = 这条没有按钮。</param>
    public static FrameworkElement Build(
        FrameworkElement host, CreditEntry entry, Action<CreditEntry, Button>? onAction)
    {
        var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatar = BuildAvatar(host, entry);
        Grid.SetColumn(avatar, 0);
        row.Children.Add(avatar);

        var textBox = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textBox.Children.Add(new TextBlock
        {
            Text = entry.Name,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)host.FindResource("TextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        textBox.Children.Add(new TextBlock
        {
            Text = DescriptionOf(entry),
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = 12,
            Foreground = (Brush)host.FindResource("TextFaintBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(textBox, 1);
        row.Children.Add(textBox);

        if (entry.ButtonLabel is { Length: > 0 } label && onAction is not null)
        {
            var button = new Button
            {
                Content = Localizer.Tr(label),
                Style = (Style)host.FindResource("GhostButton"),
                MinWidth = ButtonWidth,
                Height = 30,
                Margin = new Thickness(12, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            button.Click += (_, _) => onAction(entry, button);
            Grid.SetColumn(button, 2);
            row.Children.Add(button);
        }

        return row;
    }

    /// <summary>说明文案：过 Localizer，再把 <c>{version}</c> 换成当前版本号。</summary>
    public static string DescriptionOf(CreditEntry entry)
        => Localizer.Tr(entry.Description)
            .Replace("{version}", Core.AppInfo.AppVersion, StringComparison.Ordinal);

    /// <summary>
    /// 头像：`Assets\credits\&lt;file&gt;`。
    ///
    /// ⚠️ 图没放 / 图坏了**不报错**：画一个圆底 + 名字首字母。
    ///    名单是先搭结构的（图后补），缺图不该让这一页开不出来。
    /// </summary>
    private static FrameworkElement BuildAvatar(FrameworkElement host, CreditEntry entry)
    {
        var frame = new Border
        {
            Width = AvatarSize,
            Height = AvatarSize,
            CornerRadius = new CornerRadius(AvatarSize / 2),
            Background = (Brush)host.FindResource("AccentBgBrush"),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (TryLoadAvatar(entry.Avatar) is { } image)
        {
            frame.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            return frame;
        }

        frame.Child = new TextBlock
        {
            Text = FirstLetter(entry.Name),
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)host.FindResource("AccentTextStrongBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return frame;
    }

    private static BitmapImage? TryLoadAvatar(string file)
    {
        if (file.Length == 0)
        {
            return null;
        }
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            // OnLoad：整张读进内存再 Freeze，别留一个跟着资源流懒加载的对象
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri($"pack://application:,,,/Assets/credits/{file}", UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or NotSupportedException
                                      or ArgumentException or InvalidOperationException)
        {
            // 名单先搭结构、图后补：没有这个资源就当"没图"，走首字母
            return null;
        }
    }

    /// <summary>头像兜底用的首字母（中文名就是第一个字）。</summary>
    private static string FirstLetter(string name)
    {
        var text = (name ?? string.Empty).Trim();
        return text.Length == 0 ? "?" : text[..1].ToUpperInvariant();
    }
}
