using System.IO;
using System.Windows;
using System.Windows.Media;
using Mosslight.Core.Settings;
using Mosslight.Core.Theme;

namespace Mosslight.App.Infrastructure;

/// <summary>
/// 把一套调色板刷进 WPF 的资源里；⚠️ 所有刷子都要 `Freeze()`：冻结之后 WPF 可以跨线程共享、也不用
/// 为每次使用做变更通知，是实打实的性能差别（列表里几百个元素时看得出来）。
/// </summary>
public static class ThemeApplier
{
    /// <summary>当前实际生效的主题（`system` 已经解开了）。</summary>
    public static PaletteMode CurrentMode { get; private set; } = PaletteMode.Dark;

    /// <summary>`bg_card` → `BgCard`（跟生成器的规则一字不差）。</summary>
    public static string KeyOf(string token)
    {
        var parts = token.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p =>
            char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
    }

    /// <summary>
    /// 按配置重新算一整套颜色并刷进资源（换主题、改强调色、改卡片透明度都走它）。
    /// </summary>
    public static void Apply(AppSettings settings)
    {
        var systemIsDark = SystemTheme.IsDark();
        CurrentMode = ThemeService.EffectiveMode(settings.Theme, systemIsDark);
        var palette = ThemeService.Palette(CurrentMode, settings.AccentColor);
        ApplyPalette(palette);
        ApplyCardOpacity(settings.CardOpacity);
        ApplyChrome(settings);
    }

    /// <summary>把调色板写进资源字典。</summary>
    public static void ApplyPalette(IReadOnlyDictionary<string, string> palette)
    {
        var resources = Application.Current.Resources;
        foreach (var (token, hex) in palette)
        {
            var key = KeyOf(token);
            var color = ParseColor(hex);
            resources[key] = color;
            resources[key + "Brush"] = FrozenBrush(color);
        }
    }

    /// <summary>卡片那一族底色（带「卡片透明度」）。</summary>
    public static void ApplyCardOpacity(int opacityPercent)
    {
        var clamped = Math.Clamp(opacityPercent, Core.Appearance.BackgroundModes.MinCardOpacity,
            Core.Appearance.BackgroundModes.MaxCardOpacity);
        var alpha = (byte)Math.Round(clamped * 255.0 / 100.0, MidpointRounding.ToEven);
        var resources = Application.Current.Resources;
        resources["CardSurfaceBrush"] = FrozenBrush(WithAlpha(ParseColor(HexOf("BgCard")), alpha));
        resources["CardBorderBrush"] = FrozenBrush(WithAlpha(ParseColor(HexOf("Border")), alpha));
        resources["CardHoverBrush"] = FrozenBrush(WithAlpha(ParseColor(HexOf("BgElevated")), alpha));
    }

    /// <summary>「外壳」的半透明：只给侧边栏；⚠️ **只在设了背景图时**才半透明。</summary>
    public static void ApplyChrome(AppSettings settings)
    {
        const double chromeAlpha = 0.82;
        var hasImage = File.Exists(settings.BackgroundImage);
        var alpha = hasImage ? (byte)Math.Round(chromeAlpha * 255, MidpointRounding.AwayFromZero) : (byte)255;
        Application.Current.Resources["SidebarBrush"] =
            FrozenBrush(WithAlpha(ParseColor(HexOf("BgSidebar")), alpha));
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>资源里某个 Color 键当前的十六进制值（`#rrggbb`）。</summary>
    public static string HexOf(string key)
    {
        if (Application.Current.Resources[key] is Color color)
        {
            return $"#{color.R:x2}{color.G:x2}{color.B:x2}";
        }
        return "#000000";
    }

    /// <summary>资源里某个 Brush 键当前的纯色（拿不到就回退成透明）。</summary>
    public static Color ColorOfBrush(string key)
        => Application.Current.Resources[key] is SolidColorBrush brush ? brush.Color : Colors.Transparent;

    public static Color ParseColor(string hex)
    {
        var text = hex.TrimStart('#');
        if (text.Length == 8)
        {
            return Color.FromArgb(
                System.Convert.ToByte(text[..2], 16), System.Convert.ToByte(text.Substring(2, 2), 16),
                System.Convert.ToByte(text.Substring(4, 2), 16), System.Convert.ToByte(text.Substring(6, 2), 16));
        }
        return Color.FromRgb(
            System.Convert.ToByte(text[..2], 16), System.Convert.ToByte(text.Substring(2, 2), 16),
            System.Convert.ToByte(text.Substring(4, 2), 16));
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
