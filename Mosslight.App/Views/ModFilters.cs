using Mosslight.Core.Localization;
using Mosslight.Core.Services.Engine;

namespace Mosslight.App.Views;

/// <summary>
/// 搜索卡上一个下拉项：**接口值** + **显示文案**。
///
/// 两者必须分开存：接口要的是 `vanilla-like` / `game-mechanics` 这种原文，
/// 屏幕上要的是「原版风」/「游戏机制」。拿显示文案去发请求的话，
/// Modrinth 会把它当成一个不存在的分类 —— 不报错，只是 0 条结果。
/// </summary>
/// <param name="Value">发给 Modrinth 的原文（排序的话是 `index` 参数的值）。</param>
/// <param name="Label">中文源文案（Localizer 的 key）；<see cref="Translate"/> 为 false 时就是原文。</param>
/// <param name="Translate">
/// 要不要过 <see cref="Localizer"/>。产品名（Fabric / NeoForge）不翻译 ——
/// 那是商标，译了反而是错的（1.0.0 的加载器表也不翻译）。
/// </param>
public sealed record ModFilterOption(string Value, string Label, bool Translate = true);

/// <summary>
/// 搜索卡那四个下拉的**候选表**（= 1.0.0 `mods_page.py` 的 `LOADERS` / `CATEGORIES` / `SORTS`）。
///
/// ## 为什么表在界面层，而不是引擎里
/// 引擎只认接口原文（`fabric` / `worldgen` / `index=downloads`），中文名是**界面的事**。
/// 引擎要是也认识中文名，那两处迟早有一处先改、另一处悄悄对不上。
/// 取数据的接口在 `ModrinthApi`（`CategoriesAsync` / `ReleaseVersionsAsync`），
/// 翻译和兜底在这儿。
///
/// ## 为什么还留一份静态表
/// 那两张表（分类 / 游戏版本）是**问接口要**的 —— 分类会改名、版本会新增。
/// 但网络不通的时候下拉不能是空的（空下拉看着像坏了，用户还会以为是自己选错了），
/// 所以留一份"上次核对过的"表当兜底，接口回来之后就地把内容换掉（见 `ModsPage`）。
/// </summary>
public static class ModFilters
{
    /// <summary>「全部」—— 不给 facets 加任何条件。跟引擎的常量是同一个字符串。</summary>
    public const string Any = ModrinthApi.AnyOption;

    /// <summary>排序的默认值（Modrinth 的 `relevance`）。</summary>
    public const string DefaultSort = ModrinthApi.DefaultSort;

    /// <summary>排序（= 1.0.0 的 `SORTS`）。值是 `index` 参数原文。</summary>
    public static readonly IReadOnlyList<ModFilterOption> Sorts = new ModFilterOption[]
    {
        new("relevance", "相关度"),
        new("downloads", "下载量"),
        new("follows", "关注数"),
        new("newest", "最新发布"),
        new("updated", "最近更新"),
    };

    /// <summary>
    /// 加载器（= 1.0.0 的 `LOADERS`，四家都在这儿）。
    /// ⚠️ 只有 mod 和整合包有这一维（见 <see cref="ModContentType.ShowsLoaderFilter"/>）。
    /// </summary>
    public static readonly IReadOnlyList<ModFilterOption> Loaders = new ModFilterOption[]
    {
        new("fabric", "Fabric", Translate: false),
        new("forge", "Forge", Translate: false),
        new("neoforge", "NeoForge", Translate: false),
        new("quilt", "Quilt", Translate: false),
    };

    /// <summary>
    /// 游戏版本的兜底表（= 1.0.0 的 `MC_VERSIONS`，当时核对过的那 23 个）。
    ///
    /// 只在 <see cref="ModrinthApi.ReleaseVersionsAsync"/> 拿不到东西时用 ——
    /// 这张表会越来越旧，所以它**不是**主路径（新版本它里面没有）。
    /// </summary>
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

    /// <summary>
    /// 分类的**兜底表**（按项目类型分；2026-10 照 `/v2/tag/category` 里
    /// `header == "categories"` 那一组逐条核对过）。
    ///
    /// ⚠️ **一栏一张表**，不能共用：拿 mod 的分类去筛资源包会**静默返回 0 条**
    ///    （`categories:technology` 对资源包是个不存在的组合），理由见
    ///    `ModContentType` 的类注释。
    /// ⚠️ **数据包和光影包没有分类这一维**（数据包是 Modrinth tag 表里就是空的；
    ///    光影包按用户要求跟 PCL-CE 对齐，不做分类筛选 —— 光影包的"分类"其实是
    ///    风格/性能档那些标签，拿它们当筛选维对上架量很小的光影包来说只在添乱）。
    ///    这两栏的「分类」下拉会被整格收起来（按 `Count == 0` 判，见 `ModsPage.InitFilters`）。
    /// </summary>
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

    /// <summary>
    /// 分类原文 → 中文源文案（给**接口回来的新值**用；兜底表之外的那本账）。
    ///
    /// 接口回来一堆 slug（`semi-realistic`、`kitchen-sink` …），我们只翻译认识的；
    /// 不认识的**原样显示** —— 显示英文总比把它藏起来好（藏起来等于这个分类不能用）。
    /// </summary>
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
    /// 一个分类原文的显示文案（认不出来就返回原文，**不返回 null**：
    /// 调用方不用再判空，而"显示英文"正是我们要的兜底）。
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
