using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosslight.Core.Services.Engine;

// ============================================================================
//  按版本覆盖启动参数
//  （移植自 1.0.0 的 core/version_settings.py）
//
//  ## 为什么不写进版本 JSON
//
//  `versions/<id>/<id>.json` 是**安装器管的**（PCL、HMCL、官方启动器都往里写），
//  改它有两个后果：
//
//  1. 我们自己的"文件校验 / 补全缺失的库"会把它当成被改坏的版本
//  2. 用户下次用别的启动器更新那个版本，改动直接被覆盖
//
//  所以放我们自己的边文件：`<配置目录>/versions.json`，
//  键是**版本 id**（就是 `versions/` 下面那个目录名）。
//
//  ## 三态，不是两态
//
//  每一项要么「跟随启动器默认」—— 文件里**没有**这个键；
//  要么「这个版本自己定」—— 文件里有这个键。
//
//  **不是**"把默认值抄一份过来"。抄过来的话，以后改了启动器默认，
//  所有已经动过一次的版本都不会跟着变，用户会莫名其妙（PCL2 也是没动过的项跟着全局走）。
//  所以"取消自定义" = 把这个键删掉。
// ============================================================================

/// <summary>一个版本的覆盖项（只有用户真的设过的键才在里面）。</summary>
public sealed record PerVersionSettings
{
    public string? JavaPath { get; set; }

    public int? MinMemory { get; set; }

    public int? MaxMemory { get; set; }

    public string? ExtraJvmArgs { get; set; }

    public string? ExtraGameArgs { get; set; }

    /// <summary>自定义信息：替换 `${version_type}`（游戏里那行字）。</summary>
    public string? CustomInfo { get; set; }

    /// <summary>自动进入服务器："ip" 或 "ip:port"。</summary>
    public string? ServerAddress { get; set; }

    /// <summary>这个版本在列表里用哪张图（配置目录 `icons/<文件名>`）。</summary>
    public string? Icon { get; set; }

    public bool IsEmpty =>
        string.IsNullOrEmpty(JavaPath) && MinMemory is null && MaxMemory is null
        && string.IsNullOrEmpty(ExtraJvmArgs) && string.IsNullOrEmpty(ExtraGameArgs)
        && string.IsNullOrEmpty(CustomInfo) && string.IsNullOrEmpty(ServerAddress)
        && string.IsNullOrEmpty(Icon);
}

/// <summary>所有版本的覆盖项。整体读一次、改完整体写回（文件很小）。</summary>
public sealed class VersionSettings
{
    /// <summary>配置格式版本（跟 config.json 一个意思，以后改结构时 +1）。</summary>
    private const int SettingsVersion = 1;

