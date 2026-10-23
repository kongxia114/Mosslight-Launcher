using Mosslight.App.Infrastructure;
using Mosslight.Core.Localization;

namespace Mosslight.App.Views;

/// <summary>
/// 一条 Modrinth 版本装下去之后**落到哪儿** —— 决定走哪个服务、详情页底部那句话怎么写。
/// </summary>
public enum ModInstallTarget
{
    /// <summary>游戏目录下的固定子目录（`mods` / `resourcepacks` / `shaderpacks`）。</summary>
    ContentDir,

    /// <summary>
    /// 整合包：**不落在某个 mods 目录里**，而是装成一个独立实例 （`versions/&lt;整合包名&gt;/`）。
    /// </summary>
    ModpackInstance,

    /// <summary>数据包：落点是**某一个存档**的 `saves/&lt;存档&gt;/datapacks/`。</summary>
    WorldDatapacks,
}

/// <summary>「社区资源」里一栏的全部差异。</summary>
public sealed record ModContentType(
    string ProjectType,
    string NavKey,
    string Title,
    string Subtitle,
    bool UsesLoader,
    bool ShowsLoaderFilter,
    ModInstallTarget Target)
{
    /// <summary>模组：唯一要按加载器筛的一栏，也是唯一装进 `mods/` 的一栏。</summary>
    public static readonly ModContentType Mod = new(
        ProjectType: "mod",
        NavKey: "mods",
        Title: "模组",
        Subtitle: "搜索模组 / 光影 / 资源包…",
        UsesLoader: true,
        ShowsLoaderFilter: true,
        Target: ModInstallTarget.ContentDir);

    /// <summary>整合包：走已有的 `.mrpack` 服务，落成独立实例。</summary>
    public static readonly ModContentType Modpack = new(
        ProjectType: "modpack",
        NavKey: "modpacks",
        Title: "整合包",
        Subtitle: "搜索整合包…",
        UsesLoader: false,
        ShowsLoaderFilter: true,
        Target: ModInstallTarget.ModpackInstance);

    /// <summary>数据包：落点由用户在下载时选（某个存档的 datapacks/ 目录）。</summary>
    public static readonly ModContentType Datapack = new(
        ProjectType: "datapack",
        NavKey: "datapacks",
        Title: "数据包",
        Subtitle: "搜索数据包…",
        UsesLoader: false,
        ShowsLoaderFilter: false,
        Target: ModInstallTarget.WorldDatapacks);

    /// <summary>资源包：落 `&lt;游戏目录&gt;/resourcepacks/`。</summary>
    public static readonly ModContentType ResourcePack = new(
        ProjectType: "resourcepack",
        NavKey: "resourcepacks",
        Title: "资源包",
        Subtitle: "搜索资源包…",
        UsesLoader: false,
        ShowsLoaderFilter: false,
        Target: ModInstallTarget.ContentDir);

    /// <summary>光影包：落 `&lt;游戏目录&gt;/shaderpacks/`。</summary>
    public static readonly ModContentType Shader = new(
        ProjectType: "shader",
        NavKey: "shaders",
        Title: "光影包",
        Subtitle: "搜索光影包…",
        UsesLoader: false,
        ShowsLoaderFilter: false,
        Target: ModInstallTarget.ContentDir);

    /// <summary>五栏都在这里（顺序 = 左栏的顺序）。</summary>
    public static readonly IReadOnlyList<ModContentType> All = new[]
    {
        Mod, Modpack, Datapack, ResourcePack, Shader,
    };

    /// <summary>本地化过的类型名（「资源包」/「Resource packs」）。</summary>
    public string Label => Localizer.Tr(Title);

    /// <summary>项目图标没下来时用的那张内置方块图。</summary>
    public string FallbackIcon => ModIcons.FallbackNameFor(ProjectType);

    /// <summary>按 Modrinth 的 `project_type` 找（认不出来返回 null）。</summary>
    public static ModContentType? ForProjectType(string? projectType)
        => All.FirstOrDefault(t => string.Equals(t.ProjectType, projectType, StringComparison.OrdinalIgnoreCase));

    /// <summary>按下载页左栏的 key 找（认不出来返回 null —— 那一栏就该走占位页）。</summary>
    public static ModContentType? ForNavKey(string? navKey)
        => All.FirstOrDefault(t => string.Equals(t.NavKey, navKey, StringComparison.Ordinal));
}
