using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Mosslight.Core.Localization;

/// <summary>
/// 多语言：优先读 exe 旁 `Assets\lang`，缺失时回落到内嵌语言文件；⚠️ 两层优先级不能反。
/// </summary>
public static class Localizer
{
    public const string FallbackLanguage = "zh_CN";

    /// <summary>
    /// 内嵌资源的名字前缀；⚠️ 改了 `Mosslight.Core.csproj` 的 &lt;RootNamespace&gt;、或者把
    /// `Localization\lang\` 挪走，**必须同步改这一行**，否则运行时一个语言都读不到。
    /// </summary>
    private const string EmbeddedResourcePrefix = "Mosslight.Core.Localization.lang.";

    /// <summary>语言代码（文件夹里的文件名）→ 显示名。</summary>
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

    /// <summary>
    /// 程序集里**真的编进去**的语言，从内嵌资源名反推出来（`zh_CN` / `en_US` …）。
    /// </summary>
    public static IReadOnlyList<string> EmbeddedLanguages()
    {
        // 已知的顺序（LanguageNames 的键序）优先，未知的排后面 ——
        // 跟 AvailableLanguages 一个规矩，下拉框里看着才一致
        var found = new HashSet<string>(EmbeddedLanguageSet(), StringComparer.Ordinal);
        var known = LanguageNames.Keys.Where(found.Contains).ToList();
        known.AddRange(found.Except(LanguageNames.Keys).OrderBy(x => x, StringComparer.Ordinal));
        return known;
    }

    /// <summary>某个语言当前是从哪一层读到的 —— 只给诊断 / 自检用。</summary>
    public enum LanguageSource
    {
        /// <summary>没读到（连内嵌那份都没有，或者 Init 还没调过就查了）。</summary>
        None,

        /// <summary>
        /// exe 旁边的 `Assets\lang\&lt;代码&gt;.json`（用户覆盖 / 自己加的语言）。
        /// </summary>
        Disk,

        /// <summary>编译进程序集的那份。</summary>
        Embedded,
    }