    /// <summary>
    /// 一个版本能覆盖哪些项。加新项只改这两处：页面和保存都按这个表走。
    ///
    /// 注意这里**没有**窗口尺寸之类 —— 那些还没有"启动器默认值"，
    /// 一个只有版本覆盖、没有全局默认的项，界面上的"跟随默认"就没意义了。
    /// </summary>
    public static readonly string[] Fields =
    {
        "java_path", "min_memory", "max_memory", "extra_jvm_args",
        "extra_game_args", "custom_info", "server_address", "icon",
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, PerVersionSettings> _data = new(StringComparer.Ordinal);

    public VersionSettings(string configDir)
    {
        _path = Path.Combine(configDir, "versions.json");
        Load();
    }

    public string FilePath => _path;

    // ---------- 读写 ----------

    private void Load()
    {
        JsonObject? root = null;
        if (File.Exists(_path))
        {
            try
            {
                root = JsonIO.ParseBytes(File.ReadAllBytes(_path)) as JsonObject;
            }
            catch (Exception ex) when (ex is IOException or JsonException
                                          or UnauthorizedAccessException or NotSupportedException)
            {
                // 读不出来就当没有 —— 跟 config.json 一样，不能让配置文件
                // 把启动器搞得起不来
                System.Diagnostics.Debug.WriteLine($"[VersionSettings] 读取失败，这次当空的: {ex.Message}");
            }
        }

        lock (_lock)
        {
            _data.Clear();
            if (root?["versions"] is JsonObject versions)
            {
                foreach (var (versionId, node) in versions)
                {
                    if (node is not JsonObject overrides)
                    {
                        continue;
                    }
                    var clean = Normalize(overrides);
                    if (!clean.IsEmpty)
                    {
                        _data[versionId] = clean;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 只留认识的项、去掉空值、顺手纠正类型。
    ///
    /// ⚠️ 空字符串 / 0 都等于"没设"，**不存** —— 免得文件里一堆空项，
    ///    界面上的"跟随默认"判起来也麻烦。
    /// </summary>
    private static PerVersionSettings Normalize(JsonObject overrides)
    {
        string? Str(string key)
        {
            var text = ReadString(overrides, key)?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        int? Int(string key)
        {
            var value = ReadInt(overrides, key);
            return value is > 0 ? value : null;
        }

        return new PerVersionSettings
        {
            JavaPath = Str("java_path"),
            MinMemory = Int("min_memory"),
            MaxMemory = Int("max_memory"),
            ExtraJvmArgs = Str("extra_jvm_args"),
            ExtraGameArgs = Str("extra_game_args"),
            CustomInfo = Str("custom_info"),
            ServerAddress = Str("server_address"),
            Icon = Str("icon"),
        };
    }

    /// <summary>
    /// 字符串取值。
    ///
    /// ⚠️ 不强转：配置文件是纯文本，用户手改过之后 `min_memory` 可能是 `"4096"`
    ///    这种字符串，但 `java_path` 也可能被写成数字 —— 那明显是用户填错了，
    ///    当没设比硬转成 "123" 强。
    /// </summary>
    private static string? ReadString(JsonObject obj, string key)
        => obj[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String
            ? v.GetValue<string>()
            : null;

    private static int? ReadInt(JsonObject obj, string key)
    {
        if (obj[key] is not JsonValue v)
        {
            return null;
        }
        return v.GetValueKind() switch
        {
            JsonValueKind.Number => v.GetValue<int>(),
            JsonValueKind.String => int.TryParse(v.GetValue<string>().Trim(), out var n) ? n : null,
            _ => null,
        };
    }

    /// <summary>原子写。写不进去只记日志、返回 false，不抛异常。</summary>
    public bool Save()
    {
        JsonObject versions;
        lock (_lock)
        {
            versions = new JsonObject();
            foreach (var (versionId, settings) in _data)
            {
                versions[versionId] = ToJson(settings);
            }
        }

        var payload = new JsonObject
        {
            ["settings_version"] = SettingsVersion,
            ["versions"] = versions,
        };

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, payload.ToJsonString(WriteOptions) + "\n", new UTF8Encoding(false));
            File.Move(tmp, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[VersionSettings] 保存失败: {ex.Message}");
            return false;
        }
    }

    private static JsonObject ToJson(PerVersionSettings s)
    {
        var obj = new JsonObject();
        if (!string.IsNullOrEmpty(s.JavaPath)) obj["java_path"] = s.JavaPath;
        if (s.MinMemory is > 0) obj["min_memory"] = s.MinMemory.Value;
        if (s.MaxMemory is > 0) obj["max_memory"] = s.MaxMemory.Value;
        if (!string.IsNullOrEmpty(s.ExtraJvmArgs)) obj["extra_jvm_args"] = s.ExtraJvmArgs;
        if (!string.IsNullOrEmpty(s.ExtraGameArgs)) obj["extra_game_args"] = s.ExtraGameArgs;
        if (!string.IsNullOrEmpty(s.CustomInfo)) obj["custom_info"] = s.CustomInfo;
        if (!string.IsNullOrEmpty(s.ServerAddress)) obj["server_address"] = s.ServerAddress;
        if (!string.IsNullOrEmpty(s.Icon)) obj["icon"] = s.Icon;
        return obj;
    }

    // ---------- 查询与修改 ----------

    /// <summary>这个版本的覆盖项（没有就是空的）。</summary>
    public PerVersionSettings Get(string versionId)
    {
        lock (_lock)
        {
            return _data.TryGetValue(versionId, out var s) ? Clone(s) : new PerVersionSettings();
        }
    }

    public bool IsCustom(string versionId, string field)
    {
        var s = Get(versionId);
        return field switch
        {
            "java_path" => !string.IsNullOrEmpty(s.JavaPath),
            "min_memory" => s.MinMemory is > 0,
            "max_memory" => s.MaxMemory is > 0,
            "extra_jvm_args" => !string.IsNullOrEmpty(s.ExtraJvmArgs),
            "extra_game_args" => !string.IsNullOrEmpty(s.ExtraGameArgs),
            "custom_info" => !string.IsNullOrEmpty(s.CustomInfo),
            "server_address" => !string.IsNullOrEmpty(s.ServerAddress),
            "icon" => !string.IsNullOrEmpty(s.Icon),
            _ => false,
        };
    }

    /// <summary>
    /// 把传进来的覆盖项**也过一遍纠正**，然后整体替换。
    ///
    /// ⚠️ `Update` 必须自己纠正一次，不能直接存：**界面填的东西也会带空格**
    ///    （用户复制粘贴一个路径，前后十有八九有空白）。只在读 JSON 时纠正的话，
    ///    刚设完的值是带空格的，重启一次才变干净 —— 而且两份值还会被
    ///    "落盘再读回来一样吗"这种检查抓个正着。
    ///    1.0.0 的 `update()` 也是 `clean = self._normalize(overrides)` 再存。
    /// </summary>
    public bool Update(string versionId, PerVersionSettings overrides)
    {
        var clean = Normalize(overrides);
        lock (_lock)
        {
            if (clean.IsEmpty)
            {
                _data.Remove(versionId);
            }
            else
            {
                _data[versionId] = clean;
            }
        }
        return Save();
    }

    /// <summary>字符串 trim、0 / 空当"没设"、然后判空。</summary>
    private static PerVersionSettings Normalize(PerVersionSettings s) => new()
    {
        JavaPath = Trim(s.JavaPath),
        MinMemory = s.MinMemory is > 0 ? s.MinMemory : null,
        MaxMemory = s.MaxMemory is > 0 ? s.MaxMemory : null,
        ExtraJvmArgs = Trim(s.ExtraJvmArgs),
        ExtraGameArgs = Trim(s.ExtraGameArgs),
        CustomInfo = Trim(s.CustomInfo),
        ServerAddress = Trim(s.ServerAddress),
        Icon = Trim(s.Icon),
    };

    private static string? Trim(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>忘掉这个版本的覆盖（= 全部跟随默认）。</summary>
    public bool Forget(string versionId) => Update(versionId, new PerVersionSettings());

    /// <summary>所有被覆盖过的版本 id（界面可以标一下"这个版本改过"）。</summary>
    public IReadOnlyList<string> CustomizedVersions()
    {
        lock (_lock)
        {
            return _data.Keys.ToList();
        }
    }

    /// <summary>
    /// 把覆盖和启动器默认合起来，返回**最终生效**的值。
    ///
    /// <paramref name="defaults"/> 由调用方给（默认值在 config.json 里，
    /// 这个类不去读它，免得又多一个环形依赖）。
    /// </summary>
    public EffectiveSettings Resolve(string versionId, EffectiveSettings defaults)
    {
        var s = Get(versionId);
        var result = defaults with
        {
            JavaPath = !string.IsNullOrEmpty(s.JavaPath) ? s.JavaPath! : defaults.JavaPath,
            MinMemory = s.MinMemory is > 0 ? s.MinMemory.Value : defaults.MinMemory,
            MaxMemory = s.MaxMemory is > 0 ? s.MaxMemory.Value : defaults.MaxMemory,
            ExtraJvmArgs = !string.IsNullOrEmpty(s.ExtraJvmArgs) ? s.ExtraJvmArgs! : defaults.ExtraJvmArgs,
            ExtraGameArgs = !string.IsNullOrEmpty(s.ExtraGameArgs) ? s.ExtraGameArgs! : defaults.ExtraGameArgs,
            CustomInfo = !string.IsNullOrEmpty(s.CustomInfo) ? s.CustomInfo! : defaults.CustomInfo,
            ServerAddress = !string.IsNullOrEmpty(s.ServerAddress) ? s.ServerAddress! : defaults.ServerAddress,
            Icon = !string.IsNullOrEmpty(s.Icon) ? s.Icon! : defaults.Icon,
        };

        // ⚠️ 最小堆不能大于最大堆：两边可能一个来自默认、一个来自覆盖
        //    （默认 max 2048 + 版本自定义 min 4096），不夹一下 JVM 会直接拒绝启动。
        if (result.MinMemory > result.MaxMemory)
        {
            result = result with { MinMemory = result.MaxMemory };
        }
        return result;
    }

    private static PerVersionSettings Clone(PerVersionSettings s) => s with { };
}

/// <summary>合并之后的最终生效值（启动一次要用的那些）。</summary>
public sealed record EffectiveSettings(
    string JavaPath = "",
    int MinMemory = 512,
    int MaxMemory = 2048,
    string ExtraJvmArgs = "",
    string ExtraGameArgs = "",
    string CustomInfo = "",
    string ServerAddress = "",
    string Icon = "");
