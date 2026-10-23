using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace Mosslight.Core.Services.Engine;

// 启动相关的纯函数（rules / natives / maven / 离线 UUID / 参数拆分），不做 IO。
// ⚠️ rules 采用「最后一条匹配生效」；离线 UUID 必须是 `OfflinePlayer:<名字>` 的 MD5；参数不能按空格简单 split。

/// <summary>当前运行环境（版本 JSON 里的 `os.name` / `os.arch` 用的写法）。</summary>
public static class PlatformInfo
{
    /// <summary>JSON 里 `os.name` 的写法。</summary>
    public static string PlatformName
    {
        get
        {
            if (OperatingSystem.IsWindows()) return "windows";
            if (OperatingSystem.IsMacOS()) return "osx";
            if (OperatingSystem.IsLinux()) return "linux";
            return "windows";
        }
    }

    /// <summary>JSON 里 `os.arch` 的写法。</summary>
    public static string ArchName => Environment.Is64BitOperatingSystem ? "x86_64" : "x86";

    /// <summary>`${arch}` 占位符要填的东西：64 或 32（**不是** x86_64）。</summary>
    public static string ArchBits => ArchName == "x86_64" ? "64" : "32";

    /// <summary>等价的 `platform.version()`。</summary>
    public static string OsVersion => Environment.OSVersion.Version.ToString();

    /// <summary>classpath 分隔符。</summary>
    public static string ClasspathSeparator => OperatingSystem.IsWindows() ? ";" : ":";
}

/// <summary>Minecraft 用来决定「哪些参数要加」的那套开关。</summary>
public sealed class FeatureSet
{
    public static readonly FeatureSet Default = new();

    private readonly Dictionary<string, bool> _values = new(StringComparer.Ordinal)
    {
        ["is_demo_user"] = false,
        ["has_custom_resolution"] = true,
        ["has_quick_plays_support"] = false,
        ["is_quick_play_singleplayer"] = false,
        ["is_quick_play_multiplayer"] = false,
        ["is_quick_play_realms"] = false,
    };

    public bool Get(string key) => _values.TryGetValue(key, out var value) && value;

    public void Set(string key, bool value) => _values[key] = value;
}

/// <summary>版本 JSON 里的 rules / natives / maven 坐标。</summary>
public static class LaunchRules
{
    /// <summary>继承链最深几层。</summary>
    public const int MaxInheritDepth = 5;

    // rules

