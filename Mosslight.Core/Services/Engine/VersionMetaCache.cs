using System.Text;
using System.Text.RegularExpressions;

namespace Mosslight.Core.Services.Engine;

/// <summary>版本私有元数据：`mosslight.ini`（UTF-8 无 BOM）+ PCL2 的 `Setup.ini` 兜底。</summary>
public static class VersionMetaCache
{
    public const string FileName = "mosslight.ini";

    public sealed record Meta(string? VanillaVersion, string? LoaderType, string? LoaderVersion);

    /// <summary>合法原版号：只认 `1.20.4` / `26.2` / `1.7.10` 这种**纯数字点分**。</summary>
    public static bool IsValidVanilla(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && Regex.IsMatch(value.Trim(), @"^\d+(\.\d+)*$");

    public static Meta? Read(string dirPath)
    {
        var path = Path.Combine(dirPath, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var values = ParseIni(File.ReadAllText(path, Encoding.UTF8), "VersionInfo");
            return new Meta(Grab(values, "VanillaVersion"), Grab(values, "LoaderType"), Grab(values, "LoaderVersion"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void Write(string dirPath, string vanilla, string? loaderType, string? loaderVersion)
    {
        if (string.IsNullOrWhiteSpace(vanilla)) return;
        var path = Path.Combine(dirPath, FileName);
        var text = "[VersionInfo]" + Environment.NewLine
                 + "VanillaVersion=" + vanilla + Environment.NewLine
                 + "LoaderType=" + (loaderType ?? string.Empty) + Environment.NewLine
                 + "LoaderVersion=" + (loaderVersion ?? string.Empty) + Environment.NewLine;
        try
        {
            if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == text) return;
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or System.Security.SecurityException) { }
    }

    public static Meta? ReadPclSetup(string dirPath)
    {
        var path = Path.Combine(dirPath, "Setup.ini");
        if (!File.Exists(path)) return null;
        try
        {
            var values = ParseIni(File.ReadAllText(path, Encoding.UTF8), null);
            string? loader = null;
            foreach (var (key, _) in values)
                if (key.StartsWith("Version", StringComparison.OrdinalIgnoreCase) && key.Length > 7) loader = key[7..];
            return new Meta(Grab(values, "VersionVanillaName"), loader, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static List<(string Key, string Value)> ParseIni(string text, string? section)
    {
        var list = new List<(string, string)>();
        var current = string.Empty;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { current = line[1..^1].Trim(); continue; }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (section is not null && !current.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add((line[..eq].Trim(), line[(eq + 1)..].Trim()));
        }
        return list;
    }

    private static string? Grab(List<(string Key, string Value)> values, string key)
    {
        foreach (var (k, v) in values)
            if (k.Equals(key, StringComparison.OrdinalIgnoreCase) && v.Length > 0) return v;
        return null;
    }
}
