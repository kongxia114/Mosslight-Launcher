namespace Mosslight.Core.Theme;

/// <summary>配置里存的三种主题模式（跟 1.0.0 的 `core/theme.py` MODES 一一对应）。</summary>
public enum ThemeMode
{
    /// <summary>跟随系统</summary>
    System,

    Dark,

    Light,
}

/// <summary>落到实际的两种之一（"跟随系统"在这里被解掉）。</summary>
public enum PaletteMode
{
    Dark,

    Light,
}

/// <summary>
/// 主题解析：把「用户选了什么」变成「一套具体的颜色」。
///
/// 移植自 1.0.0 的 `core/theme.py` + `ui/theme_state.py`，但**没有 Qt/WPF 依赖** ——
/// "系统是不是深色"由调用方（界面层）用 `Application.Current` 读出来传进来，
/// 这样这个类可以纯粹地单测（Python 那边也是这么切的）。
///
/// ## 两件事
/// 1. 48 个基础 token 来自生成的 <see cref="ThemeData"/>（源头还是 theme.py）。
/// 2. 用户自定义了强调色的话，**只覆盖强调色那一族**（14 个键），其余不动。
///    自带的深绿不参与计算，用的是手挑的精确值 —— 算出来的和手挑的会差一点点。
/// </summary>
public static class ThemeService
{
    /// <summary>配置里那三种取值（存进 json 的就是这些字符串）。</summary>
    public static readonly IReadOnlyList<string> Modes = new[] { "system", "dark", "light" };

    /// <summary>
    /// 自定义强调色时要重算的那 14 个 token。
    /// ⚠️ 跟 `theme.py` 的 `_accent_variants()` 返回的键**必须一模一样**，
    /// 少一个就会留下"旧主题色的残留"。
    /// </summary>
    public static readonly IReadOnlyList<string> AccentTokens = new[]
    {
        "accent",
        "accent_fill",
        "accent_fill_hover",
        "accent_fill_pressed",
        "accent_text",
        "accent_text_strong",
        "accent_bg",
        "accent_border",
        "accent_bg_disabled",
        "accent_border_disabled",
        "accent_text_disabled",
        "accent_selected_bg",
        "avatar_bg",
        "on_accent",
    };

    private static readonly HashSet<string> AccentTokenSet = new(AccentTokens, StringComparer.Ordinal);

    /// <summary>把配置里的字符串认成枚举（认不出来 = 跟随系统）。</summary>
    public static ThemeMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "dark" => ThemeMode.Dark,
        "light" => ThemeMode.Light,
        _ => ThemeMode.System,
    };

    /// <summary>存回配置时用的字符串。</summary>
    public static string ModeName(ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => "dark",
        ThemeMode.Light => "light",
        _ => "system",
    };

    public static string PaletteName(PaletteMode mode) => mode == PaletteMode.Light ? "light" : "dark";

    /// <summary>
    /// 把「跟随系统」落到实际的 dark / light。
    /// `systemIsDark` 由界面层读系统设置后传进来（Core 里读不到）。
    /// </summary>
    public static PaletteMode EffectiveMode(ThemeMode mode, bool systemIsDark) => mode switch
    {
        ThemeMode.Light => PaletteMode.Light,
        ThemeMode.Dark => PaletteMode.Dark,
        _ => systemIsDark ? PaletteMode.Dark : PaletteMode.Light,
    };

    /// <summary>取一套调色板；`accent` 传空或格式不对就用主题自带的手挑值。</summary>
    public static IReadOnlyDictionary<string, string> Palette(PaletteMode mode, string? accent = null)
    {
        var basePalette = mode == PaletteMode.Light ? ThemeData.Light : ThemeData.Dark;
        var result = new Dictionary<string, string>(basePalette, StringComparer.Ordinal);
        if (ColorMath.IsHex(accent))
        {
            foreach (var (key, value) in AccentVariants(accent!, result, mode))
            {
                result[key] = value;
            }
        }
        return result;
    }

    /// <summary>
    /// 从一个强调色派生出一整族（填充 / 悬停 / 按下 / 文字 / 淡底 / 边框）。
    ///
    /// ⚠️ 这些系数是从 1.0.0 抄的，别"顺手调好看一点" —— 调了就跟 Python 版观感不一致了。
    /// </summary>
    public static IReadOnlyDictionary<string, string> AccentVariants(
        string accent, IReadOnlyDictionary<string, string> basePalette, PaletteMode mode)
    {
        var dark = mode != PaletteMode.Light;
        var page = basePalette["bg_page"];
        var fill = ColorMath.Shift(accent, dark ? -0.13 : -0.06);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["accent"] = accent,
            ["accent_fill"] = fill,
            ["accent_fill_hover"] = ColorMath.Shift(accent, dark ? -0.07 : 0.0),
            ["accent_fill_pressed"] = ColorMath.Shift(accent, dark ? -0.20 : -0.12),
            ["accent_text"] = ColorMath.Shift(accent, dark ? 0.16 : -0.14),
            ["accent_text_strong"] = ColorMath.Shift(accent, dark ? 0.30 : -0.22),
            ["accent_bg"] = ColorMath.Blend(accent, page, dark ? 0.13 : 0.16),
            ["accent_border"] = ColorMath.Blend(accent, page, dark ? 0.24 : 0.34),
            ["accent_bg_disabled"] = ColorMath.Blend(accent, page, 0.07),
            ["accent_border_disabled"] = ColorMath.Blend(accent, page, 0.12),
            ["accent_text_disabled"] = ColorMath.Blend(accent, page, 0.35),
            ["accent_selected_bg"] = ColorMath.Blend(accent, page, dark ? 0.18 : 0.24),
            ["avatar_bg"] = fill,
            ["on_accent"] = ColorMath.ReadableOn(fill),
        };
    }

    /// <summary>
    /// 这套调色板里，哪些键属于强调色一族（界面上"恢复默认"时要把它们全清掉）。
    /// </summary>
    public static bool IsAccentToken(string token) => AccentTokenSet.Contains(token);
}
