using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosslight.Core.Services.Engine;

//  ⚠️ **永远不抛异常**：坏 jar、`.jar.disabled` 里其实不是 zip 的文件、被游戏
//  ⚠️ **不解压整个 jar**：`ZipArchive` 只读中央目录 + 那两三个小文件，
// 设计说明见 docs/design-notes.md#mosslight-core-services-engine-modmetadata-cs

/// <summary>一个 mod 的展示信息。读不出元数据时只有文件名（<see cref="Name"/>）。</summary>
public sealed record ModMeta(
    string Name,
    string Version,
    string Loader,
    string Authors,
    /// <summary>图标原始字节（PNG / JPEG），没有就是 null。界面自己解码。</summary>
    byte[]? Icon = null)
{
    /// <summary>真的从 jar 里读到了东西（不是退回文件名那种）。</summary>
    public bool FromJar => Loader.Length > 0 || Version.Length > 0 || Authors.Length > 0;
}

/// <summary>读 mod jar 的展示信息（见文件头的优先级表）。</summary>
public static class ModMetadata
{
    /// <summary>元数据文件的读取上限（防"把巨大的文本塞进 fabric.mod.json"拖死扫描）。</summary>
    private const long MaxMetaBytes = 512 * 1024;

    /// <summary>图标上限：列表里只画 32×32，超过 512KB 的多半不是正经图标。</summary>
    private const long MaxIconBytes = 512 * 1024;

    /// <summary>没有元数据指路时，按约定去找的几个位置（相对 jar 根）。</summary>
    private static readonly string[] IconFallbacks = { "icon.png", "logo.png", "pack.png" };

    /// <summary>
    /// 读一个 mod jar（`.jar` 或 `.jar.disabled` 都行）。
    ///
    /// **不抛异常**：读不出来就返回"文件名当名字"的结果。
    /// </summary>
    public static ModMeta Read(string jarPath)
    {
        var stem = FileStem(jarPath);
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);

            var manifest = ReadText(zip, "META-INF/MANIFEST.MF");
            var manifestVersion = ManifestValue(manifest, "Implementation-Version");
            var manifestTitle = ManifestValue(manifest, "Implementation-Title");

