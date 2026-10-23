using System.IO.Compression;
using System.Text.Json.Nodes;
using Mosslight.Core.Localization;

namespace Mosslight.Core.Services.Engine;

/// <summary>一个本地压缩包是什么东西；拖放安装靠这个分流。</summary>
public enum PackKind
{
    /// <summary>认不出来（空文件、不是 zip、里面没有能用的东西）。</summary>
    Unknown = 0,

    /// <summary>Modrinth 整合包（`.mrpack`，里面有 `modrinth.index.json`）。</summary>
    Modrinth,

    /// <summary>CurseForge 整合包（`manifest.json` + `overrides/`）。</summary>
    CurseForge,

    /// <summary>
    /// 覆盖包：只有 `mods/`、`config/` 这类文件，没有清单（PCL 那种"解压进版本"）。
    /// </summary>
    Overlay,
}

/// <summary>
/// 一个压缩包的摘要 —— 给拖放界面显示"这东西是什么、要不要装、装到哪"；⚠️ 这里**不下载任何东西**：
/// CurseForge 的 mod 要靠它的接口换下载地址，那一步在安装阶段做（`CurseForgePacks`），检测阶段
/// 只报"里面有多少个 mod"。
/// </summary>
public sealed record PackSummary(
    PackKind Kind,
    string Name = "",
    string Minecraft = "",
    string Loader = "",
    string LoaderVersion = "",
    int FileCount = 0,
    int OverlayCount = 0,
    IReadOnlyList<string>? Notes = null);

/// <summary>
/// 本地整合包格式识别：按包内清单判定（Modrinth → CurseForge → 覆盖包），扩展名不可信，检测阶段不
/// 下载任何内容。
/// </summary>
public static class PackArchive
{
    /// <summary>CurseForge 清单的文件名。</summary>
    public const string CurseManifestName = "manifest.json";

    /// <summary>
    /// 覆盖包认得的目录名（名单外的文件一律不展开 —— 谁也不知道那个文件该去哪，瞎写进游戏目录比不
    /// 写更糟）。
    /// </summary>
    public static readonly string[] OverlayDirs =
    {
        "mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks",
        "scripts", "kubejs", "customnpcs", "packmenu", "structures",
    };

    // 压进了包里但**不该展开**的杂项（清单本身、导出的文件列表、图标）。
    private static readonly string[] IgnoredRootFiles =
    {
        "manifest.json", "modlist.html", "profileimage.png", "profileimage.jpg",
        "icon.png", "pack.png", "readme.txt", "readme.md", "desktop.ini",
    };

