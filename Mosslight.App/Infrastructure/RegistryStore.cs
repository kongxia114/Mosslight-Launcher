using System;
using Microsoft.Win32;

namespace Mosslight.App.Infrastructure;

/// <summary>
/// 注册表里的那点机器本地状态（`HKCU\Software\Mosslight`）；⚠️ 读写**永远不抛**：值被改坏（类型不
/// 对、权限没有、键被删）一律当"没有"，调用方拿到 null 就走默认值 —— 一个坏注册表值不该让启动器起
/// 不来。
/// </summary>
public static class RegistryStore
{
    private const string Root = @"Software\Mosslight";

    /// <summary>会话级设置（窗口大小、语言）用的子键。</summary>
    public const string SessionKey = "Session";

    /// <summary>读一个整数；读不到 / 类型不对都返回 null。</summary>
    public static int? ReadInt(string section, string name)
        => Read(section, name) switch
        {
            int i => i,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => null,
        };

    /// <summary>读一个布尔；`1` / `true` 为真，`0` / `false` 为假，其它当没有。</summary>
    public static bool? ReadBool(string section, string name)
        => Read(section, name) switch
        {
            int i => i != 0,
            string s when bool.TryParse(s, out var parsed) => parsed,
            string s when s == "1" => true,
            string s when s == "0" => false,
            _ => null,
        };

    /// <summary>读一个字符串；空白当没有。</summary>
    public static string? ReadString(string section, string name)
        => Read(section, name) is string s && !string.IsNullOrWhiteSpace(s) ? s : null;

    public static void WriteInt(string section, string name, int value)
        => Write(section, name, value);

    public static void WriteBool(string section, string name, bool value)
        => Write(section, name, value ? 1 : 0);

    public static void WriteString(string section, string name, string value)
        => Write(section, name, value);

    /// <summary>删掉一个值（没有也算成功）。</summary>
    public static void Delete(string section, string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{Root}\{section}", writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException
                                       or System.IO.IOException)
        {
            // 注册表写不动（被策略挡住 / 键被占用）：状态记不下来而已，不影响使用
        }
    }

    private static object? Read(string section, string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{Root}\{section}");
            return key?.GetValue(name);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException
                                       or System.IO.IOException)
        {
            return null;
        }
    }

    private static void Write(string section, string name, object value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{Root}\{section}");
            key?.SetValue(name, value);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException
                                       or System.IO.IOException)
        {
            // 同上：记不下来就算了
        }
    }
}
