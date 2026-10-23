using System.Text.Json.Nodes;

namespace Mosslight.Core.Services.Engine;

/// <summary>详情页里的**一个版本分组**：一个「游戏版本 × 加载器 × 正式/预览」的组合。</summary>
public sealed record ModVersionGroup(
    string GameVersion,
    string Loader,
    bool Prerelease,
    IReadOnlyList<JsonObject> Versions,
    bool MatchesFilter);

public static class ModVersionGroups
{
    // 加载器的显示名（产品名，不翻译）。
    private static readonly Dictionary<string, string> LoaderLabels = new(StringComparer.Ordinal)
    {
        ["fabric"] = "Fabric",
        ["forge"] = "Forge",
        ["neoforge"] = "NeoForge",
        ["quilt"] = "Quilt",
        ["liteloader"] = "LiteLoader",
        ["rift"] = "Rift",
    };

    // 排序时加载器的先后（越小越靠前）。
    private static readonly Dictionary<string, int> LoaderOrder = new(StringComparer.Ordinal)
    {
        ["neoforge"] = 0,
        ["fabric"] = 1,
        ["forge"] = 2,
        ["quilt"] = 3,
    };

    /// <summary>是**用户在下拉里选的**筛选条件，传「全部」等于没筛。</summary>
    public static List<ModVersionGroup> Group(
        IEnumerable<JsonObject>? versions,
        string preferVersion = "",
        string preferLoader = "",
        bool splitByLoader = true)
    {
        var wantVersion = Real(preferVersion);
        var wantLoader = Real(preferLoader);
        var buckets = new Dictionary<(string Game, string Loader, bool Pre), List<JsonObject>>();

        foreach (var version in versions ?? Enumerable.Empty<JsonObject>())
        {
            var type = version["version_type"]?.GetValue<string>() ?? "release";
            var prerelease = type is "beta" or "alpha";

            var gameVersions = (version["game_versions"] as JsonArray ?? new JsonArray())
                .Select(x => x?.GetValue<string>() ?? string.Empty)
                .Where(x => x.Length > 0)
                .ToList();
            var loaders = (version["loaders"] as JsonArray ?? new JsonArray())
                .Select(x => x?.GetValue<string>() ?? string.Empty)
                .Where(x => x.Length > 0)
                .ToList();

            foreach (var game in gameVersions)
            {
                // ⚠️ 一个版本可能连 `loaders` 都没有（老版本 / 数据不全）：
                // 那样也得成组，否则这些版本在界面上**永远看不到**。
                foreach (var loader in splitByLoader && loaders.Count > 0 ? loaders : new List<string> { string.Empty })
                {
                    var key = (game, loader, prerelease);
                    if (!buckets.TryGetValue(key, out var list))
                    {
                        list = new List<JsonObject>();
                        buckets[key] = list;
                    }
                    list.Add(version);
                }
            }
        }

        var groups = new List<ModVersionGroup>(buckets.Count);
        foreach (var ((game, loader, prerelease), items) in buckets)
        {
            var sorted = items
                .OrderByDescending(v => v["date_published"]?.GetValue<string>() ?? string.Empty,
                    StringComparer.Ordinal)
                .ToList();

            var matched = wantVersion.Length > 0
                          && string.Equals(game, wantVersion, StringComparison.Ordinal)
                          && (wantLoader.Length == 0
                              || string.Equals(loader, wantLoader, StringComparison.OrdinalIgnoreCase));

            groups.Add(new ModVersionGroup(game, loader, prerelease, sorted, matched));
        }

        // ⚠️ 用 OrderBy（**稳定排序**）而不是 List.Sort：后者的顺序对相等元素不保证，
        // 会让同样的数据两次排出来的次序不一样。
        return groups.OrderBy(g => g, Comparer<ModVersionGroup>.Create(Compare)).ToList();
    }

    public static string Title(ModVersionGroup group)
    {
        var loader = LoaderLabels.TryGetValue(group.Loader, out var label)
            ? label
            : group.Loader;

        var text = loader.Length > 0
            ? $"{loader} {group.GameVersion}"
            : group.GameVersion;

        if (!group.Prerelease)
        {
            return text;
        }

        // 一组里可能混着 beta 和 alpha：按**最新那个版本**的类型标（组内本来就是新的在前）
        var raw = RawTypeOf(group);
        return text + " " + (raw.Length > 0 ? raw : "beta");
    }

    /// <summary>
    /// 这一组的原始 `version_type`（`release` / `beta` / `alpha`）认不出来返回空串。
    /// </summary>
    private static string RawTypeOf(ModVersionGroup group)
    {
        try
        {
            return group.Versions.Count > 0
                   && group.Versions[0]["version_type"]?.GetValue<string>() is { } raw
                ? raw
                : string.Empty;
        }
        catch (InvalidOperationException)
        {
            // version_type 不是字符串（接口换过字段类型）：别让它把整页带崩
            return string.Empty;
        }
    }

    // 内部

    /// <summary>「全部」/ 空白 → 空串（= 没筛）。</summary>
    private static string Real(string? want)
    {
        var text = (want ?? string.Empty).Trim();
        return string.Equals(text, ModrinthApi.AnyOption, StringComparison.Ordinal) ? string.Empty : text;
    }

    /// <summary>`1.20.1` → `[1, 20, 1]`。</summary>
    private static int[] Numbers(string? text)
    {
        var parts = (text ?? string.Empty).Split('.');
        var result = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            var digits = new string(part.Where(char.IsDigit).ToArray());
            result.Add(digits.Length > 0 && int.TryParse(digits, out var n) ? n : 0);
        }
        return result.Count > 0 ? result.ToArray() : new[] { 0 };
    }

    private static int Compare(ModVersionGroup a, ModVersionGroup b)
    {
        // 1. 对得上筛选的排最前
        if (a.MatchesFilter != b.MatchesFilter)
        {
            return a.MatchesFilter ? -1 : 1;
        }

        // 2. 游戏版本新的在前
        var cmp = LoaderCatalog.CompareVersionKeys(Numbers(b.GameVersion), Numbers(a.GameVersion));
        if (cmp != 0)
        {
            return cmp;
        }

        // 3. 正式版在预览版前面
        if (a.Prerelease != b.Prerelease)
        {
            return a.Prerelease ? 1 : -1;
        }

        // 4. 加载器按常用度
        var orderA = LoaderOrder.TryGetValue(a.Loader, out var oa) ? oa : 99;
        var orderB = LoaderOrder.TryGetValue(b.Loader, out var ob) ? ob : 99;
        if (orderA != orderB)
        {
            return orderA.CompareTo(orderB);
        }

        // 5. 兜底：版本号字符串（保证顺序稳定）
        return string.CompareOrdinal(a.GameVersion, b.GameVersion);
    }
}