    private static HashSet<string> EmbeddedLanguageSet()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var asm = typeof(Localizer).Assembly;
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(EmbeddedResourcePrefix, StringComparison.Ordinal)) continue;
            if (!name.EndsWith(".json", StringComparison.Ordinal)) continue;
            var code = name[EmbeddedResourcePrefix.Length..^".json".Length];
            // 只认直接的 `xx.json`，不认 `xx.yy.json` 这种带点的（目录层级串味了）
            if (code.Length > 0 && !code.Contains('.')) result.Add(code);
        }
        return result;
    }

    public static string CurrentLanguage { get; private set; } = FallbackLanguage;

    /// <summary>
    /// 语言文件放在哪（约定 `&lt;exe&gt;\Assets\lang`）；⚠️ 这个目录是**可选**的：发布产物里根本
    /// 没有 `Assets\`，此时所有语言都走内嵌那份。
    /// </summary>
    public static void Init(string languageDir)
    {
        _languageDir = languageDir;
    }

    public static IReadOnlyList<string> AvailableLanguages()
    {
        var found = new HashSet<string>(EmbeddedLanguageSet(), StringComparer.Ordinal) { FallbackLanguage };
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
            // 读不到目录就当只有内嵌那几种，不该因此起不来
        }

        var known = LanguageNames.Keys.Where(found.Contains).ToList();
        known.AddRange(found.Except(LanguageNames.Keys).OrderBy(x => x, StringComparer.Ordinal));
        return known;
    }

    public static string LanguageName(string code)
        => LanguageNames.TryGetValue(code, out var name) ? name : code;

    public static void SetLanguage(string language)
    {
        var available = AvailableLanguages();
        var target = available.Contains(language) ? language : FallbackLanguage;
        CurrentLanguage = target;
        _strings = LoadLanguage(target);
    }

    /// <summary>
    /// 跟 SetLanguage 一样，但顺带告诉调用方"这一份是从哪读来的" （磁盘覆盖 vs 内嵌兜底）。
    /// </summary>
    public static LanguageSource SetLanguageWithSource(string language)
    {
        var available = AvailableLanguages();
        var target = available.Contains(language) ? language : FallbackLanguage;
        CurrentLanguage = target;
        var (strings, source) = LoadLanguageWithSource(target);
        _strings = strings;
        return source;
    }

    private static Dictionary<string, string> LoadLanguage(string language)
        => LoadLanguageWithSource(language).Strings;

    /// <summary>**两层**加载：① exe 旁边的 json → ② 内嵌资源。</summary>
    private static (Dictionary<string, string> Strings, LanguageSource Source) LoadLanguageWithSource(
        string language)
    {
        var fromDisk = TryLoadFromDisk(language);
        if (fromDisk is { Count: > 0 }) return (fromDisk, LanguageSource.Disk);

        var fromEmbedded = TryLoadFromEmbedded(language);
        if (fromEmbedded is { Count: > 0 }) return (fromEmbedded, LanguageSource.Embedded);

        // 两层都没有（比如查一个根本不存在的语言）：空字典 = 全部退回中文源文案
        return (new Dictionary<string, string>(StringComparer.Ordinal), LanguageSource.None);
    }

    private static Dictionary<string, string>? TryLoadFromDisk(string language)
    {
        if (string.IsNullOrEmpty(_languageDir)) return null;
        var path = Path.Combine(_languageDir, language + ".json");
        try
        {
            if (!File.Exists(path)) return null;
            // 读字节而不是文本：json 解析器认得 BOM 和 UTF-16/32，
            // 而按 UTF-8 读一个"UTF-8 with BOM"的文件会在第一个字符就炸
            // （记事本存出来的就是这种）。
            var bytes = File.ReadAllBytes(path);
            return Parse(JsonSerializer.Deserialize<Dictionary<string, string>>(bytes), path);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 语言文件读不到 / 格式坏了 → **回落到内嵌那份**（不是整个退回中文）
            System.Diagnostics.Debug.WriteLine($"[i18n] 磁盘覆盖读不到或格式错误，改用内嵌: {path} ({ex.Message})");
            return null;
        }
    }

    private static Dictionary<string, string>? TryLoadFromEmbedded(string language)
    {
        var asm = typeof(Localizer).Assembly;
        var name = EmbeddedResourcePrefix + language + ".json";
        try
        {
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) return null;
            return Parse(JsonSerializer.Deserialize<Dictionary<string, string>>(stream), name);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"[i18n] 内嵌语言资源读不到或格式错误: {name} ({ex.Message})");
            return null;
        }
    }

    private static Dictionary<string, string>? Parse(Dictionary<string, string>? parsed, string what)
    {
        if (parsed is null)
        {
            System.Diagnostics.Debug.WriteLine($"[i18n] 语言数据是 null（不是个对象？）: {what}");
            return null;
        }
        // 键比较用 Ordinal：这些 key 是中文源文案，不能跟着当前区域设置变
        return new Dictionary<string, string>(parsed, StringComparer.Ordinal);
    }

    /// <summary>某个语言里**有没有**这条文案（自检专用）。</summary>
    public static bool HasTranslation(string language, string source)
    {
        if (string.IsNullOrEmpty(language) || string.IsNullOrEmpty(source))
        {
            return false;
        }
        try
        {
            return LoadLanguage(language).ContainsKey(source);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                      or InvalidOperationException or FormatException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>取当前语言的文案。</summary>
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

    /// <summary>
    /// 把 `{key}` 换成值；只要有一个 key 找不到就返回 null（让调用方退回源文案）。
    /// </summary>
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

    // 系统语言

    /// <summary>各种写法的语言标记 → 我们支持的那几种。</summary>
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

    /// <summary>系统的界面语言。</summary>
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
    /// 启动时决定用哪个语言：配置里存过就用存的，没存过按系统语言定（并写回配置，这样设置页显示的
    /// 就是实际在用的、用户也改得掉）。
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
