namespace Mosslight.App.Views;

/// <summary>「关于 / 特别鸣谢」里的一条（照 PCL2 的「关于与鸣谢」那一页）。</summary>
public sealed record CreditEntry(
    string Name,
    string Description,
    string Avatar,
    string? ButtonLabel = null,
    string? Url = null,
    string Icon = "");

/// <summary>「关于」和「特别鸣谢」两张名单。</summary>
public static class Credits
{
    /// <summary>赞助页（爱发电）。</summary>
    public const string SponsorUrl = "https://afdian.com/a/Mosslight_Launcher";

    /// <summary>
    /// 「关于」：作者 → 协助宣传 → 本软件（PCL 也是这个顺序：人排在软件前面）；⚠️ 头像都是 128×
    /// 128 PNG，放在 `Assets\credits\`（内嵌，不联网取）。
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
    /// 「特别鸣谢」：给了镜像、工具、翻译、测试的每一位；⚠️ 头像都是 128×128 的 PNG，放在
    /// `Assets\credits\`（**嵌进程序集，不联网取**）：隐私说明里写着"不向作者发送任何东西"，为了
    /// 几个 logo 去联网既破例、又容易挂。
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
    /// 「更多 → 帮助与反馈」那几条：形状跟名单一样（头像 / 名字 / 说明 / 右侧按钮），所以复用同一
    /// 个行模板。
    /// </summary>
    public static readonly IReadOnlyList<CreditEntry> Links = new[]
    {
        new CreditEntry(
            Name: "帮助与常见问题",
            Description: "安装、启动和第三方登录的常见问题",
            Avatar: string.Empty,
            ButtonLabel: "打开 README",
            Url: "https://github.com/kongxia114/Mosslight-Launcher#readme",
            Icon: "help"),
        new CreditEntry(
            Name: "反馈问题",
            Description: "遇到问题就开一个 Bug 报告，模板会提示你贴哪些日志",
            Avatar: string.Empty,
            ButtonLabel: "提交 Bug",
            Url: "https://github.com/kongxia114/Mosslight-Launcher/issues/new?template=bug_report.yml",
            Icon: "feedback"),
        new CreditEntry(
            Name: "功能建议",
            Description: "想要什么新功能，直接开一个功能请求",
            Avatar: string.Empty,
            ButtonLabel: "提建议",
            Url: "https://github.com/kongxia114/Mosslight-Launcher/issues/new?template=feature_request.yml",
            Icon: "idea"),
        new CreditEntry(
            Name: "新功能投票",
            Description: "想要的功能可以在这里投票",
            Avatar: string.Empty,
            ButtonLabel: "打开 Discussions",
            Url: "https://github.com/kongxia114/Mosslight-Launcher/discussions",
            Icon: "idea"),
    };
}
