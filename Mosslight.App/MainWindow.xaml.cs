using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
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
    /// <summary>设计说明见 docs/design-notes.md#mosslight-app-mainwindow-xaml-cs</summary>
    private static readonly (string Key, string Text)[] NavItems =
    {
        ("home", "启动"),
        ("download", "下载"),
        ("personalize", "个性化"),
        ("settings", "设置"),
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

        // 设计说明见 docs/design-notes.md#mosslight-app-mainwindow-xaml-cs
        BrandText.Text = Core.AppInfo.AppName;
        AccountSectionLabel.Text = Localizer.Tr("当前档案");
        VersionText.Text = Core.AppInfo.AppVersion;

        BuildNavItems();
        RefreshAccountChip();

        // 把整合包拖到窗口上任意位置就装（= PCL 的手感）。
        // ⚠️ 整合包那张卡自己也有拖放：那边的处理里 `e.Handled = true`，
        //    所以拖在卡片上时这里不会重复收一次 —— 两个入口不会打架。
        AllowDrop = true;
        DragOver += OnWindowDragOver;
        Drop += OnWindowDrop;

        Backdrop.Apply(Settings);
        ShowPage("home");

        // 给"从页面里冒一句提示"留个入口（`App.Toast` / 收藏那颗星用）
        Current = this;
    }

    /// <summary>全局配置的快捷方式（界面各处都在用）。</summary>
    public AppSettings Settings { get; }

    // ============================================================
    //  自绘标题栏
    // ============================================================

    /// <summary>
    /// 窗口句柄一就绪就给 DWM 要圆角。
    ///
    /// ⚠️ 为什么不在 XAML 里做：WPF 没有"圆角窗口"这个属性。
    ///    可选的只有三条路 ——
    ///      ① `AllowsTransparency="True"` + 圆角 Border：能在 Win10 上也圆，
    ///         但整窗会降级成软件渲染（见 MainWindow.xaml 顶部那段注释），
    ///         这个工程有缓动动画，代价太大；
    ///      ② `SetWindowRgn` 裁一个圆角矩形：硬件加速，但**边缘是硬切的锯齿**；
    ///      ③ 让 DWM 圆：Win11（build ≥ 22000）原生支持，硬件加速、带阴影、
    ///         抗锯齿，什么都不用自己画 —— 就是这里用的。
    ///
    /// ⚠️ 半径不能自己定：DWM 只有"正圆角/小圆角/不要圆角"三档（8px / 4px / 0），
    ///    没有"半径设成 12"。所以 PCL2 那种更圆的观感在这个方案下拿不到，
    ///    真要更圆就得回到第 ① 条并接受掉帧。
    ///
    /// ⚠️ Win10（&lt; 22000）上这个调用会失败，窗口保持直角 —— 属于预期，
    ///    不抛异常、不影响任何功能。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            // 2 = DWMWCP_ROUND（Win11 的默认圆角）；1 = 小圆角；1 太小，肉眼几乎看不出
            var preferRound = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preferRound,
                sizeof(int));

            // 顺手把窗口边框色也交给 DWM 跟随界面主题，免得在浅色系统上留一圈不合群的边。
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE。这里用的就是 ThemeApplier 当前生效的模式，
            // 不是重新读一遍配置 —— 用户在个性化里换浅色之后，这一句要跟着变。
            var dark = ThemeApplier.CurrentMode == Core.Theme.PaletteMode.Dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }
        catch
        {
            // 圆角只是观感；拿不到就让窗口保持直角，绝不能因此起不来。
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// 背景设置对话框改完一项之后调：把背景重画一遍、卡片底色按新透明度重刷。
    ///
    /// ⚠️ 两件事都要做，缺一件就是"拖了没反应"：
    ///    · `Backdrop.Apply` —— 背景图 / 铺法 / 压暗 / 纯色，全在那一个控件里，
    ///      不重跑它的话画面停在旧设置上（它只在窗口构造时跑过一次）；
    ///    · `ThemeApplier.ApplyCardOpacity` —— 卡片底色那一族刷子
    ///      （CardSurfaceBrush / CardBorderBrush / CardHoverBrush）。
    ///      界面上是 `{DynamicResource}` 引用的，写进资源就自己刷新。
    /// </summary>
    public void ApplyBackgroundSettings()
    {
        Backdrop.Apply(Settings);
        ThemeApplier.ApplyCardOpacity(Settings.CardOpacity);
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
        ref int value, int size);

    // ============================================================
    //  浮层提示（toast）
    // ============================================================

    /// <summary>当前窗口（给"从页面里冒一句提示"用的；没有窗口时为 null）。</summary>
    public static MainWindow? Current { get; private set; }

    /// <summary>设计说明见 docs/design-notes.md#mosslight-app-mainwindow-xaml-cs</summary>
    public void ShowToast(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var label = new TextBlock
        {
            Text = text,
            Style = (Style)FindResource("ToastText"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 左边那个勾：PCL2 的提示框也是「图标 + 一句话」。图标认不出来
        // （`NavIcons` 里没这一项）就退回只有文字，不报错。
        UIElement content = label;
        if (NavIcons.Create("toast", 15, 2.5, (Brush)FindResource("AccentTextBrush")) is { } icon)
        {
            icon.Margin = new Thickness(0, 0, 8, 0);
            content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { icon, label },
            };
        }

        var card = new Border
        {
            Style = (Style)FindResource("ToastCard"),
            Child = content,
            // 进出动画要动这两个属性
            RenderTransform = new TranslateTransform(0, 0),
        };
        ToastStack.Children.Add(card);

        // ⚠️ 离屏截图模式（`--screenshot`）下**不建动画**：那个模式窗口从不 Show，
        //    没有 PresentationSource，WPF 的动画时钟不会 tick —— 挂着淡入的元素会
        //    永远停在起始状态（opacity 0），截出来就是"提示框根本没出现"。
        //    跟 PageAnimator.BuildPlan 里那条判断同一个理由，见那边的长注释。
        var animate = Settings.AnimationsEnabled && !App.ScreenshotMode;
        if (animate)
        {
            // 进场：从下面 8px 升上来 + 淡入（180ms，跟别的轻量动画一个手感）。
            // 方向是**上浮**不是下落：提示框在右下角，往上顶才像是「冒出来」。
            card.Opacity = 0;
            ((TranslateTransform)card.RenderTransform).Y = 8;

            var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160));
            var slideIn = new System.Windows.Media.Animation.DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
                },
            };
            card.BeginAnimation(OpacityProperty, fadeIn);
            ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.YProperty, slideIn);
        }

        // 停留 + 淡出 + 移除。用 DispatcherTimer 串起来（窗口关了就一起没影）
        var stay = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2.2),
        };
        stay.Tick += (_, _) =>
        {
            stay.Stop();
            if (!animate)
            {
                ToastStack.Children.Remove(card);
                return;
            }

            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
            fadeOut.Completed += (_, _) => ToastStack.Children.Remove(card);
            card.BeginAnimation(OpacityProperty, fadeOut);
        };
        stay.Start();
    }

    /// <summary>本窗口有没有还在屏幕上的提示（自检 / 截图用）。</summary>
    public int ToastCount => ToastStack.Children.Count;

    // ---------- 侧边栏 ----------

    /// <summary>
    /// 换语言之后把**长驻**的界面重贴一遍文字（侧边栏、档案区）。
    ///
    /// ⚠️ 为什么需要它：页面本身是切页时重建的（`ShowPage` 建新的，
    ///    XAML 里的 `{loc:Tr …}` 会重新求值），但主窗口是**一直活着**的 ——
    ///    侧边栏那四个按钮、底部的「当前档案」、档案卡片都是构造时贴的一次性文字，
    ///    不重贴的话切完语言还挂着旧语言（看起来就是"语言系统坏了"）。
    /// </summary>
    public void RefreshLanguage()
    {
        foreach (var (key, text) in NavItems)
        {
            if (_navButtons.TryGetValue(key, out var button))
            {
                button.Content = NavIcons.Row(key, Localizer.Tr(text), size: 17, gap: 10);
            }
        }
        AccountSectionLabel.Text = Localizer.Tr("当前档案");
        RefreshAccountChip();
    }

    /// <summary>
    /// 换语言（界面里的"个性化 → 语言"和诊断开关 `--switch-lang` 都走这里）。
    ///
    /// 顺序有讲究：写配置 → 换词典 → 重贴主窗口 chrome → **重建当前页**。
    ///   · chrome（侧边栏/档案区）是主窗口自己的，必须单独重贴；
    ///   · 页面里的 `{loc:Tr …}` 只在建控件时求值，所以当前页要整个重建。
    /// </summary>
    public void ApplyLanguage(string code)
    {
        App.Settings.Language = code;
        Localizer.SetLanguage(code);
        RefreshLanguage();
        ShowPage(_currentPageKey);
    }

    private void BuildNavItems()
    {
        foreach (var (key, text) in NavItems)
        {
            var button = new ToggleButton
            {
                Style = (Style)FindResource("ShellNavItem"),
                // 图标是矢量（见 NavIcons）：跟文字一起变色，选中态不用另做一套。
                // ⚠️ 以前这里是 `"  {icon}    {文字}"` 那种**用空格对齐**的写法，
                //    靠的是等宽字形；换成真图标之后靠 StackPanel 排，别再退回空格。
                Content = NavIcons.Row(key, Localizer.Tr(text), size: 17, gap: 10),
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

    /// <summary>
    /// 拖到窗口上：能接就显示"可以放"，别的一律拒绝。
    ///
    /// ⚠️ 这里要**每次都设** `e.Effects`：不设的话鼠标一路显示"禁止"光标，
    ///    用户根本不会松手 —— 光有 Drop 处理器是没用的。
    /// </summary>
    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = PackDrop.TryGetSinglePack(e.Data, out _)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    /// <summary>
    /// 把整合包拖进窗口 → 切到「下载 → 整合包」并把包交给那张卡（任意页面都算）。
    /// </summary>
    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (PackDrop.TryGetSinglePack(e.Data, out var path))
        {
            OpenLocalPack(path);
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

        // `--page downloadwindow`：**也不是一页**，是那个下载进度窗口。
        // 同理（日志窗口 / 动效对话框 / `download:loader` 那些子段）：这个窗口平时是
        // "点下载之后"才出现的，离屏截图点不了 —— 不给入口它的排版永远没人看过。
        if (string.Equals(key, "downloadwindow", StringComparison.Ordinal))
        {
            var window = App.OpenDownloadWindow("26.2");
            SeedDownloadSample();
            // 滚到底 —— 这样截图里能看见「还有 5120 个文件没列出来」那句（见同名注释）
            window.ScrollRowsToEnd();
            key = "home";
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
            download.SetInitialMcVersion(_initialMcVersion);
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
    /// `--mc &lt;版本号&gt;`：安装包那几家加载器的版本列表跟着游戏版本走，
    /// 截图想验 LiteLoader / Cleanroom 这种只支持老版本的，就必须能指定版本
    /// （见 `DownloadPage.SetInitialMcVersion`）。在 <see cref="ShowPage"/> 之前调。
    /// </summary>
    public void SetInitialMcVersion(string? version) => _initialMcVersion = version;

    /// <summary>`--mc` 指定的游戏版本（没给就是 null）。</summary>
    private string? _initialMcVersion;

    /// <summary>
    /// 切到「下载 → 整合包」并把一个本地整合包交给那张卡（**拖放和启动参数共用这一条路**）。
    ///
    /// ## 为什么做成一个公开方法，而不是在 Drop 里现写一遍
    /// 拖放是**鼠标动作**，离屏截图/自动化点不了 —— 给一个方法当入口，
    /// 「拖进来 → 认格式 → 预览 → 安装」这一整段才验得到
    /// （`--pack &lt;文件&gt;` 调的就是这里，跟真的拖进来执行的是同一段代码，
    /// 不是"另写一条给截图用的假路"）。
    ///
    /// ⚠️ 不在这里解析/安装：那张卡已经有完整的一套（认格式 → 预览 → 安装），
    ///    这里再写一遍等于养两份会走偏的逻辑。这里只负责"把人送到那儿"。
    /// </summary>
    public void OpenLocalPack(string path)
    {
        ShowPage("download:modpacks");
        if (PageHost.Content is DownloadPage page)
        {
            page.OfferLocalPack(path);
        }
    }

    /// <summary>
    /// 离屏截图该渲染哪个窗口。
    ///
    /// ⚠️ 默认是主窗口，但 `--page personalize:anim` 打开的是**对话框** ——
    ///    那时候要截的是对话框本身（它没 Show，只能由 App 直接渲染它的 Content）。
    ///    返回 null 就表示"截主窗口"。
    /// </summary>
    public Window? ScreenshotTarget
    {
        get
        {
            // ⚠️ 写成一段段的 if 而不是 `??` 连起来：`??` 链的**结果类型**取的是
            //    右边非空那个操作数的类型（这里会推成 LogWindow），
            //    下一段 `?? DownloadWindow` 就报 CS0019（两个不同类型的窗口没法 ??）。
            if ((PageHost.Content as ModsPage)?.ActiveDialog is { } targetPicker) return targetPicker;
            if ((PageHost.Content as PersonalizePage)?.ActiveDialog is { } anim) return anim;
            if ((PageHost.Content as SettingsPage)?.ActiveDialog is { } about) return about;
            if (App.LogWindowForScreenshot is { } log) return log;
            if (App.DownloadWindowForScreenshot is { } download) return download;
            return null;
        }
    }

    /// <summary>
    /// 往下载窗口灌一份**样例数据**（只有 `--page downloadwindow` 那条截图路径会用）。
    ///
    /// 为什么要灌：空窗口什么都验不到 —— 左边三个读数、每行那根细条、
    /// 「还有 N 个文件没列出来」这句，全都要有数据才看得出来排得对不对。
    ///
    /// ⚠️ 只在截图模式下调。正常运行时往 <see cref="Core.Services.Engine.DownloadMonitor"/>
    ///    里塞假数据是**不能接受**的：那会让窗口显示一堆根本没在下的文件。
    /// </summary>
    private static void SeedDownloadSample()
    {
        if (!App.ScreenshotMode) return;

        // 8 个文件、5132 个任务总量 —— 照 1.0.0 那张截图的样子造
        var tasks = new List<Core.Services.Engine.DownloadTask>
        {
            new("https://example.invalid/c", @"C:\mc\versions\26.2\26.2.jar",
                size: 37_380_000, label: "client.jar", kind: "客户端"),
            new("https://example.invalid/a", @"C:\mc\libraries\authlib-9.0.75.jar",
                size: 141_880, label: "authlib-9.0.75.jar", kind: "库"),
            new("https://example.invalid/j", @"C:\mc\libraries\jtracy-1.0.37.jar",
                size: 12_570, label: "jtracy-1.0.37.jar", kind: "库"),
            new("https://example.invalid/jn", @"C:\mc\libraries\jtracy-1.0.37-natives-windows.jar",
                size: 45_670, label: "jtracy-1.0.37-natives-windows.jar", kind: "库"),
            new("https://example.invalid/nb", @"C:\mc\libraries\netty-buffer-4.2.15.Final.jar",
                size: 363_200, label: "netty-buffer-4.2.15.Final.jar", kind: "库"),
            new("https://example.invalid/nc", @"C:\mc\libraries\netty-codec-base-4.2.15.Final.jar",
                size: 149_330, label: "netty-codec-base-4.2.15.Final.jar", kind: "库"),
            new("https://example.invalid/ncc", @"C:\mc\libraries\netty-codec-compression-4.2.15.Final.jar",
                size: 478_920, label: "netty-codec-compression-4.2.15.Final.jar", kind: "库"),
            new("https://example.invalid/nh", @"C:\mc\libraries\netty-handler-4.2.15.Final.jar",
                size: 512_410, label: "netty-handler-4.2.15.Final.jar", kind: "库"),
        };

        // ⚠️ 不能把任务标成"正在下"（`State` 的 setter 在 Core 里是 internal）。
        //    所有行都停在"等待中"、显示 `0 B / 大小` —— 正好跟 1.0.0 那张截图一样
        //    （刚开窗那一秒，八个文件都还没收到第一个字节）。

        // 补到 5132 个：窗口只列 12 行，剩下的会变成「还有 5120 个文件没列出来」——
        // 那句正是要靠这个数才看得出来对不对（1.0.0 那张截图就是 5120）。
        for (var i = tasks.Count; i < 5132; i++)
        {
            tasks.Add(new Core.Services.Engine.DownloadTask(
                "https://example.invalid/lib", $@"C:\mc\libraries\lib-{i}.jar",
                size: 20_000 + i, label: $"lib-{i}.jar", kind: "库"));
        }

        Core.Services.Engine.DownloadMonitor.Publish(
            new Core.Services.Engine.DownloadManager(maxWorkers: 1, source: Core.Services.Engine.DownloadSourceMode.Official),
            new Core.Services.Engine.DownloadSnapshot(
                Tasks: tasks,
                Done: 0,
                Failed: 0,
                Active: 8,
                Count: 5132,
                Bytes: 0,
                Total: tasks.Sum(t => t.Total),
                Percent: 0.0377,
                Speed: 0,
                Finished: false,
                MirrorOff: false));
    }

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
            ShowAvatarImage(null);
            AvatarText.Text = "?";
            AccountNameText.Text = Localizer.Tr("未选择档案");
            AccountTypeText.Text = Localizer.Tr("点此新建");
            AccountChip.ToolTip = Localizer.Tr("管理档案");
            return;
        }

        // 皮肤头像：配置目录里自己放的 `<档案名>.png` 优先，否则按 UUID 挑一张内置默认皮肤。
        // 读不出来（文件坏了 / 内置图缺了）就退回首字母 —— 别显示成空白。
        var avatar = PlayerAvatars.Avatar(
            account,
            Core.Settings.AppSettings.ResolveConfigDir(App.BaseDir),
            size: 32,
            dpr: VisualTreeHelper.GetDpi(this).DpiScaleX);
        ShowAvatarImage(avatar);

        AvatarText.Text = account.Name.Length > 0 ? account.Name[..1].ToUpperInvariant() : "?";
        AccountNameText.Text = account.Name;
        AccountTypeText.Text = AccountTypeLabel(account.Kind);
        AccountChip.ToolTip = Localizer.Tr("管理档案");
    }

    /// <summary>有皮肤头像就盖住首字母，没有就撤掉图（两条路都别留空位）。</summary>
    private void ShowAvatarImage(System.Windows.Media.Imaging.BitmapSource? avatar)
    {
        AvatarImage.Source = avatar;
        AvatarImage.Visibility = avatar is null ? Visibility.Collapsed : Visibility.Visible;
        AvatarText.Visibility = avatar is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 账户类型的中文名（1.0.0 在 core/accounts.py 的 type_label()）。
    ///
    /// ⚠️ 转给 <see cref="RealAccountService.TypeLabel"/>，**不要在这里再抄一份**：
    ///    文案的归属地只能有一处，不然改了一边另一边不会跟着变。
    /// </summary>
    public static string AccountTypeLabel(AccountKind kind) => RealAccountService.TypeLabel(kind);
}
