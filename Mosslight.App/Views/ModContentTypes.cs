using Mosslight.App.Infrastructure;
using Mosslight.Core.Localization;

namespace Mosslight.App.Views;

/// <summary>
/// 一条 Modrinth 版本装下去之后**落到哪儿** —— 决定走哪个服务、详情页底部那句话怎么写。
/// </summary>
public enum ModInstallTarget
{
    /// <summary>
    /// 游戏目录下的固定子目录（`mods` / `resourcepacks` / `shaderpacks`）。
    /// 走 <see cref="Core.Services.IModService"/>，目录由 `ContentDirFor` 算（含版本隔离）。
    /// </summary>
    ContentDir,

    /// <summary>
    /// 整合包：**不落在某个 mods 目录里**，而是装成一个独立实例
    /// （`versions/&lt;整合包名&gt;/`）。走工程已有的 <see cref="Core.Services.IModpackService"/>。
    /// </summary>
    ModpackInstance,

    /// <summary>还没接（数据包：得先知道放进哪一个存档）。界面要**明说**，不给假按钮。</summary>
    NotImplemented,
}

/// <summary>
/// 「社区资源」里一栏的全部差异（= 1.0.0 `mods_page.py` 的 `MOD_TYPES` 那一套）。
///
/// ## 为什么要有这么一张表
/// 五栏（模组 / 整合包 / 数据包 / 资源包 / 光影包）**界面长得一模一样**：
/// 搜索框 + 结果卡 + 详情页 + 版本行 + 「下载」。差别只有五处：
///   1. 发给 Modrinth 的 `project_type`
///   2. 标题 / 副标题 / 搜索框提示 / 空结果文案
///   3. 图标兜底那张内置方块图（`ModIcons.FallbackNameFor`）
///   4. 要不要按加载器筛（**只有 mod 要**，见下）
///   5. 下载落到哪（<see cref="ModInstallTarget"/>）
/// 与其在 `ModsPage` 里塞五个 `if`，不如把差异摆在一张表里 ——
/// 加第六栏时只动这张表，页面代码一行不用改。
///
/// ## ⚠️ 为什么"要不要按加载器筛"不是所有类型都 true
/// 资源包 / 光影包根本没有加载器这一维（Modrinth 给它们的 `loaders` 是
/// `["minecraft"]`）。硬塞 `categories:fabric` 的结果是**几乎把所有东西筛掉**，
/// 而且**不报错**（2026-10 实测：资源包 36437 → 16，光影 983 → 5，
/// 剩下那几条只是恰好也被打了 fabric 分类的）：
///
///     facets=[["project_type:resourcepack"]]                  → 36437
///     facets=[["project_type:resourcepack"],["categories:fabric"]] → 16
///
/// 整合包虽然自己带加载器（fabric / forge / quilt），但那个加载器是**整合包自己声明的**、
/// 装的时候由整合包服务装上 —— 拿用户本地已装的加载器去筛，只会把一半的包藏起来
/// （实测 1262 → 1202，看着不多，但被藏掉的那 60 个用户永远搜不到）。
/// 所以只有 mod 是 true。
///
/// ## 文案
/// <see cref="Title"/> / <see cref="Subtitle"/> 都是**中文源文案**（Localizer 的 key），
/// 语言文件里查得到就显示译文、查不到就显示中文 —— 跟 1.0.0 的 key 体系一致。
/// </summary>
/// <param name="ProjectType">Modrinth 的 `project_type` 原文（不进 facets 以外的任何界面）。</param>
/// <param name="NavKey">下载页左栏那一行的 key（`DownloadPage.Nav` 里的）。</param>
/// <param name="Title">标题（页面顶部那个大字）。</param>
/// <param name="Subtitle">副标题（标题下面那行说明）。</param>
/// <param name="UsesLoader">搜索 / 筛版本时要不要带加载器（只有 mod 是 true，理由见类注释）。</param>
/// <param name="Target">下载落到哪。</param>
public sealed record ModContentType(
    string ProjectType,
    string NavKey,
    string Title,
    string Subtitle,
    bool UsesLoader,
    ModInstallTarget Target)
{
    /// <summary>模组：唯一要按加载器筛的一栏，也是唯一装进 `mods/` 的一栏。</summary>
    public static readonly ModContentType Mod = new(
        ProjectType: "mod",
        NavKey: "mods",
        Title: "模组",
        Subtitle: "搜索模组 / 光影 / 资源包…",
        UsesLoader: true,
        Target: ModInstallTarget.ContentDir);

    /// <summary>整合包：走已有的 `.mrpack` 服务，落成独立实例。</summary>
    public static readonly ModContentType Modpack = new(
        ProjectType: "modpack",
        NavKey: "modpacks",
        Title: "整合包",
        Subtitle: "搜索整合包…",
        UsesLoader: false,
        Target: ModInstallTarget.ModpackInstance);

    /// <summary>数据包：**还没接**（要先知道放进哪个存档，见详情页/左栏的说明）。</summary>
    public static readonly ModContentType Datapack = new(
        ProjectType: "datapack",
        NavKey: "datapacks",
        Title: "数据包",
        Subtitle: "搜索数据包…",
        UsesLoader: false,
        Target: ModInstallTarget.NotImplemented);

    /// <summary>资源包：落 `&lt;游戏目录&gt;/resourcepacks/`。</summary>
    public static readonly ModContentType ResourcePack = new(
        ProjectType: "resourcepack",
        NavKey: "resourcepacks",
        Title: "资源包",
        Subtitle: "搜索资源包…",
        UsesLoader: false,
        Target: ModInstallTarget.ContentDir);

    /// <summary>光影包：落 `&lt;游戏目录&gt;/shaderpacks/`。</summary>
    public static readonly ModContentType Shader = new(
        ProjectType: "shader",
        NavKey: "shaders",
        Title: "光影包",
        Subtitle: "搜索光影包…",
        UsesLoader: false,
        Target: ModInstallTarget.ContentDir);

    /// <summary>五栏都在这里（顺序 = 左栏的顺序）。</summary>
    public static readonly IReadOnlyList<ModContentType> All = new[]
    {
        Mod, Modpack, Datapack, ResourcePack, Shader,
    };

    /// <summary>本地化过的类型名（「资源包」/「Resource packs」）。</summary>
    public string Label => Localizer.Tr(Title);

    /// <summary>
    /// 项目图标没下来时用的那张内置方块图。
    /// ⚠️ 名字必须对上 `Assets\icons\version\` 里真实存在的文件
    ///    （映射表在 <see cref="ModIcons.FallbackNameFor"/>，认不出来的类型回 forge）。
    /// </summary>
    public string FallbackIcon => ModIcons.FallbackNameFor(ProjectType);

    /// <summary>按 Modrinth 的 `project_type` 找（认不出来返回 null）。</summary>
    public static ModContentType? ForProjectType(string? projectType)
        => All.FirstOrDefault(t => string.Equals(t.ProjectType, projectType, StringComparison.OrdinalIgnoreCase));

    /// <summary>按下载页左栏的 key 找（认不出来返回 null —— 那一栏就该走占位页）。</summary>
    public static ModContentType? ForNavKey(string? navKey)
        => All.FirstOrDefault(t => string.Equals(t.NavKey, navKey, StringComparison.Ordinal));
}
