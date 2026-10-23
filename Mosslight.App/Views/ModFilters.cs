using Mosslight.Core.Localization;
using Mosslight.Core.Services.Engine;

namespace Mosslight.App.Views;

/// <summary>搜索卡上一个下拉项：**接口值** + **显示文案**。</summary>
public sealed record ModFilterOption(string Value, string Label, bool Translate = true);

/// <summary>搜索卡那四个下拉的**候选表**。</summary>
public static class ModFilters
{
    /// <summary>「全部」—— 不给 facets 加任何条件。</summary>
    public const string Any = ModrinthApi.AnyOption;

    /// <summary>排序的默认值（Modrinth 的 `relevance`）。</summary>
    public const string DefaultSort = ModrinthApi.DefaultSort;

    public static readonly IReadOnlyList<ModFilterOption> Sorts = new ModFilterOption[]
    {
        new("relevance", "相关度"),
        new("downloads", "下载量"),
        new("follows", "关注数"),
        new("newest", "最新发布"),
        new("updated", "最近更新"),
    };

    /// <summary>
    /// 来源（下载源）；⚠️ **A 方案：现在只有「全部 / Modrinth」两档**—— 「全部」由 `ModsPage` 铺
    /// 下拉时统一放第一项（includeAny），这张表里只列**具体来源**。
    /// </summary>
    public static readonly IReadOnlyList<ModFilterOption> Sources = new ModFilterOption[]
    {
        new("modrinth", "Modrinth", Translate: false),
    };

    /// <summary>加载器；⚠️ 搜索卡目前没有这个下拉，表暂时没人用，但按加载器筛迟早要回来。</summary>
    public static readonly IReadOnlyList<ModFilterOption> Loaders = new ModFilterOption[]
    {
        new("fabric", "Fabric", Translate: false),
        new("forge", "Forge", Translate: false),
        new("neoforge", "NeoForge", Translate: false),
        new("quilt", "Quilt", Translate: false),
    };

    public static readonly IReadOnlyList<string> FallbackVersions = new[]
    {
        "1.21.5", "1.21.4", "1.21.3", "1.21.2", "1.21.1", "1.21",
        "1.20.6", "1.20.4", "1.20.2", "1.20.1", "1.20",
        "1.19.4", "1.19.2", "1.19",
        "1.18.2", "1.18.1",
        "1.17.1",
        "1.16.5", "1.16.4",
        "1.15.2", "1.14.4",
        "1.12.2", "1.7.10",
    };

    public static IReadOnlyList<ModFilterOption> CategoriesFor(string projectType) => projectType switch
    {
        "mod" => new ModFilterOption[]
        {
            new("adventure", "冒险"), new("cursed", "诅咒"), new("decoration", "装饰"),
            new("economy", "经济"), new("equipment", "装备"), new("food", "食物"),
            new("game-mechanics", "游戏机制"), new("library", "支持库"), new("magic", "魔法"),
            new("management", "管理"), new("minigame", "小游戏"), new("mobs", "生物"),
            new("optimization", "性能优化"), new("social", "社交"), new("storage", "存储"),
            new("technology", "科技"), new("transportation", "交通"), new("utility", "实用工具"),
            new("worldgen", "世界生成"),
        },
        "modpack" => new ModFilterOption[]
        {
            new("adventure", "冒险"), new("challenging", "挑战"), new("combat", "战斗"),
            new("kitchen-sink", "大杂烩"), new("lightweight", "轻量"), new("magic", "魔法"),
            new("multiplayer", "多人"), new("optimization", "性能优化"), new("quests", "任务"),
            new("technology", "科技"),
        },
        "resourcepack" => new ModFilterOption[]
        {
            new("combat", "战斗"), new("cursed", "诅咒"), new("decoration", "装饰"),
            new("modded", "模组化"), new("realistic", "写实"), new("simplistic", "简约"),
            new("themed", "主题"), new("tweaks", "微调"), new("utility", "实用工具"),
            new("vanilla-like", "原版风"),
        },
        // 数据包 / 光影包（以及以后新加的类型）：没有分类这一维
        _ => Array.Empty<ModFilterOption>(),
    };

    // 分类原文 → 中文源文案（给**接口回来的新值**用；兜底表之外的那本账）。
    private static readonly Dictionary<string, string> CategoryLabels = new(StringComparer.Ordinal)
    {
        ["adventure"] = "冒险",
        ["cartoon"] = "卡通",
        ["challenging"] = "挑战",
        ["combat"] = "战斗",
        ["cursed"] = "诅咒",
        ["decoration"] = "装饰",
        ["economy"] = "经济",
        ["equipment"] = "装备",
        ["fantasy"] = "奇幻",
        ["food"] = "食物",
        ["game-mechanics"] = "游戏机制",
        ["kitchen-sink"] = "大杂烩",
        ["library"] = "支持库",
        ["lightweight"] = "轻量",
        ["magic"] = "魔法",
        ["management"] = "管理",
        ["minigame"] = "小游戏",
        ["mobs"] = "生物",
        ["modded"] = "模组化",
        ["multiplayer"] = "多人",
        ["optimization"] = "性能优化",
        ["quests"] = "任务",
        ["realistic"] = "写实",
        ["semi-realistic"] = "半写实",
        ["simplistic"] = "简约",
        ["social"] = "社交",
        ["storage"] = "存储",
        ["technology"] = "科技",
        ["themed"] = "主题",
        ["transportation"] = "交通",
        ["tweaks"] = "微调",
        ["utility"] = "实用工具",
        ["vanilla-like"] = "原版风",
        ["worldgen"] = "世界生成",
    };

    /// <summary>
    /// 一个分类原文的显示文案（认不出来就返回原文，**不返回 null**：调用方不用再判空，而"显示英文
    /// "正是我们要的兜底）。
    /// </summary>
    public static string CategoryLabel(string slug)
        => CategoryLabels.TryGetValue(slug, out var label) ? Localizer.Tr(label) : slug;

    /// <summary>按接口值找排序的显示名（认不出来返回原文，理由同上）。</summary>
    public static string SortLabel(string index)
    {
        foreach (var option in Sorts)
        {
            if (string.Equals(option.Value, index, StringComparison.Ordinal))
            {
                return Localizer.Tr(option.Label);
            }
        }
        return index;
    }
}
