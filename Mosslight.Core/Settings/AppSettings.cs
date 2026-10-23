using System.Text.Json;
using System.Text.Json.Nodes;
using Mosslight.Core.Services.Engine;
using Mosslight.Core.Theme;

namespace Mosslight.Core.Settings;

/// <summary>
/// 启动器配置（`config.json`）；⚠️ 只纠正**认识的**键，不认识的键原样保留（用户自己塞的东西不能弄
/// 丢）。
/// </summary>
public sealed class AppSettings
{
    public const int ConfigVersion = 1;
    public const string DataDirName = "Mosslight";

    public const int MinThreadRange = 1;
    public const int MaxThreadRange = 32;
    public const int DefaultThreads = 8;
    public const int DefaultMaxMemory = 2048;
    public const int DefaultMinMemory = 512;

    private static readonly string[] ThemeModesValue = { "system", "dark", "light" };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // 中文不转义 —— 配置里存着路径和版本号，转义了人就没法看了
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly JsonObject _data = new();

    /// <summary>配置文件路径（`&lt;启动器目录&gt;\Mosslight\config.json`）。</summary>
    public string FilePath { get; private set; } = string.Empty;


    // 建 / 读 / 写

    /// <summary>`%APPDATA%\Mosslight`：程序目录写不了的时候退到这儿。</summary>
    public static string AppDataDir
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            DataDirName);

    /// <summary>
    /// 配置目录：优先 `&lt;启动器目录&gt;\Mosslight`（跟程序放一起，整个文件夹拷走就是完整迁移）
    /// 那儿写不了（装在 `C:\Program Files`、U 盘写保护）就退到 `%APPDATA%\Mosslight`。
    /// </summary>
    public static string ResolveConfigDir(string baseDir)
        => PickConfigDir(baseDir, IsWritable, AppDataDir);

    /// <summary>纯函数版（自检用）：`canWrite` 说能写就用程序目录，否则用 `fallbackDir`。</summary>
    public static string PickConfigDir(string baseDir, Func<string, bool> canWrite, string fallbackDir)
    {
        var portable = Path.Combine(baseDir, DataDirName);
        try
        {
            if (canWrite(portable))
            {
                return portable;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 判定本身炸了也当"写不了"
        }
        return fallbackDir;
    }

    /// <summary>往这个目录能不能写：建目录 + 写一个探针文件再删掉。</summary>
    public static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static AppSettings Load(string baseDir)
    {
        var settings = new AppSettings();
        var portable = Path.Combine(baseDir, DataDirName);
        settings.UsingFallbackDir = !IsWritable(portable);
        var dir = settings.UsingFallbackDir ? AppDataDir : portable;
        settings.FilePath = Path.Combine(dir, "config.json");

        if (File.Exists(settings.FilePath))
        {
            try
            {
                var parsed = JsonNode.Parse(File.ReadAllText(settings.FilePath));
                if (parsed is JsonObject obj)
                {
                    foreach (var (key, value) in obj)
                    {
                        settings._data[key] = value?.DeepClone();
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // 配置坏了不该让启动器起不来：当作没有配置，用默认值继续
                settings.LastError = ex.Message;
            }
        }
        settings._data["config_version"] = ConfigVersion;
        return settings;
    }

    /// <summary>上次读 / 写配置文件出的错（界面可以提示一下，但不用因此罢工）。</summary>
    public string? LastError { get; private set; }

    /// <summary>配置退到了 `%APPDATA%`（程序目录写不了）。</summary>
    public bool UsingFallbackDir { get; private set; }

    public bool Save()
    {
        if (TryWriteTo(FilePath))
        {
            return true;
        }

        var fallback = Path.Combine(AppDataDir, "config.json");
        if (string.Equals(fallback, FilePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!TryWriteTo(fallback))
        {
            return false;
        }
        FilePath = fallback;
        UsingFallbackDir = true;
        return true;
    }

    private bool TryWriteTo(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, _data.ToJsonString(WriteOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            LastError = ex.Message;
            return false;
        }
    }

    // 类型纠正（公开，别处也要用）

    public static int AsInt(JsonNode? value, int fallback)
    {
        if (value is null) return fallback;
        try
        {
            return value.GetValueKind() switch
            {
                JsonValueKind.Number => value.GetValue<int>(),
                JsonValueKind.String => int.TryParse(
                    value.GetValue<string>().Trim(), out var parsed) ? parsed : fallback,
                // true → 1，跟 Python 的 int(True) 一致
                JsonValueKind.True => 1,
                JsonValueKind.False => 0,
                _ => fallback,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// ⚠️ 别偷懒写成 `GetValue&lt;bool&gt;()`：`"false"` 这个字符串在 Python 和 C# 里都容易变成
    /// `true`，用户手改一下就把开关反了。
    /// </summary>
    public static bool AsBool(JsonNode? value, bool fallback)
    {
        if (value is null) return fallback;
        switch (value.GetValueKind())
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number: return value.GetValue<double>() != 0;
            case JsonValueKind.String:
                return value.GetValue<string>().Trim().ToLowerInvariant() switch
                {
                    "true" or "yes" or "on" or "1" => true,
                    "false" or "no" or "off" or "0" => false,
                    _ => fallback,
                };
            default:
                return fallback;
        }
    }

    public static string AsStr(JsonNode? value, string fallback)
    {
        if (value is null) return fallback;
        return value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : value.ToJsonString().Trim('"');
    }

    /// <summary>只接受 `#rrggbb`；拼错、写成颜色名一律退回默认。</summary>
    public static string AsColor(JsonNode? value, string fallback)
    {
        if (value is null || value.GetValueKind() != JsonValueKind.String) return fallback;
        var text = value.GetValue<string>().Trim();
        // 注意：这个类里有个属性也叫 Theme，所以不能写 `Theme.ColorMath`（会被当成属性）
        return ColorMath.IsHex(text) ? text.ToLowerInvariant() : fallback;    }

    // 读写单个键

    public JsonNode? Raw(string key) => _data.TryGetPropertyValue(key, out var node) ? node : null;

    public string? GetString(string key) => _data.TryGetPropertyValue(key, out var n) && n is not null
        ? AsStr(n, string.Empty) : null;

    public int GetInt(string key, int fallback = 0) => AsInt(Raw(key), fallback);

    public bool GetBool(string key, bool fallback = false) => AsBool(Raw(key), fallback);

    public bool Set(string key, string value) => SetNode(key, JsonValue.Create(value));

    public bool Set(string key, int value) => SetNode(key, JsonValue.Create(value));

    public bool Set(string key, bool value) => SetNode(key, JsonValue.Create(value));

    private bool SetNode(string key, JsonNode? node)
    {
        _data[key] = node;
        return true;
    }

    // 界面直接用的那几个（都做过纠正）

    /// <summary>主题：`system` / `dark` / `light`（认不出来回 `system`）。</summary>
    public ThemeMode Theme
    {
        get
        {
            var text = GetString("theme");
            return text is not null && ThemeModesValue.Contains(text)
                ? ThemeService.ParseMode(text)
                : ThemeMode.System;
        }
        set
        {
            Set("theme", ThemeService.ModeName(value));
            Save();
        }
    }

    /// <summary>自定义强调色；空 = 用主题自带那套手挑值。</summary>
    public string AccentColor
    {
        get => AsColor(Raw("accent_color"), string.Empty);
        set
        {
            Set("accent_color", value ?? string.Empty);
            Save();
        }
    }

    /// <summary>界面语言；空 = 首次启动按系统语言定。</summary>
    public string Language
    {
        get => GetString("language") ?? string.Empty;
        set { Set("language", value ?? string.Empty); Save(); }
    }

    public string MinecraftDir
    {
        get => GetString("minecraft_dir") ?? string.Empty;
        set { Set("minecraft_dir", value ?? string.Empty); Save(); }
    }

    /// <summary>上次启动的版本 id（首页默认选中它）。</summary>
    public string LastVersion
    {
        get => GetString("last_version") ?? string.Empty;
        set { Set("last_version", value ?? string.Empty); Save(); }
    }

    /// <summary>
    /// "用过的游戏目录"（`known_minecraft_dirs`），**最近的排最前面**；⚠️ **只增不改**：新加的成
    /// 员不动任何已有成员的签名或行为 （已有键照旧原样保留、原样写回）。
    /// </summary>
    public IReadOnlyList<string> KnownMinecraftDirs
    {
        get
        {
            if (Raw("known_minecraft_dirs") is not JsonArray array)
            {
                return Array.Empty<string>();
            }
            var result = new List<string>(array.Count);
            foreach (var node in array)
            {
                var text = node is null ? null : AsStr(node, string.Empty).Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    result.Add(text);
                }
            }
            return result;
        }
    }

    /// <summary>把一个目录记进"用过的列表"，最近的排最前面。</summary>
    public bool RememberMinecraftDir(string pathText)
    {
        var text = (pathText ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var known = new List<string> { text };
        foreach (var existing in KnownMinecraftDirs)
        {
            if (!string.Equals(existing, text, StringComparison.OrdinalIgnoreCase))
            {
                known.Add(existing);
            }
        }

        var array = new JsonArray();
        foreach (var item in known.Take(12))
        {
            array.Add(JsonValue.Create(item));
        }
        SetNode("known_minecraft_dirs", array);
        return Save();
    }

    public IReadOnlyList<string> CustomJavaPaths
    {
        get
        {
            if (Raw("java_paths") is not JsonArray array)
            {
                return Array.Empty<string>();
            }
            var result = new List<string>(array.Count);
            foreach (var node in array)
            {
                var text = node is null ? null : AsStr(node, string.Empty).Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    result.Add(text);
                }
            }
            return result;
        }
    }

    public bool AddCustomJavaPath(string pathText)
    {
        var text = (pathText ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var paths = new List<string> { text };
        foreach (var existing in CustomJavaPaths)
        {
            if (!string.Equals(existing, text, StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(existing);
            }
        }

        var array = new JsonArray();
        foreach (var item in paths.Take(24))
        {
            array.Add(JsonValue.Create(item));
        }
        SetNode("java_paths", array);
        return Save();
    }

    public bool RemoveCustomJavaPath(string pathText)
    {
        var text = (pathText ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var array = new JsonArray();
        var changed = false;
        foreach (var existing in CustomJavaPaths)
        {
            if (string.Equals(existing, text, StringComparison.OrdinalIgnoreCase))
            {
                changed = true;
                continue;
            }
            array.Add(JsonValue.Create(existing));
        }

        if (!changed)
        {
            return true;
        }
        SetNode("java_paths", array);
        return Save();
    }
    public int MaxMemory
    {
        get => GetInt("max_memory", DefaultMaxMemory);
        set { Set("max_memory", value); Save(); }
    }

    public int MinMemory
    {
        get => GetInt("min_memory", DefaultMinMemory);
        set { Set("min_memory", value); Save(); }
    }

    /// <summary>
    /// 内存是不是「自动配置」（照 PCL 的「内存分配 → 自动配置 / 自定义」两个单选）；⚠️ 默认
    /// **false**：老配置里存的是用户自己调过的 min/max，升级后不能因为多了这个开关就悄悄把他们的
    /// 值换成推荐值 —— 只有用户明确点「自动配置」才切。
    /// </summary>
    public bool MemoryAuto
    {
        get => GetBool("memory_auto", false);
        set { Set("memory_auto", value); Save(); }
    }


    public string BackgroundImage
    {
        get => GetString("bg_image") ?? string.Empty;
        set { Set("bg_image", value ?? string.Empty); Save(); }
    }

    public string BackgroundColor
    {
        get => AsColor(Raw("bg_color"), string.Empty);
        set { Set("bg_color", value ?? string.Empty); Save(); }
    }

    /// <summary>铺法：`fill` / `fit` / `stretch` / `center` / `span`。</summary>
    public string BackgroundMode
    {
        get
        {
            var text = GetString("bg_mode") ?? string.Empty;
            return Appearance.BackgroundModes.Contains(text) ? text : Appearance.BackgroundModes.Default;
        }
        set { Set("bg_mode", value); Save(); }
    }

    /// <summary>压暗程度 0~200。</summary>
    public int BackgroundDim
    {
        get => Math.Clamp(GetInt("bg_dim", Appearance.BackgroundModes.DefaultDim), 0, 200);
        set { Set("bg_dim", Math.Clamp(value, 0, 200)); Save(); }
    }

    /// <summary>卡片底色不透明度 10~100（只动底色，不动文字）。</summary>
    public int CardOpacity
    {
        get => Math.Clamp(GetInt("card_opacity", 100), 10, 100);
        set { Set("card_opacity", Math.Clamp(value, 10, 100)); Save(); }
    }

    public bool AnimationsEnabled
    {
        get => GetBool("anim_enabled", true);
        set { Set("anim_enabled", value); Save(); }
    }

    /// <summary>入场动画带不带淡入。</summary>
    public bool AnimationFade => AnimationsEnabled && GetBool("anim_fade", true);

    public string AnimationPreset
    {
        get => GetString("anim_preset") ?? "slide_left";
        set { Set("anim_preset", value); Save(); }
    }

    public string AnimationSpeed
    {
        get => GetString("anim_speed") ?? "normal";
        set { Set("anim_speed", value); Save(); }
    }


    public bool MultiThread
    {
        get => GetBool("multi_thread", false);
        set { Set("multi_thread", value); Save(); }
    }

    public int DownloadThreads
    {
        get => Math.Clamp(GetInt("download_threads", DefaultThreads), MinThreadRange, MaxThreadRange);
        set { Set("download_threads", Math.Clamp(value, MinThreadRange, MaxThreadRange)); Save(); }
    }

    /// <summary>实际用几个线程。</summary>
    public int EffectiveThreads => MultiThread ? DownloadThreads : DefaultThreads;

    /// <summary>
    /// 下载源策略（设置 → 下载 → 「下载源」）；⚠️ 存的是**字符串**（`auto` / `official` /
    /// `mirror` / `dual`），不是枚举序号：以后往枚举中间插值，序号型的配置会静默变成另一个策略。
    /// </summary>
    public DownloadSourceMode DownloadSource
    {
        get => DownloadSourceModes.Parse(GetString("download_source"));
        set { Set("download_source", DownloadSourceModes.ToKey(value)); Save(); }
    }

    /// <summary>
    /// 「游戏语言」：这个版本**第一次启动**时写进 `options.txt` 的语言（设置 → 其他）。
    /// </summary>
    public string GameLanguage
    {
        get
        {
            var saved = (GetString("game_language") ?? string.Empty).Trim();
            return saved.Length == 0 ? GameLocales.Auto : saved;
        }
        set { Set("game_language", string.IsNullOrWhiteSpace(value) ? GameLocales.Auto : value!.Trim()); Save(); }
    }
}
