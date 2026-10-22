namespace Mosslight.App.Views;

/// <summary>「关于 / 特别鸣谢」里的一条（照 PCL2 的「关于与鸣谢」那一页）。</summary>
/// <param name="Name">人名 / 产品名 —— **不翻译**（跟 Fabric、NeoForge 同一条规矩）。</param>
/// <param name="Description">
/// 一行说明的**中文源文案**（渲染时过 <c>Localizer</c>）。
/// 可以带 <c>{version}</c> 占位符，渲染时换成 <see cref="Core.AppInfo.AppVersion"/>。
/// </param>
/// <param name="Avatar">
/// `Assets\credits\` 下的文件名。**缺图不报错**：画一个圆底 + 首字母兜底
/// （跟 ModIcons 那条"不留空洞"的规矩一致）。
/// </param>
/// <param name="ButtonLabel">按钮文案的**中文源文案**；null = 这条没有按钮。</param>
/// <param name="Url">按钮打开的地址；null = 内部动作（目前只有「检查更新」）。</param>
/// <param name="Icon">
/// 没有头像图时画哪个**矢量图标**（`NavIcons` 的 key，例如 `help` / `feedback`）。
/// 空串 = 退回"名字首字母"。帮助/反馈这几行没有头像，用线性图标比用一个汉字更像样。
/// </param>
public sealed record CreditEntry(
    string Name,
    string Description,
    string Avatar,
    string? ButtonLabel = null,
    string? Url = null,
    string Icon = "");

/// <summary>
/// 「关于」和「特别鸣谢」两张名单。
///
/// ## 为什么是数据表而不是一堆 XAML
/// 加一个人 / 换一张头像 / 改一个链接，都只该动一行数据 —— 界面那边是一个循环。
/// 这跟 `ModContentTypes`、`ModFilters.Sources` 是同一个路子。
///
/// ## 翻译边界
/// 人名、产品名**不进语言文件**（那是商标/人名，译了反而错）；
/// 只有 <see cref="CreditEntry.Description"/> 和 <see cref="CreditEntry.ButtonLabel"/>
/// 会过 <c>Localizer</c>，所以 `check_lang_keys.py` 扫得到它们。
/// </summary>
public static class Credits
{
    /// <summary>赞助页（爱发电）。</summary>
    public const string SponsorUrl = "https://afdian.com/a/Mosslight_Launcher";

    /// <summary>
    /// 「关于」：作者 → 协助宣传 → 本软件（PCL 也是这个顺序：人排在软件前面）。
    ///
    /// ⚠️ 头像都是 128×128 PNG，放在 `Assets\credits\`（内嵌，不联网取）。
    /// ⚠️ 作者行的按钮指向 GitHub 主页，**不是**赞助 —— 赞助那颗在卡片右上角，
    ///    行里再挂一个就重复了（PCL 是因为它那张卡没有右上角按钮才挂在行上）。
    /// </summary>
    public static readonly IReadOnlyList<CreditEntry> About = new[]
    {
        new CreditEntry(
            Name: "神秘纳西妲",
            Description: "Mosslight Launcher 的开发者",
            Avatar: "author.png",
            ButtonLabel: "GitHub 主页",
            Url: "https://github.com/kongxia114"),
        new CreditEntry(
            Name: "空夏114の战雷TV",
            Description: "负责宣传",
            Avatar: "promo.png",
            ButtonLabel: "B 站主页",
            Url: "https://space.bilibili.com/550325373"),
        new CreditEntry(
            Name: Core.AppInfo.AppName,
            Description: "当前版本：{version}",
            Avatar: "app.png",
            ButtonLabel: "检查更新"),
    };

    /// <summary>
    /// 「特别鸣谢」：给了镜像、工具、翻译、测试的每一位。
    ///
    /// ⚠️ 头像都是 128×128 的 PNG，放在 `Assets\credits\`（**嵌进程序集，不联网取**）：
    ///    隐私说明里写着"不向作者发送任何东西"，为了几个 logo 去联网既破例、又容易挂。
    /// ⚠️ 说明和按钮文案是**数据**（变量传给 Localizer），check_lang_keys 扫不到 ——
    ///    所以这几条目前只有中文，加条目时心里有数。
    /// </summary>
    public static readonly IReadOnlyList<CreditEntry> Thanks = new[]
    {
        new CreditEntry(
            Name: "BMCLAPI",
            Description: "提供原版和加载器的下载镜像",
            Avatar: "bmclapi.png",
            ButtonLabel: "访问镜像",
            Url: "https://bmclapi2.bangbang93.com"),
        new CreditEntry(
            Name: "Modrinth",
            Description: "提供社区资源（模组 / 整合包 / 数据包 / 资源包 / 光影）的搜索与下载",
            Avatar: "modrinth.png",
            ButtonLabel: "打开 Modrinth",
            Url: "https://modrinth.com"),
        new CreditEntry(
            Name: "LittleSkin",
            Description: "提供第三方登录与皮肤服务",
            Avatar: "littleskin.png",
            ButtonLabel: "打开 LittleSkin",
            Url: "https://littleskin.cn"),
        new CreditEntry(
            Name: "authlib-injector",
            Description: "第三方账户进游戏时用的认证注入",
            Avatar: "authlib.png",
            ButtonLabel: "GitHub 页面",
            Url: "https://github.com/yushijinhun/authlib-injector"),
        new CreditEntry(
            Name: "Lucide",
            Description: "界面里全部线性图标",
            Avatar: "lucide.png",
            ButtonLabel: "打开 Lucide",
            Url: "https://lucide.dev"),
        new CreditEntry(
            Name: "Plain Craft Launcher 2",
            Description: "界面与交互的参考",
            Avatar: "pcl.png",
            ButtonLabel: "GitHub 页面",
            Url: "https://github.com/Meloong-Git/PCL"),
    };

    /// <summary>
    /// 「更多 → 帮助与反馈」那几条：形状跟名单一样（头像 / 名字 / 说明 / 右侧按钮），
    /// 所以复用同一个行模板。头像留空 → 走首字母。
    /// </summary>
    public static readonly IReadOnlyList<CreditEntry> Links = new[]
    {
        new CreditEntry(
            Name: "帮助与常见问题",
            Description: "安装、启动和第三方登录的常见问题",
            Avatar: string.Empty,
            ButtonLabel: "打开 Wiki",
            Url: "https://github.com/kongxia114/Mosslight-Launcher-2/wiki",
            Icon: "help"),
        new CreditEntry(
            Name: "反馈问题",
            Description: "遇到问题或有建议，去仓库开个 issue",
            Avatar: string.Empty,
            ButtonLabel: "打开 Issues",
            Url: "https://github.com/kongxia114/Mosslight-Launcher-2/issues",
            Icon: "feedback"),
        new CreditEntry(
            Name: "新功能投票",
            Description: "想要的功能可以在这里投票",
            Avatar: string.Empty,
            ButtonLabel: "打开 Discussions",
            Url: "https://github.com/kongxia114/Mosslight-Launcher-2/discussions",
            Icon: "idea"),
    };
}