    /// <summary>这一条规则在当前环境下适不适用。</summary>
    private static bool RuleMatches(JsonObject rule, FeatureSet features)
    {
        if (rule["os"] is JsonObject os)
        {
            if (os["name"] is JsonValue nameNode &&
                nameNode.GetValueKind() == System.Text.Json.JsonValueKind.String &&
                !string.Equals(nameNode.GetValue<string>(), PlatformInfo.PlatformName, StringComparison.Ordinal))
            {
                return false;
            }

            if (os["arch"] is JsonValue archNode &&
                archNode.GetValueKind() == System.Text.Json.JsonValueKind.String &&
                !string.Equals(archNode.GetValue<string>(), PlatformInfo.ArchName, StringComparison.Ordinal))
            {
                return false;
            }

            // 正则匹配系统版本号，几乎没有 JSON 用，但规范里有
            if (os["version"] is JsonValue versionNode &&
                versionNode.GetValueKind() == System.Text.Json.JsonValueKind.String)
            {
                try
                {
                    if (!Regex.IsMatch(PlatformInfo.OsVersion, versionNode.GetValue<string>()))
                    {
                        return false;
                    }
                }
                catch (ArgumentException)
                {
                    // 正则本身写坏了 → 当作不匹配（跟 Python 版一样吞掉 re.error）
                    return false;
                }
            }
        }

        if (rule["features"] is JsonObject featuresNode)
        {
            foreach (var (key, wanted) in featuresNode)
            {
                var wantedBool = wanted is JsonValue value &&
                                 value.GetValueKind() == System.Text.Json.JsonValueKind.True;
                if (features.Get(key) != wantedBool)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>判断一个带 rules 的库 / 参数在当前环境下要不要。</summary>
    public static bool RulesAllow(JsonNode? rules, FeatureSet? features = null)
    {
        if (rules is not JsonArray array || array.Count == 0)
        {
            return true;
        }

        features ??= FeatureSet.Default;
        var allowed = false;
        foreach (var node in array)
        {
            if (node is not JsonObject rule)
            {
                continue;
            }
            if (RuleMatches(rule, features))
            {
                allowed = rule["action"] is JsonValue action &&
                          action.GetValueKind() == System.Text.Json.JsonValueKind.String &&
                          action.GetValue<string>() == "allow";
            }
        }
        return allowed;
    }

    // natives

    /// <summary>取这个库的原生文件 classifier（如 `natives-windows-x86_64`）没有就 null。</summary>
    public static string? NativesKey(JsonObject lib)
    {
        if (lib["natives"] is not JsonObject natives)
        {
            return null;
        }
        if (natives[PlatformInfo.PlatformName] is not JsonValue value ||
            value.GetValueKind() != System.Text.Json.JsonValueKind.String)
        {
            return null;
        }
        // `natives-windows-${arch}` → `natives-windows-64`
        return value.GetValue<string>().Replace("${arch}", PlatformInfo.ArchBits, StringComparison.Ordinal);
    }

    // maven

    /// <summary>Maven 坐标 → (相对路径, 相对目录)。</summary>
    public static (string? RelativePath, string? RelativeDir) MavenToPath(string? name)
    {
        if (string.IsNullOrEmpty(name) || !name.Contains(':'))
        {
            return (null, null);
        }

        var ext = "jar";
        if (name.Contains('@'))
        {
            var at = name.LastIndexOf('@');
            ext = name[(at + 1)..];
            name = name[..at];
        }

        var parts = name.Split(':');
        if (parts.Length < 3)
        {
            return (null, null);
        }

        var group = parts[0];
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? parts[3] : null;

        var filename = $"{artifact}-{version}";
        if (!string.IsNullOrEmpty(classifier))
        {
            filename += $"-{classifier}";
        }
        filename += $".{ext}";

        var relDir = $"{group.Replace('.', '/')}/{artifact}/{version}";
        return ($"{relDir}/{filename}", relDir);
    }

    // 离线账户

    /// <summary>
    /// 离线模式的标准 UUID；⚠️ 不要图省事写死成全零 —— 那样同一个玩家在不同启动器里 UUID 不同，存
    /// 档里的玩家数据、服务器白名单会对不上。
    /// </summary>
    public static string OfflineUuid(string name)
    {
        var digest = MD5.HashData(Encoding.UTF8.GetBytes($"OfflinePlayer:{name}"));
        digest[6] = (byte)((digest[6] & 0x0F) | 0x30);
        digest[8] = (byte)((digest[8] & 0x3F) | 0x80);

        // 按 UUID 的写法排：4-2-2-2-6 组，且是**大端字节序**
        var hex = Convert.ToHexString(digest).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    /// <summary>
    /// 早期启动器用的离线 UUID：同样是 `OfflinePlayer:名字` 的 MD5，但**不置版本位**。
    /// </summary>
    public static string OfflineUuidLegacy(string name)
    {
        var digest = MD5.HashData(Encoding.UTF8.GetBytes($"OfflinePlayer:{name}"));
        var hex = Convert.ToHexString(digest).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    // 参数拆分

    /// <summary>把一串参数按空格拆成列表，但尊重引号（引号本身去掉）。</summary>
    public static List<string> SplitArgs(string? text)
    {
        var args = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return args;
        }

        var current = new StringBuilder();
        char quote = '\0';
        foreach (var ch in text)
        {
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }
        return args;
    }
}
