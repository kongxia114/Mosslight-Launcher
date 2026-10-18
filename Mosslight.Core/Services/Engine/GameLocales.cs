using System.Text;

namespace Mosslight.Core.Services.Engine;

// ============================================================================
//  「首次启动时写进游戏的默认语言」——设置 → 其他 → 游戏语言
//
//  为什么要单独一个设置（2026-10 用户要求）：
//  启动器自己的界面语言（设置 → 个性化 → 语言）是给**启动器**看的；
//  进游戏之后游戏说哪种语言是 `options.txt` 里那行 `lang:` 决定的。
//  两个是**两件独立的事** —— 有人界面用英文、游戏想用中文，反之也行。
//
//  行为约定（都是刻意选的）：
//    · 只在这个版本**还没有 `options.txt`** 时写一次（= 第一次启动）；
//      已经有就一个字都不动 —— 用户在游戏里改过语言，不能被启动器覆盖；
//    · 写进去的文件只有一行 `lang:<代码>`，其余交给游戏自己补默认值；
//    · 选「自动」= 跟启动器的界面语言走（zh_CN → zh_cn 这种直接对应）。
//
//  ⚠️ `zh_hk`（繁體中文（香港））是真实存在的语言文件
//     （实测 26.3 的 assets 索引里 `minecraft/lang/zh_hk.json` 有），别当成笔误删掉。
// ============================================================================

/// <summary>游戏语言的选项表 + 首次启动时写 `options.txt` 的种子逻辑（纯函数 + 一次文件写）。</summary>
public static class GameLocales
{
    /// <summary>「自动」= 跟随启动器语言。</summary>
    public const string Auto = "auto";

    /// <summary>
    /// 下拉框里的选项：配置里存的值 / `options.txt` 里 `lang:` 的值 / 显示名。
    ///
    /// ⚠️ 除「自动」外显示名用**该语言自己**的写法（跟启动器的语言下拉一样），
    ///    不进语言文件 —— 语言名字翻成别的语言反而认不出来。
    /// </summary>
    public static readonly (string Key, string Locale, string Native)[] Choices =
    {
        (Auto, string.Empty, string.Empty),
        ("zh_cn", "zh_cn", "简体中文"),
        ("zh_tw", "zh_tw", "繁體中文"),
        ("zh_hk", "zh_hk", "繁體中文（香港）"),
        ("en_us", "en_us", "English"),
        ("ja_jp", "ja_jp", "日本語"),
        ("ru_ru", "ru_ru", "Русский"),
    };

    /// <summary>启动器界面语言 → 游戏的 `lang:` 值（选择「自动」时用）。</summary>
    public static string FromLauncher(string? launcherLanguage)
        => (launcherLanguage ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "zh_cn" => "zh_cn",
            "zh_tw" => "zh_tw",
            "en_us" => "en_us",
            "ja_jp" => "ja_jp",
            "ru_ru" => "ru_ru",
            _ => "en_us",
        };

    /// <summary>
    /// 设置里存的值 → `options.txt` 的 `lang:` 值。
    /// 空 / `auto` / 认不出来的值都退回「跟随启动器语言」；返回空串 = 不写。
    /// </summary>
    public static string Resolve(string? setting, string launcherLanguage)
    {
        var key = (setting ?? string.Empty).Trim().ToLowerInvariant();
        if (key.Length == 0 || key == Auto)
        {
            return FromLauncher(launcherLanguage);
        }
        foreach (var (candidate, locale, _) in Choices)
        {
            if (candidate == key)
            {
                return locale;
            }
        }
        return FromLauncher(launcherLanguage);
    }

    /// <summary>`zh_hk` → `繁體中文（香港）`（提示文字里显示用）；认不出来就原样返回。</summary>
    public static string NativeName(string? locale)
    {
        var key = (locale ?? string.Empty).Trim().ToLowerInvariant();
        foreach (var (_, code, native) in Choices)
        {
            if (code.Length > 0 && code == key)
            {
                return native;
            }
        }
        return (locale ?? string.Empty).Trim();
    }

    /// <summary>
    /// 首次启动的种子：<paramref name="gameDir"/> 里**没有** `options.txt` 时，
    /// 写一份只含 `lang:` 的进去。已经存在就一个字都不动。
    ///
    /// 返回 true = 这次真写了（调用方拿它决定要不要报一句进度）。
    /// 写失败（权限、磁盘满）也返回 false，且**不该拦启动** —— 这只是个默认值。
    /// </summary>
    public static bool SeedOptions(string? gameDir, string? locale)
    {
        var code = (locale ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(gameDir) || code.Length == 0)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(gameDir);
            var path = Path.Combine(gameDir, "options.txt");
            if (File.Exists(path))
            {
                return false;
            }
            File.WriteAllText(path, "lang:" + code + "\n", new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
