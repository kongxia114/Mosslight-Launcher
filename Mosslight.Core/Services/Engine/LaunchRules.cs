using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace Mosslight.Core.Services.Engine;

// ============================================================================
//  启动相关的**纯函数**
//
//  移植自 1.0.0 的 core/launch.py 上半部分（rules / natives / maven / 离线 UUID /
//  参数拆分）。这里一个 IO 都不做，所以能直接单测，也能对着真实命令行逐项 diff。
//
//  几个容易错的点，都在这里一次性处理掉：
//
//  1. **rules 的语义是「最后一条匹配的规则生效」**，不是第一条。
//     而且 `features` 必须显式判断 —— 游戏分辨率参数（--width/--height）就藏在
//     `has_custom_resolution` 这个 feature 规则后面，一律跳过就会丢。
//
//  2. **离线 UUID 不能随便编。** 必须是 `OfflinePlayer:<名字>` 的 MD5（v3 UUID），
//     否则同一个玩家在不同启动器里 UUID 不一样，存档和服务器的玩家数据全对不上。
//
//  3. **参数拆分不能图省事用正则或按空格 split** —— Windows 路径里有反斜杠，
//     用户填的引号还可能是半截的。见 <see cref="SplitArgs"/>。
// ============================================================================

/// <summary>当前运行环境（版本 JSON 里的 `os.name` / `os.arch` 用的写法）。</summary>
public static class PlatformInfo
{
    /// <summary>JSON 里 `os.name` 的写法。这个启动器只做 Windows，但仍然写全 —— 少一个平台就少一处 if。</summary>
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

    /// <summary>JSON 里 `os.arch` 的写法。只有 32 位才需要特别标明。</summary>
    public static string ArchName => Environment.Is64BitOperatingSystem ? "x86_64" : "x86";

    /// <summary>`${arch}` 占位符要填的东西：64 或 32（**不是** x86_64）。</summary>
    public static string ArchBits => ArchName == "x86_64" ? "64" : "32";

    /// <summary>等价的 `platform.version()`。</summary>
    public static string OsVersion => Environment.OSVersion.Version.ToString();

    /// <summary>classpath 分隔符。Windows 用分号，别的平台用冒号。</summary>
    public static string ClasspathSeparator => OperatingSystem.IsWindows() ? ";" : ":";
}

/// <summary>Minecraft 用来决定「哪些参数要加」的那套开关。</summary>
public sealed class FeatureSet
{
    /// <summary>
    /// ⚠️ `has_custom_resolution` 必须是 **true** —— 我们总是传 `--width/--height`。
    /// 其余几个（演示模式、快速进入）都是 false，跳过对应参数。
    /// </summary>
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
    /// <summary>继承链最深几层。超过就报错，免得环形 inheritsFrom 把栈吃光。</summary>
    public const int MaxInheritDepth = 5;

    // ---------- rules ----------

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

    /// <summary>
    /// 判断一个带 rules 的库 / 参数在当前环境下要不要。
    ///
    /// 官方语义（和 PCL、官方启动器一致）：
    ///   - rules 缺失或为空 → 允许
    ///   - 否则：**最后一条匹配的规则**决定结果
    ///   - 一条都没匹配上 → 不允许
    /// </summary>
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

    // ---------- natives ----------

    /// <summary>取这个库的原生文件 classifier（如 `natives-windows-x86_64`）；没有就 null。</summary>
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

    // ---------- maven ----------

    /// <summary>
    /// Maven 坐标 → (相对路径, 相对目录)。
    ///
    /// 支持 `group:artifact:version[:classifier][@ext]`。
    /// 解析不出来返回 (null, null)。
    /// </summary>
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

    // ---------- 离线账户 ----------

    /// <summary>
    /// 离线模式的标准 UUID。
    ///
    /// 等价于 Java 的 `UUID.nameUUIDFromBytes(("OfflinePlayer:" + name).getBytes())`，
    /// 也就是 MD5 后把版本位置成 3、variant 位置成 10xx。
    ///
    /// ⚠️ 不要图省事写死成全零 —— 那样同一个玩家在不同启动器里 UUID 不同，
    /// 存档里的玩家数据、服务器白名单会对不上。
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

    // ---------- 参数拆分 ----------

    /// <summary>
    /// 把一串参数按空格拆成列表，但尊重引号（引号本身去掉）。
    ///
    /// **刻意不用命令行解析库**：POSIX 模式会把 Windows 路径里的反斜杠当转义符吃掉
    /// （`-Djava.library.path=D:\mc` 变成 `D:mc`），而不是 POSIX 模式又根本不合并引号
    /// （`-Dpath="C:\Program Files"` 会被拆成两个参数）。自己扫一遍最稳，
    /// 而且未闭合的引号也不会抛异常 —— 用户填错一个引号不该让启动直接失败。
    /// </summary>
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
