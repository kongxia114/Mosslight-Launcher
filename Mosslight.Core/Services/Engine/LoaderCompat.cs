namespace Mosslight.Core.Services.Engine;

// 安装页的加载器互斥 / 自动带上规则（照 PCL2 的 PageDownloadInstall）。
// ⚠️ 主加载器互斥；OptiFine 与 NeoForge / LabyMod / Cleanroom 永不兼容；LiteLoader 可与 Forge / OptiFine 共存。
// 自检逐条钉着，改规则前先看 CheckLoaderCompat。

/// <summary>安装页的兼容性规则（纯函数）。</summary>
public static class LoaderCompat
{
    /// <summary>主加载器（这些之间**互斥**：同时只能选一个）。</summary>
    public static readonly string[] PrimaryLoaders =
    {
        "forge", "neoforge", "cleanroom", "fabric", "legacy_fabric", "quilt",
        "optifine", "liteloader", "labymod",
    };

    /// <summary>可选组件（挂在某个主加载器上，不是"另一家加载器"）。</summary>
    public static readonly string[] Addons = { "fabric_api", "legacy_fabric_api", "qfapi", "optifabric" };

    /// <summary>这个 key 是不是主加载器。</summary>
    public static bool IsPrimary(string? key)
        => key is not null && PrimaryLoaders.Contains((key ?? string.Empty).Trim().ToLowerInvariant());

    /// <summary>展示名（加了几种"不是加载器"的组件）。</summary>
    public static string Title(string? key)
    {
        var k = (key ?? string.Empty).Trim().ToLowerInvariant();
        return k switch
        {
            "fabric_api" => "Fabric API",
            "legacy_fabric_api" => "Legacy Fabric API",
            "qfapi" => "QFAPI / QSL",
            "optifabric" => "OptiFabric",
            _ => LoaderCatalog.LoaderNames.TryGetValue(k, out var name) ? name : k,
        };
    }

    /// <summary>
    /// 这一家在**这个游戏版本**上有没有意义（没有意义就整行不显示，跟 PCL 一样）；⚠️ 这是"该不该
    /// 出现"，不是"有没有版本"：出现了但列表是空的，界面说"无可用版本"（见
    /// `DownloadPage.LoaderNote`）。
    /// </summary>
    public static bool IsApplicable(string key, string mcVersion)
    {
        var k = (key ?? string.Empty).Trim().ToLowerInvariant();
        var mc = (mcVersion ?? string.Empty).Trim();
        if (mc.Length == 0)
        {
            return false;
        }

        // 加了组件的 key：跟着主加载器走（它们自己的显隐在界面里按"有没有选中"决定）
        if (k is "fabric_api" or "legacy_fabric_api" or "qfapi" or "optifabric")
        {
            return true;
        }

        var atLeast = Compare(mc, "1.0") >= 0;
        return k switch
        {
            "forge" => Compare(mc, "1.1") >= 0,
            "neoforge" => Compare(mc, "1.20.1") >= 0,
            "optifine" => Compare(mc, "1.7.2") >= 0,
            "fabric" => Compare(mc, "1.14") >= 0,
            "legacy_fabric" => Compare(mc, "1.3") >= 0 && Compare(mc, "1.13.2") <= 0,
            "liteloader" => Compare(mc, "1.5.2") >= 0 && Compare(mc, "1.12.2") <= 0,
            "cleanroom" => string.Equals(mc, "1.12.2", StringComparison.Ordinal),
            "labymod" => Compare(mc, "1.8.9") >= 0,
            "quilt" => Compare(mc, "1.14") >= 0,
            _ => atLeast,
        };
    }

