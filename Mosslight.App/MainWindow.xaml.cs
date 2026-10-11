using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Mosslight.App.Infrastructure;
using Mosslight.App.Views;
using Mosslight.Core.Localization;
using Mosslight.Core.Logging;
using Mosslight.Core.Services;
using Mosslight.Core.Settings;

namespace Mosslight.App;

/// <summary>
/// 主窗口：侧边栏（导航 + 当前档案 + 版本号）+ 页面容器。
///
/// 移植自 1.0.0 的 `ui/main_window.py`。那边侧边栏是个独立控件
/// （`ui/widgets/sidebar.py`），这里为了少一层文件先放在窗口里 ——
/// 但导航项的表、间距、状态色都照抄，没有"顺手简化"。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// 导航项：(页面 key, 图标, 中文文案)。
    ///
    /// 图标全用**几何符号**而不是 emoji：emoji 在缺字体的机器上会变成豆腐块，
    /// 这几个符号在 Segoe UI Symbol 里一定有（1.0.0 的注释里专门写了这条）。
    ///
    /// ⚠️ 「版本」「账户」都不在导航里：启动面板上的「选择版本」和底部那张档案卡片
    ///    就是入口，同一件事不留两个门（用户 2026-09 明确要求去掉）。
    /// </summary>
    private static readonly (string Key, string Icon, string Text)[] NavItems =
    {
        ("home", "\u25b6", "启动"),
        ("download", "\u2193", "下载"),
        ("personalize", "\u25d0", "个性化"),
        ("settings", "\u2699", "设置"),
    };

    private readonly Dictionary<string, ToggleButton> _navButtons = new(StringComparer.Ordinal);
    private string _currentPageKey = "home";

    /// <summary>
    /// 上一页的 key（切页**之前**那一页）。
    ///
    /// 「版本设置」页的「← 返回」要用它 —— 1.0.0 那边是主窗口记着"从哪进来的"
    /// （`_go_back()`），因为那一页有两个入口：启动面板和版本列表。
    /// 只有一个读者，所以不做整套历史栈，只记一格。
    /// </summary>
    public string? PreviousPageKey { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        Settings = App.Settings;

        BrandText.Text = $"\u26cf  {Core.AppInfo.AppName}";
        AccountSectionLabel.Text = Localizer.Tr("当前档案");
        VersionText.Text = Core.AppInfo.AppVersion;

        BuildNavItems();
        RefreshAccountChip();

        Backdrop.Apply(Settings);
        ShowPage("home");
    }

    /// <summary>全局配置的快捷方式（界面各处都在用）。</summary>
    public AppSettings Settings { get; }

    // ---------- 侧边栏 ----------

    private void BuildNavItems()
    {
        foreach (var (key, icon, text) in NavItems)
        {
            var button = new ToggleButton
            {
                Style = (Style)FindResource("ShellNavItem"),
                // 图标和文案之间是**制表用的空格**：1.0.0 里是
                // f"  {icon}    {tr(text)}"，照抄，不用 Grid 对齐 ——
                // 那样反而会在不同字体下错位。
                Content = $"  {icon}    {Localizer.Tr(text)}",
                Tag = key,
                IsChecked = key == _currentPageKey,
            };
            button.Click += OnNavClick;
            _navButtons[key] = button;
            NavList.Children.Add(button);
        }
    }

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string key })
        {
            ShowPage(key);
        }
    }

    private void OnAccountChipClick(object sender, RoutedEventArgs e)
    {
        // 1.0.0 里这张卡片跳「账户」页。2.0 还没做那一页，先老老实实说清楚。
        ShowPage("accounts");
    }

    /// <summary>
    /// 切页。只更新按钮选中态、不重新触发事件（避免初始化时递归）。
    ///
    /// <paramref name="key"/> 支持 `页面:子段` 这种写法（`download:loader`）——
    /// 自动化截图靠它截到分段页面里默认看不到的那几段
    /// （离屏截图只会停在默认那一段，别的段的 XAML 就永远没人验过）。
    /// </summary>
    public void ShowPage(string key)
    {
        var section = (string?)null;
        var colon = key.IndexOf(':');
        if (colon > 0)
        {
            section = key[(colon + 1)..];
            key = key[..colon];
        }

        // `--page logwindow`：**不是一页**，是那个独立日志窗口。
        //
        // ⚠️ 为什么要有这个入口：日志窗口是"点了设置开关、启动游戏"才会出现的，
        //    离屏截图点不了鼠标也起不了游戏 —— 不给入口的话它的排版永远没人验过
        //    （跟 `personalize:anim` 那个对话框、`download:loader:forge` 那些子段一个道理）。
        //    这里顺手塞几条**样例日志**：空窗口截出来看不出级别配色和过滤那排按钮长什么样，
        //    而配色正是这个窗口最需要人眼过一遍的东西。
        if (string.Equals(key, "logwindow", StringComparison.Ordinal))
        {
            SeedLogSamples();
            App.OpenLogWindow();
            key = "home";       // 主窗口随便落一页（截图的目标是那个窗口，不是这一页）
        }

        // 记下"从哪进来的"。⚠️ 只在**真的换页**时记：同一页刷新（切语言重建）
        // 不该把上一页覆盖成自己，否则「← 返回」会原地打转。
        if (!string.Equals(key, _currentPageKey, StringComparison.Ordinal))
        {
            PreviousPageKey = _currentPageKey;
        }

        _currentPageKey = key;
        foreach (var (navKey, button) in _navButtons)
        {
            button.IsChecked = navKey == key;
        }
        var page = CreatePage(key);
        PageHost.Content = page;

        // 模组页要能被"带着关键词打开"（`--query iris`）：搜索结果是**点出来的**，
        // 离屏截图点不了鼠标 —— 不给个入口的话"卡片上有没有图标"这块永远没人验过。
        // ⚠️ 只认真的在页面上的那一个实例（下载页里挂的那份走它自己的路）。
        // ⚠️ 空串也算"给了"：`--query ""` 就是"不带关键词搜一次"（= 按下载量排的那一屏）。
        if (page is ModsPage mods && _initialQuery is not null)
        {
            mods.RunQuery(_initialQuery);
        }

        // 模组页的子段（`mods:detail`）：等搜索落地后进第一个结果的详情页 ——
        // 详情页也是"点一张卡"点出来的，同样点不了。⚠️ 要排在 RunQuery **后面**
        // （它 await 的就是上面那次搜索的任务）。
        if (page is ModsPage modsPage && section is not null)
        {
            modsPage.ShowSection(section);
        }

        // 下载页切过去时先把"游戏版本下拉 + 加载器列表"备好 ——
        // 那一页自己不知道用户什么时候会切过来
        if (page is DownloadPage download)
        {
            // ⚠️ `--query` 要在 ShowSection **之前**转过去：那一页里的社区资源页
            //    是"切到某一栏"的那一刻才建的，晚一步关键词就喂不进去了。
            download.SetInitialQuery(_initialQuery);
            download.PrepareLoaderSection();
            if (section is not null)
            {
                download.ShowSection(section);
            }
        }

        // 个性化页的子段（`personalize:anim`）：把「动效设置」对话框打开，
        // 好让离屏截图能截到它 —— 那个对话框是**点出来**的，脚本点不了鼠标。
        if (page is PersonalizePage personalize && section is not null)
        {
            personalize.ShowSection(section);
        }

        // 设置页的子段（`settings:memory`）：把页面滚到某张卡片。
        // 这一页五张卡片加起来比 820 高的窗口高一截，不滚的话底下那几张
        // （下载 / 其他 / 关于）在离屏截图里**永远看不到**。
        if (page is SettingsPage settingsPage && section is not null)
        {
            settingsPage.ShowSection(section);
        }

        // 版本设置页的子段（`versionsettings:advanced`）：滚到某张卡 / 展开高级选项 /
        // 切到「Mod 管理」tab。这一页四张卡比窗口高一截，而「高级选项」默认还是收起的
        // （1.0.0 就是 setVisible(False)），不给入口那几块永远没人验过。
        if (page is VersionSettingsPage versionSettingsPage && section is not null)
        {
            versionSettingsPage.ShowSection(section);
        }

        // 版本管理页的子段（`versions:filter:loader` / `versions:search:1.20` /
        // `versions:emptydir` / `versions:dirnext` / `versions:activate`）：
        // 这些状态都是鼠标点出来的（换筛选、敲关键词、切游戏目录、双击版本），
        // 离屏截图点不了鼠标 —— 不给入口那几条分支永远没人验过。
        // ⚠️ `versions:activate` 会切回启动页，所以这一页最后**不是**它自己了；
        //    那条路的截图要的就是"真的跳过去了"这个结果。
        if (page is VersionsPage versionsPage && section is not null)
        {
            versionsPage.ShowSection(section);
        }

        // 入场动效（总开关关掉时 PageAnimator 里会直接返回，什么都不建）
        if (page is IMosslightPage { AnimationHost: { } host })
        {
            PageAnimator.Reset(host);
            PageAnimator.AnimateIn(host, Settings);
        }
    }

    /// <summary>
    /// `--query &lt;关键词&gt;`：进模组页时顺手搜一把（自动化截图用）。
    /// 只对**这一次**建出来的模组页生效 —— 用户自己再点回这一页是全新实例，
    /// 该是空的还是空的。
    /// </summary>
    private string? _initialQuery;

    /// <summary>设置启动参数里的初始搜索词（在 <see cref="ShowPage"/> 之前调）。</summary>
    public void SetInitialQuery(string? query) => _initialQuery = query;

    /// <summary>
    /// 离屏截图该渲染哪个窗口。
    ///
    /// ⚠️ 默认是主窗口，但 `--page personalize:anim` 打开的是**对话框** ——
    ///    那时候要截的是对话框本身（它没 Show，只能由 App 直接渲染它的 Content）。
    ///    返回 null 就表示"截主窗口"。
    /// </summary>
    public Window? ScreenshotTarget
        => (PageHost.Content as PersonalizePage)?.ActiveDialog
           ?? (PageHost.Content as SettingsPage)?.ActiveDialog
           ?? App.LogWindowForScreenshot;

    /// <summary>
    /// 往日志总线塞一小组**样例日志**（只有 `--page logwindow` 那条截图路径会用）。
    ///
    /// 为什么要塞：空窗口截出来既看不到级别配色（错误红 / 警告黄 / 调试灰），
    /// 也看不出"点一下藏一级"那排按钮有内容。截图是给人看的，得让它有东西可看。
    ///
    /// ⚠️ 只在截图模式下调 —— 正常运行时往用户日志里插假数据是**不能接受**的
    ///    （发问题报告时那些行会被当成真的）。
    /// </summary>
    private static void SeedLogSamples()
    {
        if (!App.ScreenshotMode) return;
        if (LogBus.Entries.Count > 0) return;    // 已经有真日志了就别掺假

        foreach (var line in new[]
                 {
                     "[启动器] 检测到 10 个可用的 Java",
                     "[启动器] 准备启动 1.20.1",
                     "[启动器] 启动参数：java -Xmx8192M -Xms3072M …",
                     "[12:00:01] [main/INFO]: Loading Minecraft 1.20.1 with Fabric Loader 0.15.7",
                     "[12:00:02] [main/WARN]: Option 'foo' is deprecated, use 'bar' instead",
                     "[12:00:03] [Render thread/INFO]: OpenGL 4.6.0 NVIDIA 552.22",
                     "[12:00:04] [Server thread/INFO]: Saving chunks for level 'ServerLevel[world]'/minecraft:overworld",
                     "[12:00:05] [main/ERROR]: Could not load mod 'example'",
                     "\tat net.fabricmc.loader.impl.ModContainerImpl.<init>(ModContainerImpl.java:112)",
                     "\tat net.fabricmc.loader.impl.discovery.ModDiscoverer.add(ModDiscoverer.java:88)",
                     "[12:00:05] [main/FATAL]: A fatal error occurred, the game cannot continue",
                     "[12:00:06] [Worker-Main-1/DEBUG]: Cache hit for blockstates/minecraft:stone",
                 })
        {
            LogBus.Write(line);
        }
    }

    private static UserControl CreatePage(string key) => key switch
    {        "home" => new LaunchPage(),
        "personalize" => new PersonalizePage(),
        "accounts" => new AccountsPage(),
        "settings" => new SettingsPage(),
        "versions" => new VersionsPage(),
        "versionsettings" => new VersionSettingsPage(),
        "mods" => new ModsPage(),
        "download" => new DownloadPage(),
        _ => new PlaceholderPage(
            "账户", "离线档案的新建和管理还没搬过来。",
            "正版（微软登录）目前对个人开发者是走不通的：Azure 应用注册是免费的，"
            + "但 Minecraft Services 会回 403，Xbox 授权要加入开发者计划。所以 2.0 先只做离线账户。"),
    };

    // ---------- 底部档案卡片 ----------

    /// <summary>
    /// 刷新底部那张「当前档案」卡片。
    /// 没有档案时显示「未选择档案 / 点此新建」—— 这是 1.0.0 的原文案。
    /// </summary>
    public void RefreshAccountChip()
    {
        var account = App.Services.Accounts.Current;
        if (account is null)
        {
            AvatarText.Text = "?";
            AccountNameText.Text = Localizer.Tr("未选择档案");
            AccountTypeText.Text = Localizer.Tr("点此新建");
            AccountChip.ToolTip = Localizer.Tr("管理档案");
            return;
        }

        // 皮肤读不出来就退回首字母，别显示成空白（1.0.0 的 _AccountChip 同样处理）
        AvatarText.Text = account.Name.Length > 0 ? account.Name[..1].ToUpperInvariant() : "?";
        AccountNameText.Text = account.Name;
        AccountTypeText.Text = AccountTypeLabel(account.Kind);
        AccountChip.ToolTip = Localizer.Tr("管理档案");
    }

    /// <summary>
    /// 账户类型的中文名（1.0.0 在 core/accounts.py 的 type_label()）。
    ///
    /// ⚠️ 转给 <see cref="RealAccountService.TypeLabel"/>，**不要在这里再抄一份**：
    ///    这里原来自己写了一个 switch，和 core 那份已经对不上了
    ///    （写的「第三方登录」，而 1.0.0 的源文案是「第三方验证」）。
    ///    文案的归属地只能有一处，不然改了一边另一边不会跟着变。
    /// </summary>
    public static string AccountTypeLabel(AccountKind kind) => RealAccountService.TypeLabel(kind);
}
