namespace Mosslight.App.Views;

/// <summary>
/// 中文关键词 → Modrinth 上认的英文名。
///
/// ## 为什么需要这张表（2026-10 用户报的"有些模组中文搜不出来"）
/// Modrinth 的搜索是**纯文本**匹配标题和摘要，而这两样只有英文（它的索引里没有中文名）。
/// 所以「机械动力」在 Modrinth 上永远搜不到「Create」—— 这不是我们的 bug，是对方没有中文索引。
/// 启动器能做的是：先查一张**本地别名表**，命中就用英文名去搜。
///
/// ⚠️ 这张表是**人工维护**的：只在"某个 Modrinth 项目的中文名在中文圈已经固定"时才加，
///    拿不准的宁可不加 —— 搜错的结果比搜不到更气人。
/// ⚠️ 只对**纯中文/混合关键词**生效；英文关键词一律原样搜（见 <see cref="TryTranslate"/>）。
/// ⚠️ 匹配是"最长命中"，所以「机械动力」和「机械动力：创世神」这种前缀关系不会互相抢。
/// </summary>
internal static class ModSearchAliases
{
    /// <summary>(中文名, Modrinth 上搜得到的英文名)。按中文名长度无关，匹配时自己挑最长的。</summary>
    private static readonly (string Chinese, string English)[] Table =
    {
        // ---- 优化 / 基础库 ----
        ("钠", "Sodium"),
        ("铟", "Indium"),
        ("锂", "Lithium"),
        ("苹果皮", "AppleSkin"),
        ("地毯", "Carpet"),
        ("连接纹理", "Continuity"),
        ("实体剔除", "Entity Culling"),
        ("区块预加载", "Chunky"),
        ("优化", "ModernFix"),

        // ---- 大型内容模组 ----
        ("机械动力", "Create"),
        ("农夫乐事", "Farmer's Delight"),
        ("暮色森林", "The Twilight Forest"),
        ("植物魔法", "Botania"),
        ("通用机械", "Mekanism"),
        ("工业时代", "IndustrialCraft"),
        ("神秘时代", "Thaumcraft"),
        ("匠魂", "Tinkers Construct"),
        ("精致存储", "Refined Storage"),
        ("应用能源", "Applied Energistics"),
        ("储物抽屉", "Storage Drawers"),
        ("建筑工艺", "BuildCraft"),
        ("沉浸工程", "Immersive Engineering"),
        ("热力膨胀", "Thermal Expansion"),
        ("等价交换", "ProjectE"),
        ("龙之研究", "Draconic Evolution"),
        ("无尽贪婪", "Avaritia"),
        ("星辉魔法", "Astral Sorcery"),
        ("血魔法", "Blood Magic"),
        ("冰与火之歌", "Ice and Fire"),
        ("深渊国度", "AbyssalCraft"),

        // ---- 工具 / 界面 / 辅助 ----
        ("创世神", "WorldEdit"),
        ("小地图", "JourneyMap"),
        ("高清修复", "OptiFine"),
        ("简易语音聊天", "Simple Voice Chat"),
        ("幸运方块", "Lucky Block"),
        ("旅行者背包", "Traveler's Backpack"),
        ("饰品", "Curios"),
        ("地形", "Terralith"),
        ("东方女仆", "Touhou Little Maid"),
        ("管道", "Pipez"),
    };

    /// <summary>关键词里有没有汉字（没有就没必要查表，英文名直接搜）。</summary>
    public static bool ContainsCjk(string text)
    {
        foreach (var ch in text)
        {
            // CJK 统一表意文字（常用区）+ 扩展 A
            if (ch is >= '\u4E00' and <= '\u9FFF' or >= '\u3400' and <= '\u4DBF')
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 把中文关键词换成英文名。
    ///
    /// 命中规则：先把两边的空白去掉比**完全相等**；不中再按"表里的中文名是关键词的子串"找，
    /// 多个命中取**最长**的那个（「机械动力」优先于「机械」这类前缀）。
    /// </summary>
    public static bool TryTranslate(string query, out string english)
    {
        english = string.Empty;
        var text = (query ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        (string Chinese, string English)? best = null;
        foreach (var entry in Table)
        {
            if (string.Equals(text, entry.Chinese, StringComparison.Ordinal))
            {
                best = entry;
                break;
            }
            if (text.Contains(entry.Chinese, StringComparison.Ordinal)
                && (best is null || entry.Chinese.Length > best.Value.Chinese.Length))
            {
                best = entry;
            }
        }

        if (best is not { } hit)
        {
            return false;
        }

        english = hit.English;
        return true;
    }
}