    /// <summary>key 这一项现在能不能选；不能的话返回**给人看的原因**。</summary>
    public static CompatReason? IncompatibilityReason(
        string key, string? selectedPrimary, string mcVersion)
    {
        var k = (key ?? string.Empty).Trim().ToLowerInvariant();
        var sel = (selectedPrimary ?? string.Empty).Trim().ToLowerInvariant();
        var mc = (mcVersion ?? string.Empty).Trim();

        // 自己 / 没选：永远可以
        if (k.Length == 0 || sel.Length == 0 || k == sel)
        {
            return null;
        }

        // 可选组件：各有各的依附对象
        if (k is "fabric_api" or "legacy_fabric_api" or "qfapi" or "optifabric")
        {
            return k switch
            {
                // Fabric API：Fabric 原生能装，Quilt 也能用（Quilt 自己那套是 QFAPI/QSL）
                "fabric_api" => sel is "fabric" or "quilt" ? null : CompatReason.Needs("fabric"),
                "legacy_fabric_api" => sel == "legacy_fabric" ? null : CompatReason.Needs("legacy_fabric"),
                "qfapi" => sel == "quilt" ? null : CompatReason.Needs("quilt"),
                _ => sel == "fabric" ? null : CompatReason.Needs("optifine+fabric"),
            };
        }

        // OptiFine 这一侧
        // PCL：与 NeoForge / LabyMod / Cleanroom 永远不兼容；
        // 与 Forge 只在 1.13~1.14.3 不兼容（那几版没有能配的 Forge 补丁）；
        // 与 Fabric 在 1.20.5+ 不兼容（没有 OptiFabric）。
        if (k == "optifine")
        {
            return sel switch
            {
                "neoforge" or "labymod" or "cleanroom" => CompatReason.Incompatible(sel),
                "forge" => Compare(mc, "1.13") >= 0 && Compare(mc, "1.14.3") <= 0
                    ? CompatReason.Incompatible(sel)
                    : null,
                "fabric" => Compare(mc, "1.20.5") >= 0 ? CompatReason.Incompatible(sel) : null,
                "quilt" => Compare(mc, "1.20.5") >= 0 ? CompatReason.Incompatible(sel) : null,
                // LiteLoader 那边允许（见类注释）
                "liteloader" => null,
                _ => CompatReason.Incompatible(sel),
            };
        }

        // OptiFine 已经选中，再去选别的主加载器
        if (sel == "optifine")
        {
            return k switch
            {
                "neoforge" or "labymod" or "cleanroom" => CompatReason.Incompatible(sel),
                "forge" => Compare(mc, "1.13") >= 0 && Compare(mc, "1.14.3") <= 0
                    ? CompatReason.Incompatible(sel)
                    : null,
                "fabric" or "quilt" => Compare(mc, "1.20.5") >= 0 ? CompatReason.Incompatible(sel) : null,
                "liteloader" => null,
                _ => CompatReason.Incompatible(sel),
            };
        }

        // LiteLoader 这一侧
        if (k == "liteloader")
        {
            return sel switch
            {
                "neoforge" or "legacy_fabric" or "labymod" or "cleanroom"
                    => CompatReason.Incompatible(sel),
                // 与 Forge / OptiFine 可以共存
                _ => null,
            };
        }
        if (sel == "liteloader")
        {
            return k switch
            {
                "neoforge" or "legacy_fabric" or "labymod" or "cleanroom"
                    => CompatReason.Incompatible(sel),
                _ => null,
            };
        }

        // 其余主加载器：互斥
        if (IsPrimary(k) && IsPrimary(sel))
        {
            return CompatReason.Incompatible(sel);
        }

        return null;
    }

    /// <summary>
    /// 「与 X 不兼容」/「需要 Y」这两句要**过 Localizer**，所以给出的是 （模板, 参数）而不是拼好
    /// 的中文 —— 直接返回中文的话，另外四种语言永远显示中文，而"与 X 不兼容"这种组合有几十种，语
    /// 言文件里没法穷举。
    /// </summary>
    public static string FormatIncompatible(string otherKey)
        => $"与 {Title(otherKey)} 不兼容";

    /// <summary>「需要 Fabric」这类（PCL 的 `Compat.RequiresFabric`）。</summary>
    public static string Requires(string what)
        => $"需要 {what}";

    public static bool AutoBundledWith(string addonKey, string? primaryKey)
    {
        var addon = (addonKey ?? string.Empty).Trim().ToLowerInvariant();
        var primary = (primaryKey ?? string.Empty).Trim().ToLowerInvariant();
        return addon switch
        {
            "fabric_api" => primary is "fabric" or "quilt",
            "legacy_fabric_api" => primary == "legacy_fabric",
            _ => false,
        };
    }

    /// <summary>
    /// 加载器**自己的安装页**（安装包 → Fabric / Forge / …）一进去先落在哪个游戏版本上。
    /// </summary>
    public static string DefaultGameVersion(string key, IReadOnlyList<string> newestFirstIds)
    {
        if (newestFirstIds is null || newestFirstIds.Count == 0)
        {
            return string.Empty;
        }
        foreach (var id in newestFirstIds)
        {
            if (IsApplicable(key, id))
            {
                return (id ?? string.Empty).Trim();
            }
        }
        return (newestFirstIds[0] ?? string.Empty).Trim();
    }

    /// <summary>版本号比较（`1.14` &lt; `1.14.3` &lt; `1.20.5`）。</summary>
    public static int Compare(string left, string right)
        => LoaderCatalog.CompareVersionKeys(
            LoaderCatalog.VersionKey(left), LoaderCatalog.VersionKey(right));
}

/// <summary>安装页上一行"为什么不能选"（PCL 的 `Load*GetError()` 返回值）。</summary>
public sealed record CompatReason(string Template, string Arg)
{
    /// <summary>「与 {name} 不兼容」。</summary>
    public static CompatReason Incompatible(string loaderKey)
        => new("与 {name} 不兼容", (loaderKey ?? string.Empty).Trim().ToLowerInvariant());

    /// <summary>「需要 {name}」。</summary>
    public static CompatReason Needs(string what)
        => new("需要 {name}", (what ?? string.Empty).Trim().ToLowerInvariant());

    /// <summary>这条原因的显示名（`需要 Fabric + OptiFine` 的那种拼法）。</summary>
    public string ArgTitle => Arg switch
    {
        "optifine+fabric" => "Fabric + OptiFine",
        _ => LoaderCompat.Title(Arg),
    };
}