    /// <summary>看一眼这个 zip 是什么。</summary>
    public static PackSummary Detect(byte[]? blob)
    {
        if (blob is null || blob.Length == 0)
        {
            return new PackSummary(PackKind.Unknown,
                Notes: new[] { Localizer.Tr("这个文件是空的。") });
        }
        if ((long)blob.Length > ModpackInstaller.MaxMrpackBytes)
        {
            return new PackSummary(PackKind.Unknown,
                Notes: new[] { Localizer.Tr("这个包太大了，不敢装。") });
        }

        List<string> names;
        try
        {
            using var stream = new MemoryStream(blob, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            names = zip.Entries.Select(e => e.FullName).ToList();
        }
        catch (InvalidDataException)
        {
            return new PackSummary(PackKind.Unknown,
                Notes: new[] { Localizer.Tr("这不是一个有效的 ZIP（下载坏了，或者本来就不是压缩包）。") });
        }

        // 1. Modrinth（`.mrpack`）
        var (indexEntry, baseDir) = ModpackInstaller.FindIndex(names);
        if (indexEntry.Length > 0)
        {
            try
            {
                var plan = ModpackInstaller.PlanFromBytes(blob);
                var notes = new List<string>(plan.Warnings);
                return new PackSummary(
                    PackKind.Modrinth,
                    Name: plan.Name,
                    Minecraft: plan.Minecraft,
                    Loader: plan.Loader.Loader,
                    LoaderVersion: plan.Loader.Version,
                    FileCount: plan.Files.Count,
                    OverlayCount: plan.Overrides.Count,
                    Notes: notes);
            }
            catch (ModpackException ex)
            {
                // 有 index 但读不动 —— 说清楚原因，别退化成"覆盖包"（那会装出半截东西）
                return new PackSummary(PackKind.Unknown,
                    Notes: new[] { Localizer.Tr("这个 .mrpack 的清单读不了：{err}", ("err", ex.Message)) });
            }
        }

        // 2. CurseForge（`manifest.json`）
        var manifest = FindEntry(names, CurseManifestName, baseDir);
        if (manifest.Length > 0)
        {
            var summary = ReadCurseManifest(blob, manifest, baseDir, names);
            if (summary is not null)
            {
                return summary;
            }
        }

        // 3. 覆盖包
        var overlay = BuildOverlayEntries(names, baseDir);
        if (overlay.Count > 0)
        {
            return new PackSummary(
                PackKind.Overlay,
                OverlayCount: overlay.Count,
                Notes: new[] { Localizer.Tr("这个包里没有清单（不是 Modrinth / CurseForge 整合包），会按覆盖包解压。") });
        }

        return new PackSummary(PackKind.Unknown,
            Notes: new[] { Localizer.Tr("这个压缩包里没有能认出来的东西（既没有整合包清单，也没有 mods / config 这类目录）。") });
    }

    /// <summary>
    /// 覆盖包的安装计划：只有 ModpackPlan.Overrides 有意义 （走已有的
    /// ModpackInstaller.ApplyOverrides 去写盘）。
    /// </summary>
    public static ModpackPlan OverlayPlan(byte[] blob, string name)
    {
        using var stream = new MemoryStream(blob, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        var (indexEntry, baseDir) = ModpackInstaller.FindIndex(names);
        _ = indexEntry;
        return new ModpackPlan
        {
            Name = name,
            Overrides = BuildOverlayEntries(names, baseDir),
        };
    }

    // 内部

    /// <summary>覆盖层条目：`[(zip 里的前缀, 解压后的相对路径)]`。</summary>
    private static List<(string Prefix, string Relative)> BuildOverlayEntries(
        IReadOnlyList<string> names, string baseDir)
    {
        var overrides = ModpackInstaller.FindOverrides(names, baseDir);
        if (overrides.Count > 0)
        {
            return overrides;
        }

        var files = names.Where(n => n.Length > 0 && !n.EndsWith('/')).ToList();

        // 套了一层目录？剥掉（`.minecraft/` 和 `minecraft/` 也当一层）
        var stripped = StripCommonRoot(files);

        var result = new List<(string, string)>();
        foreach (var full in stripped)
        {
            var rel = WithoutBase(full, baseDir);
            if (rel.Length == 0 || !ModpackInstaller.IsSafeRelPath(rel))
            {
                continue;
            }

            var head = rel.Split('/', 2)[0];
            if (rel.IndexOf('/') < 0)
            {
                // 根上的散文件：只认那几个纯装饰的（Readme / 图标），别的不知道该放哪
                if (IgnoredRootFiles.Contains(rel.ToLowerInvariant()))
                {
                    continue;
                }
                continue;
            }
            if (!OverlayDirs.Contains(head.ToLowerInvariant()))
            {
                continue;
            }

            // Prefix 要能原样拼回 zip 里的条目名（ApplyOverrides 用 `prefix + "/" + rel` 找条目）
            var prefix = full[..(full.Length - rel.Length)].TrimEnd('/');
            result.Add((prefix, rel));
        }

        return result;
    }

    /// <summary>所有条目都在同一个顶层目录下时，把那一层剥掉（只剥一层）。</summary>
    private static List<string> StripCommonRoot(List<string> files)
    {
        var first = files.FirstOrDefault();
        if (string.IsNullOrEmpty(first))
        {
            return files;
        }

        var slash = first.IndexOf('/');
        if (slash < 0)
        {
            return files;       // 根上就有文件，没套目录
        }

        var root = first[..slash];
        if (OverlayDirs.Contains(root.ToLowerInvariant()))
        {
            return files;       // 这一层是内容目录本身，不能剥
        }

        var prefix = root + "/";
        return files.All(f => f.StartsWith(prefix, StringComparison.Ordinal))
            ? files.Select(f => f[prefix.Length..]).ToList()
            : files;
    }

    /// <summary>去掉 `&lt;某个文件夹&gt;/` 前缀（`.mrpack` 允许把内容套一层）。</summary>
    private static string WithoutBase(string full, string baseDir)
        => baseDir.Length > 0 && full.StartsWith(baseDir + "/", StringComparison.Ordinal)
            ? full[(baseDir.Length + 1)..]
            : full;

    /// <summary>在条目表里找某个文件名（根上优先，`&lt;baseDir&gt;/` 下也算）。</summary>
    private static string FindEntry(IReadOnlyList<string> names, string fileName, string baseDir)
    {
        foreach (var name in names)
        {
            if (name.EndsWith('/'))
            {
                continue;
            }
            var rel = WithoutBase(name, baseDir);
            if (string.Equals(rel, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }
        return string.Empty;
    }

    /// <summary>读 CurseForge 的 `manifest.json`（读不懂返回 null，让调用方继续往下判）。</summary>
    private static PackSummary? ReadCurseManifest(
        byte[] blob, string entryName, string baseDir, IReadOnlyList<string> names)
    {
        JsonObject? manifest;
        try
        {
            using var stream = new MemoryStream(blob, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = zip.GetEntry(entryName);
            if (entry is null)
            {
                return null;
            }
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            manifest = JsonIO.ParseBytes(ms.ToArray()) as JsonObject;
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException
                                      or IOException)
        {
            return null;
        }

        if (manifest is null)
        {
            return null;
        }

        // ⚠️ 别的启动器也有 manifest.json（比如某些导出格式）：认 `manifestType`，
        // 不然会把"随便一个带 manifest 的 zip"当成 CurseForge 整合包。
        var type = manifest["manifestType"]?.GetValue<string>() ?? string.Empty;
        if (!string.Equals(type, "minecraftModpack", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var minecraft = manifest["minecraft"] as JsonObject;
        var mcVersion = minecraft?["version"]?.GetValue<string>() ?? string.Empty;

        // `modLoaders: [{ "id": "neoforge-21.1.100", "primary": true }]` —— 取 primary
        var loaderKey = string.Empty;
        var loaderVersion = string.Empty;
        if (minecraft?["modLoaders"] is JsonArray loaders && loaders.Count > 0)
        {
            var chosen = loaders.OfType<JsonObject>()
                             .FirstOrDefault(l => l["primary"]?.GetValue<bool>() == true)
                         ?? loaders.OfType<JsonObject>().FirstOrDefault();
            var id = chosen?["id"]?.GetValue<string>() ?? string.Empty;
            var dash = id.IndexOf('-');
            if (dash > 0)
            {
                loaderKey = id[..dash].ToLowerInvariant();
                loaderVersion = id[(dash + 1)..];
            }
        }

        var files = (manifest["files"] as JsonArray)?.Count ?? 0;
        var overlayCount = BuildOverlayEntries(names, baseDir).Count;

        return new PackSummary(
            PackKind.CurseForge,
            Name: manifest["name"]?.GetValue<string>() ?? string.Empty,
            Minecraft: mcVersion,
            Loader: loaderKey,
            LoaderVersion: loaderVersion,
            FileCount: files,
            OverlayCount: overlayCount,
            Notes: new[]
            {
                Localizer.Tr("CurseForge 整合包里的 mod **没有直接下载地址**，要用 CurseForge 的接口按 projectID + fileID 换一次。"),
            });
    }
}
