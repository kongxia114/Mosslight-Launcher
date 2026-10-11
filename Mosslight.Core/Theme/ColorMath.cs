using System.Globalization;

namespace Mosslight.Core.Theme;

/// <summary>
/// 颜色计算（逐字移植 1.0.0 的 core/theme.py 里那几个私有助手）。
///
/// ## 为什么要"逐字"
/// 主题色（用户能在个性化页里挑的强调色）要派生出 14 个变体：填充、悬停、按下、
/// 文字、淡底、边框……WPF 这边和 Python 那边**必须算出同样的值**，
/// 否则两个版本的界面会出现"同一个主题色、不同的观感"。
///
/// ⚠️ 两个容易翻车的点：
///   1. Python 的 `round()` 是**银行家舍入**（0.5→0、1.5→2），C# 的 `Math.Round`
///      默认也是 ToEven —— 但仍然显式写出来，免得哪天默认值变了。
///   2. HLS 的换算公式必须跟 `colorsys` 一字不差（它用的是
///      `l = (max+min)/2`、`s = diff/(2-max-min)` 或 `diff/(max+min)` 这套）。
/// </summary>
public static class ColorMath
{
    /// <summary>`#rrggbb` → (r, g, b)，每个分量 0~1（跟 Python 的 _to_rgb 一致）。</summary>
    public static (double R, double G, double B) ToRgb(string hexColor)
    {
        var h = hexColor.TrimStart('#');
        return (
            int.Parse(h.Substring(0, 2), NumberStyles.HexNumber) / 255.0,
            int.Parse(h.Substring(2, 2), NumberStyles.HexNumber) / 255.0,
            int.Parse(h.Substring(4, 2), NumberStyles.HexNumber) / 255.0);
    }

    /// <summary>(r, g, b) 0~1 → `#rrggbb`（跟 Python 的 _to_hex 一致：钳到 0~255、银行家舍入）。</summary>
    public static string ToHex(double r, double g, double b)
    {
        static string Chan(double c)
        {
            // 顺序跟 Python 一致：先四舍五入，再钳。反过来在边界上会差 1。
            var v = (int)Math.Clamp(Math.Round(c * 255, MidpointRounding.ToEven), 0, 255);
            return v.ToString("x2", CultureInfo.InvariantCulture);
        }
        return "#" + Chan(r) + Chan(g) + Chan(b);
    }

    /// <summary>是不是合法的 `#rrggbb`（对应 theme.py 里的 _HEX_RE）。</summary>
    public static bool IsHex(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length == 7 && value[0] == '#'
           && value.Skip(1).All(Uri.IsHexDigit);

    /// <summary>把 fg 按 ratio 混到 bg 上：0 = 全 bg，1 = 全 fg。</summary>
    public static string Blend(string fg, string bg, double ratio)
    {
        var f = ToRgb(fg);
        var b = ToRgb(bg);
        return ToHex(
            b.R + (f.R - b.R) * ratio,
            b.G + (f.G - b.G) * ratio,
            b.B + (f.B - b.B) * ratio);
    }

    /// <summary>按 HLS 调亮/调暗（lightness 正数变亮，负数变暗）。</summary>
    public static string Shift(string hexColor, double lightness = 0.0, double saturation = 0.0)
    {
        var (h, l, s) = RgbToHls(ToRgb(hexColor));
        l = Math.Clamp(l + lightness, 0.0, 1.0);
        s = Math.Clamp(s + saturation, 0.0, 1.0);
        var (r, g, b) = HlsToRgb(h, l, s);
        return ToHex(r, g, b);
    }

    /// <summary>
    /// 在这个颜色上面写字，用白还是用深色？
    /// 用感知亮度（ITU-R BT.601）判断，所以挑个亮黄当强调色也不会白字糊成一片。
    /// </summary>
    public static string ReadableOn(string color)
    {
        var (r, g, b) = ToRgb(color);
        return (0.299 * r + 0.587 * g + 0.114 * b) > 0.62 ? "#101317" : "#ffffff";
    }

    // ---------- HLS（跟 Python colorsys 的公式一字不差） ----------

    private static (double H, double L, double S) RgbToHls((double R, double G, double B) rgb)
    {
        var (r, g, b) = rgb;
        var maxC = Math.Max(r, Math.Max(g, b));
        var minC = Math.Min(r, Math.Min(g, b));
        var sum = maxC + minC;
        var l = sum / 2.0;
        if (Math.Abs(minC - maxC) < 1e-12)
        {
            return (0.0, l, 0.0);
        }
        var diff = maxC - minC;
        var s = l > 0.5 ? diff / (2.0 - maxC - minC) : diff / sum;

        double h;
        if (Math.Abs(maxC - r) < 1e-12)
        {
            h = (g - b) / diff + (g < b ? 6.0 : 0.0);
        }
        else if (Math.Abs(maxC - g) < 1e-12)
        {
            h = (b - r) / diff + 2.0;
        }
        else
        {
            h = (r - g) / diff + 4.0;
        }
        return (h / 6.0, l, s);
    }

    private static (double R, double G, double B) HlsToRgb(double h, double l, double s)
    {
        if (s == 0.0)
        {
            return (l, l, l);
        }
        var m2 = l <= 0.5 ? l * (1.0 + s) : l + s - l * s;
        var m1 = 2.0 * l - m2;

        static double V(double m1, double m2, double hue)
        {
            hue = hue < 0 ? hue + 1.0 : (hue > 1 ? hue - 1.0 : hue);
            if (hue * 6.0 < 1.0) return m1 + (m2 - m1) * hue * 6.0;
            if (hue * 2.0 < 1.0) return m2;
            if (hue * 3.0 < 2.0) return m1 + (m2 - m1) * (2.0 / 3.0 - hue) * 6.0;
            return m1;
        }

        return (V(m1, m2, h + 1.0 / 3.0), V(m1, m2, h), V(m1, m2, h - 1.0 / 3.0));
    }
}