            var (meta, iconHint, modId) = Resolve(zip, stem, manifestVersion, manifestTitle);
            return meta with { Icon = ReadIcon(zip, iconHint, modId) };
        }
        catch (Exception ex) when (ex is IOException
                                       or InvalidDataException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or ArgumentException)
        {
            // 打不开 / 不是 zip / 被锁住 / 路径怪 —— 都只是"读不出信息"
        }

        return new ModMeta(stem, string.Empty, string.Empty, string.Empty);
    }

    /// <summary>按优先级挑一个元数据源，顺带把"图标在哪"（<paramref name="iconHint"/>）和 modid 带出来。</summary>
    private static (ModMeta Meta, string IconHint, string ModId) Resolve(
        ZipArchive zip, string stem, string manifestVersion, string manifestTitle)
    {
        // 1. Fabric / Quilt：JSON，字段最全（也最可能有 icon）
        if (TryFabric(ReadText(zip, "fabric.mod.json"), "Fabric", stem,
                out var fabric, out var fabricIcon, out var fabricId))
        {
            return (fabric, fabricIcon, fabricId);
        }
        if (TryQuilt(ReadText(zip, "quilt.mod.json"), stem,
                out var quilt, out var quiltIcon, out var quiltId))
        {
            return (quilt, quiltIcon, quiltId);
        }

        // 2. Forge / NeoForge：TOML。⚠️ NeoForge 的**必须是** META-INF/neoforge.mods.toml，
        //    Forge 的是 META-INF/mods.toml —— 两个名字不能混，混了就报"找不到 mods.toml"
        var neo = ReadText(zip, "META-INF/neoforge.mods.toml");
        if (neo.Length > 0)
        {
            return FromToml(neo, "NeoForge", stem, manifestVersion);
        }
        var forge = ReadText(zip, "META-INF/mods.toml");
        if (forge.Length > 0)
        {
            return FromToml(forge, "Forge", stem, manifestVersion);
        }

        // 3. 1.12 及以前那套
        if (TryMcModInfo(ReadText(zip, "mcmod.info"), stem,
                out var legacy, out var legacyIcon, out var legacyId))
        {
            return (legacy, legacyIcon, legacyId);
        }

        // 4. 只剩清单文件里的标题
        if (manifestTitle.Length > 0)
        {
            return (new ModMeta(manifestTitle, manifestVersion, string.Empty, string.Empty), string.Empty, string.Empty);
        }

        return (new ModMeta(stem, string.Empty, string.Empty, string.Empty), string.Empty, string.Empty);
    }

    /// <summary>
    /// `sodium-fabric-0.5.8.jar.disabled` → `sodium-fabric-0.5.8`。
    ///
    /// ⚠️ 先摘 `.disabled` 再摘 `.jar`：反过来的话 `.jar.disabled` 会变成
    ///    `sodium-fabric-0.5.8.jar`（尾巴上留着 `.jar`）。
    /// </summary>
    public static string FileStem(string path)
    {
        var name = Path.GetFileName(path ?? string.Empty);
        if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".disabled".Length];
        }
        if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".jar".Length];
        }
        return name.Length > 0 ? name : Path.GetFileName(path ?? string.Empty);
    }

    // ---------- 各家的元数据 ----------

    private static bool TryFabric(string json, string loader, string stem,
        out ModMeta meta, out string iconHint, out string modId)
    {
        meta = new ModMeta(stem, string.Empty, string.Empty, string.Empty);
        iconHint = string.Empty;
        modId = string.Empty;
        if (json.Length == 0)
        {
            return false;
        }
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                return false;
            }

            modId = Str(root["id"]);
            var name = Str(root["name"]);
            if (name.Length == 0)
            {
                name = modId;
            }
            if (name.Length == 0)
            {
                name = stem;
            }
            iconHint = IconPath(root["icon"]);
            meta = new ModMeta(name, Str(root["version"]), loader, Authors(root["authors"]));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryQuilt(string json, string stem,
        out ModMeta meta, out string iconHint, out string modId)
    {
        meta = new ModMeta(stem, string.Empty, string.Empty, string.Empty);
        iconHint = string.Empty;
        modId = string.Empty;
        if (json.Length == 0)
        {
            return false;
        }
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root
                || root["quilt_loader"] is not JsonObject loader)
            {
                return false;
            }

            var metadata = loader["metadata"] as JsonObject;
            modId = Str(loader["id"]);
            var name = Str(metadata?["name"]);
            if (name.Length == 0)
            {
                name = modId;
            }
            if (name.Length == 0)
            {
                name = stem;
            }
            iconHint = IconPath(metadata?["icon"]);

            // quilt 的作者写在 metadata.contributors（对象：名字 → 角色）
            meta = new ModMeta(name, Str(loader["version"]), "Quilt", Authors(metadata?["contributors"]));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 从 `mods.toml` / `neoforge.mods.toml` 里抠第一个 `[[mods]]` 块。
    ///
    /// ⚠️ 不引 TOML 库（这个工程零 NuGet）：只认这几种写法，够读了 ——
    ///    `key = "值"` / `key = '值'` / `key = 值`，`#` 开头是注释。
    ///    多 mod 的 jar（jar-in-jar 那种）只取**第一个** `[[mods]]`，那是主 mod。
    /// </summary>
    private static (ModMeta Meta, string IconHint, string ModId) FromToml(
        string text, string loader, string stem, string jarVersion)
    {
        var map = TomlModSection(text);
        var modId = Get(map, "modId");
        var name = Get(map, "displayName");
        if (name.Length == 0)
        {
            name = modId;
        }
        if (name.Length == 0)
        {
            name = stem;
        }

        // Forge 的 version 经常写成 `${file.jarVersion}`（由 Gradle 在打包时展开，
        // 到了用户机器上就靠清单里的 Implementation-Version 顶）
        var version = ResolveTemplate(Get(map, "version"), jarVersion);
        var iconHint = NormalizeIconPath(Get(map, "logoFile"));
        return (new ModMeta(name, version, loader, Get(map, "authors")), iconHint, modId);
    }

    private static bool TryMcModInfo(string json, string stem,
        out ModMeta meta, out string iconHint, out string modId)
    {
        meta = new ModMeta(stem, string.Empty, string.Empty, string.Empty);
        iconHint = string.Empty;
        modId = string.Empty;
        if (json.Length == 0)
        {
            return false;
        }
        try
        {
            var first = JsonNode.Parse(json) switch
            {
                JsonArray array when array.Count > 0 => array[0] as JsonObject,
                JsonObject single => single,
                _ => null,
            };
            if (first is null)
            {
                return false;
            }

            modId = Str(first["modid"]);
            var name = Str(first["name"]);
            if (name.Length == 0)
            {
                name = modId;
            }
            if (name.Length == 0)
            {
                name = stem;
            }
            iconHint = NormalizeIconPath(Str(first["logoFile"]));

            // 1.12 用 authorList，1.7 用 authors
            var authors = Authors(first["authorList"]);
            if (authors.Length == 0)
            {
                authors = Authors(first["authors"]);
            }
            meta = new ModMeta(name, Str(first["version"]), "Forge", authors);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ---------- 图标 ----------

    /// <summary>
    /// 找图标：先看元数据指的那张，再按 <see cref="IconFallbacks"/> 猜，
    /// 最后试 `assets/&lt;modid&gt;/icon.png`。读不到就是 null。
    /// </summary>
    private static byte[]? ReadIcon(ZipArchive zip, string iconHint, string modId)
    {
        var candidates = new List<string>();
        if (iconHint.Length > 0)
        {
            candidates.Add(iconHint);
        }
        candidates.AddRange(IconFallbacks);
        if (modId.Length > 0)
        {
            candidates.Add($"assets/{modId}/icon.png");
        }

        foreach (var name in candidates)
        {
            try
            {
                var entry = zip.GetEntry(name);
                if (entry is null || entry.Length <= 0 || entry.Length > MaxIconBytes)
                {
                    continue;
                }
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
            catch (Exception ex) when (ex is IOException
                                           or InvalidDataException
                                           or UnauthorizedAccessException
                                           or NotSupportedException)
            {
                // 这张读不了就试下一张
            }
        }
        return null;
    }

    /// <summary>
    /// `fabric.mod.json` 的 `icon` 有两种写法：字符串，或者 `{"64": "…", "128": "…"}`。
    /// 取第一种非空的（尺寸随便，列表里反正缩到 32）。
    /// </summary>
    private static string IconPath(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                return NormalizeIconPath(text);

            case JsonObject table:
                foreach (var pair in table)
                {
                    var path = NormalizeIconPath(Str(pair.Value));
                    if (path.Length > 0)
                    {
                        return path;
                    }
                }
                break;
        }
        return string.Empty;
    }

    /// <summary>zip 里的条目名不带开头的 `/`，但 mod 的元数据里经常写成 `/assets/…`。</summary>
    private static string NormalizeIconPath(string? path)
    {
        var text = (path ?? string.Empty).Trim();
        return text.TrimStart('/');
    }

    // ---------- 小工具 ----------

    private static Dictionary<string, string> TomlModSection(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var inside = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            if (line.StartsWith("[[", StringComparison.Ordinal))
            {
                inside = line.StartsWith("[[mods]]", StringComparison.Ordinal);
                continue;
            }
            if (line[0] == '[')
            {
                inside = false;
                continue;
            }
            if (!inside)
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            map[line[..eq].Trim()] = Unquote(line[(eq + 1)..].Trim());
        }
        return map;
    }

    /// <summary>去掉两头的引号（单引号 / 双引号都行）。</summary>
    private static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            var first = value[0];
            if ((first == '"' || first == '\'') && value[^1] == first)
            {
                return value[1..^1];
            }
        }
        return value;
    }

    /// <summary>`${file.jarVersion}` → 清单里的版本号；没有就空串（别把占位符显示给用户）。</summary>
    private static string ResolveTemplate(string version, string jarVersion)
    {
        if (!version.Contains("${", StringComparison.Ordinal))
        {
            return version;
        }
        if (jarVersion.Length == 0)
        {
            return string.Empty;
        }
        return version.Replace("${file.jarVersion}", jarVersion, StringComparison.Ordinal);
    }

    private static string Get(Dictionary<string, string> map, string key)
        => map.TryGetValue(key, out var value) ? value : string.Empty;

    /// <summary>作者：数组（字符串 / `{"name":…}`）或对象（quilt 的 contributors）都认。</summary>
    private static string Authors(JsonNode? node)
    {
        var names = new List<string>();
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    var text = item is JsonObject obj ? Str(obj["name"]) : Str(item);
                    if (text.Length > 0)
                    {
                        names.Add(text);
                    }
                }
                break;

            case JsonObject table:
                // quilt：{ "作者名": "角色", … }
                foreach (var pair in table)
                {
                    if (pair.Key.Length > 0)
                    {
                        names.Add(pair.Key);
                    }
                }
                break;

            default:
                var single = Str(node);
                if (single.Length > 0)
                {
                    names.Add(single);
                }
                break;
        }
        return string.Join(", ", names);
    }

    private static string Str(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text.Trim()
            : string.Empty;

    private static string ReadText(ZipArchive zip, string entryName)
    {
        try
        {
            var entry = zip.GetEntry(entryName);
            if (entry is null || entry.Length <= 0 || entry.Length > MaxMetaBytes)
            {
                return string.Empty;
            }
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException
                                       or InvalidDataException
                                       or UnauthorizedAccessException
                                       or NotSupportedException)
        {
            return string.Empty;
        }
    }

    /// <summary>从 MANIFEST.MF 里取一个头（`Implementation-Version` 这些）。</summary>
    private static string ManifestValue(string manifest, string key)
    {
        if (manifest.Length == 0)
        {
            return string.Empty;
        }
        foreach (var raw in manifest.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }
            if (line[..colon].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return line[(colon + 2)..].Trim();
            }
        }
        return string.Empty;
    }
}
