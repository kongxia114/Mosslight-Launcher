using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Mosslight.Core.Services.Engine;

public static class LegacyAssets
{
    public const string LegacyIndex = "legacy";
    public const string PreLegacyIndex = "pre-1.6";

    public static bool IsLegacy(string? indexId)
        => string.Equals(indexId, LegacyIndex, StringComparison.Ordinal)
           || string.Equals(indexId, PreLegacyIndex, StringComparison.Ordinal);

    public static string EnsureResources(string mcDir, string gameDir, string indexId)
    {
        var target = Path.Combine(gameDir, "resources");
        try
        {
            Materialize(mcDir, target, indexId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.Text.Json.JsonException or InvalidOperationException
                                      or NotSupportedException)
        {
        }
        return target;
    }

    private static void Materialize(string mcDir, string target, string indexId)
    {
        var indexPath = Path.Combine(mcDir, "assets", "indexes", indexId + ".json");
        if (!File.Exists(indexPath))
        {
            return;
        }

        var objects = (JsonIO.ParseBytes(File.ReadAllBytes(indexPath)) as JsonObject)?["objects"] as JsonObject;
        if (objects is null)
        {
            return;
        }

        var objectsRoot = Path.Combine(mcDir, "assets", "objects");
        Directory.CreateDirectory(target);

        foreach (var pair in objects)
        {
            if (pair.Value is not JsonObject entry)
            {
                continue;
            }
            var hash = entry["hash"] is JsonValue hashNode &&
                       hashNode.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? hashNode.GetValue<string>()
                : string.Empty;
            if (hash.Length < 2)
            {
                continue;
            }

            var source = Path.Combine(objectsRoot, hash[..2], hash);
            if (!File.Exists(source))
            {
                continue;
            }

            var destination = Path.Combine(target, pair.Key.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(destination))
            {
                continue;
            }

            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            try
            {
                if (!TryCreateHardLink(destination, source))
                {
                    File.Copy(source, destination, overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

    private static bool TryCreateHardLink(string linkPath, string existingPath)
    {
        try
        {
            return CreateHardLinkW(linkPath, existingPath, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
