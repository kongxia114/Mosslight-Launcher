using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mosslight.Core.Localization;

/// <summary>
/// 多语言。**直接复用 1.0.0 的 `assets/lang/*.json`**（698 条 × 5 种语言），
/// 所以两个版本的语言文件是同一份，不会各翻各的。
///
/// ## 为什么 key 是中文源文案
/// 沿用 1.0.0 的取舍：代码里直接看得见中文，review 方便；漏翻 / 打错 key
/// 只会退回中文显示，永远不会把 `home.launch` 或者 `#lang_1#` 糊到用户脸上。
///
/// 代价：以后改中文措辞时，那一条的译文会失效（退回中文）。这个代价是**故意接受**的。
/// </summary>
public static class Localizer
{
    public const string FallbackLanguage = "zh_CN";

    /// <summary>语言代码（文件夹里的文件名）→ 显示名。按**该语言自己**写，所以不用翻。</summary>
    public static readonly IReadOnlyDictionary<string, string> LanguageNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["zh_CN"] = "简体中文",
            ["zh_TW"] = "繁體中文",
            ["en_US"] = "English",
            ["ja_JP"] = "日本語",
            ["ru_RU"] = "Русский",
        };

    private static Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private static string _languageDir = string.Empty;

    public static string CurrentLanguage { get; private set; } = FallbackLanguage;

    /// <summary>语言文件放在哪（默认 `&lt;exe&gt;\assets\lang`）。</summary>
    public static void Init(string languageDir)
    {
        _languageDir = languageDir;
    }

    /// <summary>目录下实际存在的语言，按 <see cref="LanguageNames"/> 的顺序排（未知的排后面）。</summary>
    public static IReadOnlyList<string> AvailableLanguages()
    {
        var found = new HashSet<string>(StringComparer.Ordinal) { FallbackLanguage };
        try
        {
            if (Directory.Exists(_languageDir))
            {
                foreach (var path in Directory.EnumerateFiles(_languageDir, "*.json"))
                {
                    found.Add(Path.GetFileNameWithoutExtension(path));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到目录就当只有兜底语言，不该因此起不来
        }

        var known = LanguageNames.Keys.Where(found.Contains).ToList();
        known.AddRange(found.Except(LanguageNames.Keys).OrderBy(x => x, StringComparer.Ordinal));
        return known;
    }

    public static string LanguageName(string code)
        => LanguageNames.TryGetValue(code, out var name) ? name : code;

    /// <summary>切换语言。⚠️ 已经建好的控件**不会**自己变 —— 界面得自己重设文字。</summary>
    public static void SetLanguage(string language)
    {
        var available = AvailableLanguages();
        var target = available.Contains(language) ? language : FallbackLanguage;
        CurrentLanguage = target;
        _strings = LoadLanguage(target);
    }

    private static Dictionary<string, string> LoadLanguage(string language)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(_languageDir)) return result;
        var path = Path.Combine(_languageDir, language + ".json");
        try
        {
            // 读字节而不是文本：json 解析器认得 BOM 和 UTF-16/32，
            // 而按 UTF-8 读一个"UTF-8 with BOM"的文件会在第一个字符就炸
            // （记事本存出来的就是这种）。
            var bytes = File.ReadAllBytes(path);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(bytes);
            if (parsed is null) return result;
            foreach (var (key, value) in parsed)
            {
                result[key] = value;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 语言文件读不到 / 格式坏了 → 全部退回中文，不影响启动
            System.Diagnostics.Debug.WriteLine($"[i18n] 语言文件读不到或格式错误: {path} ({ex.Message})");
        }
        return result;
    }

    /// <summary>
    /// 取当前语言的文案。<paramref name="source"/> 是**中文源文案**。
    ///
    /// 带变量的文案走参数，别自己拼字符串：
    /// <c>Tr("已选中 {name}", ("name", name))</c>
    /// </summary>
    public static string Tr(string source)
        => _strings.TryGetValue(source, out var text) ? text : source;

    /// <summary>带占位符的版本；译文占位符对不上时退回源文案（不抛异常）。</summary>
    public static string Tr(string source, params (string Key, object? Value)[] args)
    {
        if (args.Length == 0) return Tr(source);
        var template = Tr(source);
        var rendered = Fill(template, args);
        return rendered ?? Fill(source, args) ?? source;
    }

    /// <summary>把 `{key}` 换成值；只要有一个 key 找不到就返回 null（让调用方退回源文案）。</summary>
    private static string? Fill(string template, (string Key, object? Value)[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in args)
        {
            values[key] = value switch
            {
                null => string.Empty,
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty,
            };
        }

        var sb = new StringBuilder(template.Length + 16);
        for (var i = 0; i < template.Length; i++)
        {
            var ch = template[i];
            if (ch != '{')
            {
                sb.Append(ch);
                continue;
            }
            var end = template.IndexOf('}', i + 1);
            if (end < 0)
            {
                sb.Append(ch);
                continue;
            }
            var key = template.Substring(i + 1, end - i - 1);
            if (!values.TryGetValue(key, out var replacement)) return null;
            sb.Append(replacement);
            i = end;
        }
        return sb.ToString();
    }

    // ---------- 系统语言 ----------

    /// <summary>
    /// 各种写法的语言标记 → 我们支持的那几种。
    /// 输入可能是 `zh-CN`（BCP-47）、`zh_CN`（POSIX）、
    /// `Chinese (Simplified)_China`（Windows 的老写法）。
    /// </summary>
    public static string NormalizeLocale(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return string.Empty;
        var raw = tag.Replace('-', '_');
        var parts = raw.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return string.Empty;
        var lang = parts[0].ToLowerInvariant();
        var region = parts.Length > 1 ? parts[1].ToUpperInvariant() : string.Empty;

        if (lang == "zh" || lang.Contains("chinese"))
        {
            // 中文必须分简繁，不然台湾用户会看到简体
            var upper = raw.ToUpperInvariant();
            if (region is "TW" or "HK" or "MO" || upper.Contains("HANT") || tag.Contains("Traditional"))
            {
                return "zh_TW";
            }
            return "zh_CN";
        }

        foreach (var (prefix, code) in new[]
                 {
                     ("en", "en_US"), ("ja", "ja_JP"), ("ru", "ru_RU"),
                     ("japanese", "ja_JP"), ("russian", "ru_RU"), ("english", "en_US"),
                 })
        {
            if (lang.StartsWith(prefix, StringComparison.Ordinal)) return code;
        }

        // 不支持的语言退回英文 —— 总比让外国用户看到中文强
        return "en_US";
    }

    /// <summary>系统的界面语言（Windows 上走 <c>GetUserDefaultLocaleName</c>，跟 1.0.0 一样）。</summary>
    public static string SystemLocale()
    {
        foreach (var tag in new[]
                 {
                     CultureInfo.CurrentUICulture.Name,
                     CultureInfo.CurrentCulture.Name,
                     CultureInfo.CurrentUICulture.EnglishName,
                 })
        {
            var got = NormalizeLocale(tag);
            if (!string.IsNullOrEmpty(got)) return got;
        }
        return "en_US";
    }

    /// <summary>
    /// 启动时决定用哪个语言：配置里存过就用存的，没存过按系统语言定（并写回配置，
    /// 这样设置页显示的就是实际在用的、用户也改得掉）。
    /// </summary>
    public static string ResolveLanguage(Settings.AppSettings settings)
    {
        var saved = settings.Language;
        if (!string.IsNullOrEmpty(saved)) return saved;
        var detected = SystemLocale();
        settings.Language = detected;      // 失败也不影响这次启动
        return detected;
    }
}
