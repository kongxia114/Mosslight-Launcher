using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Mosslight.App.Infrastructure;
using Mosslight.Core.Localization;
using Mosslight.Core.Services;
using Mosslight.Core.Services.Engine;
using Mosslight.Core.Settings;

namespace Mosslight.App.Views;

/// <summary>
/// 「下载」页。左边一列分类，右边跟着分类切内容（重做成 1.0.0 的样子）。
///
/// ## 四个面板
/// · **版本页**（选「版本 → Minecraft」）：最新版本卡 + 正式版 / 快照 / 远古版本
///   三个折叠组（**只有"能下到的"**，本地已装的在「版本」页，不在这）。
///   数据来自官方清单（<see cref="VersionManifest"/>，900+ 个版本，带缓存），
///   点任意一行 → 进安装选项页。
/// · **安装选项页**：五家加载器各一个折叠组，底部一个「开始下载」。
///   加载器数据走 <see cref="ILoaderService.CatalogAsync"/>。
/// · **社区资源**（模组 / 资源包 / 光影包 / 整合包）：挂共用页 `ModsPage` 的
///   对应类型那一份（见 <see cref="ModContentType"/>）；整合包那栏上面还有
///   原来那张本地 `.mrpack` 卡。
/// · **占位页 / 「还没接」页**：没做出来的分类照实说，不铺假数据
///   （数据包是单独一张说明页：它要先知道放进哪一个存档）。
///
/// ## 为什么这一页自己 new 一个 VersionManifest
/// 清单里的 `type`（release / snapshot / old_*）和 `releaseTime` 是这一页**必须**的
/// （分组、排序、"发布于 …"都靠它），而 <see cref="IDownloadService.RefreshVersionListAsync"/>
/// 只回一串版本号。`VersionManifest` 本来就是 Core 里拉清单 + 缓存的**那一份**实现
/// （`RealDownloadService` 内部用的也是它），这里直接复用，不另写一套网络与缓存。
/// ⚠️ 只**用**它，没有改 Core 的任何东西。
///
/// ## 保留的对外接口
/// · <see cref="ShowSection"/>：`--page download:loader` 这类自动化截图靠它切段
/// · <see cref="PrepareLoaderSection"/>：主窗口切到本页时调（现在只负责"先把清单动起来"）
/// · <see cref="SetInitialQuery"/>：`--query` 转给嵌进来的社区资源页（自动化截图用）
/// </summary>
public partial class DownloadPage : UserControl, IMosslightPage
{
    // ============================================================
    //  表（照 1.0.0 的 ui/pages/download_page.py 抄，顺序就是界面上的顺序）
    // ============================================================

    /// <summary>
    /// 「安装包」那一段列哪几家加载器。顺序 = 引擎那张表（`LoaderCatalog.LoaderOrder`）。
    ///
    /// ⚠️ **别在这张表里加引擎不认识的 key**：左栏会出现一个点进去"取不到版本列表"
    ///    的假入口（而其实是"我们没这个接口"）。两边的 key 集合在自检里对着
    ///    （见 `CheckLoaderKeyParity`），加一家要两处一起加。
    /// ⚠️ 文案是**专有名词**，不翻译（Forge / NeoForge / Cleanroom / Legacy Fabric …）。
    /// </summary>
    private static readonly (string Key, string Title)[] LoaderOrder =
    {
        ("forge", "Forge"),
        ("neoforge", "NeoForge"),
        ("cleanroom", "Cleanroom"),
        ("fabric", "Fabric"),
        ("legacy_fabric", "Legacy Fabric"),
        ("quilt", "Quilt"),
        ("optifine", "OptiFine"),
        ("liteloader", "LiteLoader"),
        ("labymod", "LabyMod"),
    };

    /// <summary>
    /// 「安装包」那一段的 key（顺序同表）。**给自检用的**：它要拿这张表跟引擎那张
    /// `LoaderCatalog.LoaderOrder` 对一遍 —— 两边错开的那次就是"左栏有 LiteLoader、
    /// 点进去一句'这一栏还没做'"。
    /// </summary>
    public static IReadOnlyList<string> LoaderKeys
        => LoaderOrder.Select(l => l.Key).ToArray();

    /// <summary>左侧分类。`Group` 为 null = 这一项自己就是一级、没有分组标题。</summary>
    private static readonly (string? Group, (string Key, string Text)[] Items)[] Nav = BuildNavTable();

    /// <summary>
    /// 左栏那张表。「安装包」那一段**由 <see cref="LoaderOrder"/> 生成**：
    /// 以前是手抄一遍，加一家加载器就得记着改两处（漏改的那次就是
    /// 左栏有 LiteLoader、点进去却是"这一栏还没做"）。
    /// </summary>
    private static (string? Group, (string Key, string Text)[] Items)[] BuildNavTable()
    {
        var installers = new List<(string Key, string Text)>
        {
            // 原版排在最上面（跟 1.0.0 的截图一致），其余按加载器表
            ("install_minecraft", "Minecraft"),
        };
        installers.AddRange(LoaderOrder.Select(l => (l.Key, l.Title)));

        return new (string? Group, (string Key, string Text)[] Items)[]
        {
            ("版本", new[] { ("minecraft", "Minecraft") }),
            ("社区资源", new[]
            {
                ("mods", "模组"),
                ("modpacks", "整合包"),
                ("datapacks", "数据包"),
                ("resourcepacks", "资源包"),
                ("shaders", "光影包"),
                ("worlds", "世界"),
            }),
            // 「收藏夹」没有分组标题，所以 Group 给 null
            (null, new[] { ("favorites", "收藏夹") }),
            ("安装包", installers.ToArray()),
        };
    }

    /// <summary>
    /// 三个折叠组的顺序（正式版在最上面）。
    ///
    /// ⚠️ 以前这里还有第四组「本地版本」（本地装着、但官方清单里没有的那些：
    ///    Forge / Fabric / 整合包实例）。2026-10 按用户要求**去掉了** ——
    ///    那属于「版本」页的活，「我已经有什么」不该出现在"我要下什么"的页面上。
    /// </summary>
    private static readonly string[] VersionGroupOrder = { "正式版", "快照", "远古版本" };

    /// <summary>清单里的 type → 归到哪个组。</summary>
    private static readonly Dictionary<string, string> TypeGroup = new(StringComparer.Ordinal)
    {
        ["release"] = "正式版",
        ["snapshot"] = "快照",
        ["old_beta"] = "远古版本",
        ["old_alpha"] = "远古版本",
    };

    /// <summary>
    /// 占位页的说明文案（逐条照 1.0.0 的 DETAILS 抄）。
    ///
    /// ⚠️ 只有**真的还没做**的栏才留在这儿。安装包那几家以前全在这儿（Cleanroom /
    ///    LabyMod / LiteLoader / Legacy Fabric 都写着"还没研究过"），
    ///    现在它们各有各的列表，点左栏进去就是安装选项页 —— 留着这些文案
    ///    只会在别的地方被翻出来显示成"这一栏还没做"。
    /// </summary>
    private static readonly Dictionary<string, string> Details = new(StringComparer.Ordinal)
    {
        ["minecraft"] = "各版本的原版安装包",
        ["mods"] = "按游戏版本和加载器筛 Mod，从 Modrinth 取",
        ["modpacks"] = "整合包，从 Modrinth 取",
        ["datapacks"] = "数据包，从 Modrinth 取",
        ["resourcepacks"] = "资源包，从 Modrinth 取",
        ["shaders"] = "光影包，从 Modrinth 取",
        ["worlds"] = "世界存档（Modrinth 没有这类资源，暂时还没做）",
        ["favorites"] = "收藏的版本和资源，集中在这儿一键装",
        ["install_minecraft"] = "各版本的原版安装包",
    };

    private const string PlaceholderHintText =
        "这一栏还没做。做出来以后这里会列出可下载的版本，点一下就装进当前游戏目录。";

    // 加载器副标题的几句话（照 1.0.0 的 install_options.py）
    private const string LoaderLoadingNote = "正在获取版本列表…";
    private const string LoaderFailNote = "取不到版本列表（网络或接口问题）";
    private const string LoaderNotImplementedNote = "安装还没接";
    private const string NotInstallableTail = "（安装还没接）";

    // ============================================================
    //  状态
    // ============================================================

    private readonly VersionManifest _manifest = new(AppSettings.ResolveConfigDir(App.BaseDir));
    private readonly Dictionary<string, CollapsibleGroup> _versionGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CollapsibleGroup> _loaderGroups = new(StringComparer.Ordinal);

    private Task? _manifestTask;
    private JsonObject? _manifestData;
    private ManifestInfo? _manifestInfo;
    private bool _busy;
    private VersionRowItem? _selectedVersion;
    private string _selectedLoaderKey = string.Empty;
    private string _selectedLoaderVersion = string.Empty;
    private IReadOnlyList<LoaderRow> _loaderRows = Array.Empty<LoaderRow>();

    /// <summary>这一次的加载器清单拉取任务（截图模式要等它落地再展开分组）。</summary>
    private Task? _loaderTask;
    private double _dpiScale = 1.0;

    // 整合包（原来那三段里的第三段，功能一个没少，只是换了个入口：拖进来就装）
    //
    // ⚠️ 这里**只留路径**：包的解析结果放在 `_packSummary` / `_packBlob`（见整合包那一节），
    //    装的时候服务层会自己再读一遍文件 —— 界面手里拿着一份旧 plan 反而容易过期。
    private string _modpackPath = string.Empty;

    /// <summary>
    /// 「社区资源」那几栏挂的共用页，**按 project_type 各留一份**。
    ///
    /// ⚠️ 一份实例服务一栏，不能共用：切栏时页面里的搜索框、结果列表、详情页
    ///    都会跟着带过去（用户在资源包里搜的词会出现在光影包那一栏）。
    ///    建过的留着，是为了来回切不丢结果、也不用重新建控件。
    /// </summary>
    private readonly Dictionary<string, ModsPage> _contentPages = new(StringComparer.Ordinal);

    /// <summary>
    /// `--query &lt;关键词&gt;`：自动化截图用。嵌进来的社区资源页建好之后立刻搜一把 ——
    /// 搜索结果和详情页都是"点出来的"，离屏截图点不了鼠标。
    /// </summary>
    private string? _initialQuery;

    /// <summary>`--mc &lt;版本号&gt;`：安装选项页要认的那一版（见 <see cref="SetInitialMcVersion"/>）。</summary>
    private string? _initialMcVersion;

    // ---------- 安装页的选择状态 ----------
    //
    // PCL 那边的东西对应的字段：
    //   selectedLoaderName  → `_selectedLoaderKey`（主加载器，只有一个）
    //   selectedOptiFine    → `_selectedOptiFineVersion`（**单独一个槽位**，能跟 Forge 并存）
    //   selectedFabricApi   → `_selectedFabricApiVersion`（选了 Fabric 就自动带上）
    //   isSelectNameEdited  → `_versionNameCustom`（非空 = 用户手改过，不再自动改）

    /// <summary>OptiFine 的版本（空 = 没选）。它不占"主加载器"的位置。</summary>
    private string _selectedOptiFineVersion = string.Empty;

    /// <summary>Fabric API 的版本（空 = 不装）。</summary>
    private string _selectedFabricApiVersion = string.Empty;

    /// <summary>已经自动带过一次 Fabric API 了吗（用户取消之后不再抢回来，跟 PCL 一样）。</summary>
    private bool _fabricApiAutoPicked;

    /// <summary>Fabric API 在这个游戏版本上的最新版号（空 = 没拉到 / 没有）。</summary>
    private string _fabricApiLatest = string.Empty;

    /// <summary>Fabric API 的版号**拉过一次了**（拿它区分"还在拉"和"确实没有"）。</summary>
    private bool _fabricApiLoaded;

    private Task? _fabricApiTask;

    /// <summary>Fabric API 那一行（它不在 <see cref="LoaderOrder"/> 里，单独建一组）。</summary>
    private CollapsibleGroup? _fabricApiGroup;

    /// <summary>用户手改过的版本名（空 = 跟着选择自动拼，见 <see cref="RefreshVersionName"/>）。</summary>
    private string _versionNameCustom = string.Empty;

    /// <summary>程序正在改 TextBox（这时候的 TextChanged 不算用户改的）。</summary>
    private bool _versionNameGuard;

    public DownloadPage()
    {
        InitializeComponent();

        _versionGroups["正式版"] = ReleaseGroup;
        _versionGroups["快照"] = SnapshotGroup;
        _versionGroups["远古版本"] = OldGroup;
        BuildLoaderGroups();

        BuildNav();

        // 分组里点一行 → 进安装选项页
        foreach (var group in _versionGroups.Values)
        {
            group.ItemActivated += OnVersionActivated;
        }

        // 进页面先落在「版本 → Minecraft」上，并把清单拉起来
        SelectCategory("minecraft");

        // 真机是 125% 缩放的屏幕时，构造期读到的 DPI 还不可靠（控件没进视觉树）。
        // 进树之后补一次：缩放变了就按新缩放重建图标，不然那些方块是糊的。
        Loaded += OnLoadedOnce;
    }

    /// <summary>入场动效：返回 null = 这一页不做整页入场（行太多，逐行滑入会拖沓）。</summary>
    public Panel? AnimationHost => null;

    /// <summary>
    /// 安装选项页那几组加载器 —— **按 <see cref="LoaderOrder"/> 现建**（见 XAML 里那段注释：
    /// 写在 XAML 里就得跟着引擎的表手工同步，以前漏过一次）。
    ///
    /// 标题取的是表里的专有名词（`Localizer.Tr` 对没翻过的词就是原样返回，
    /// 所以中英日俄都是这几个名字）。
    /// </summary>
    private void BuildLoaderGroups()
    {
        foreach (var (key, title) in LoaderOrder)
        {
            var group = new CollapsibleGroup { Margin = new Thickness(0, 0, 0, 8) };
            group.SetTitle(Localizer.Tr(title));
            group.SetExpanded(false);

            LoaderGroupsHost.Children.Add(group);
            _loaderGroups[key] = group;

            var loaderKey = key;
            group.ItemActivated += (_, item) => OnLoaderVersionActivated(loaderKey, item);
            group.ClearRequested += (_, _) => OnLoaderRowCleared(loaderKey);

            // Fabric API 排在 Fabric 后面（PCL 也是这个位置：配套的 mod 贴着加载器）
            if (key == "fabric")
            {
                AddFabricApiGroup();
            }
        }
    }

    /// <summary>
    /// Fabric API 那一行。
    ///
    /// ⚠️ 它**不是加载器**（不进 <see cref="LoaderOrder"/>，也不参与"主加载器互斥"）：
    ///    选中 Fabric 时自动带上、进安装请求时走 `InstallFabricApi`；
    ///    没选 Fabric / Quilt 时整行不显示（见 `RefreshFabricApiRow`）。
    /// ⚠️ 它只有一条"版本"（最新版），不像加载器那样列一长串 —— 选哪个版本装
    ///    对用户没意义（Fabric API 只在极少数情况下要指定旧版，那属于版本设置页的活）。
    /// </summary>
    private void AddFabricApiGroup()
    {
        var group = new CollapsibleGroup { Margin = new Thickness(0, 0, 0, 8) };
        group.SetTitle(LoaderCompat.Title("fabric_api"));
        group.SetExpanded(false);
        group.Visibility = Visibility.Collapsed;   // 没选 Fabric 之前不占位置

        // 插在 Fabric 后面（LoaderGroupsHost 里刚加过 Fabric）
        var index = LoaderGroupsHost.Children.IndexOf(_loaderGroups["fabric"]) + 1;
        LoaderGroupsHost.Children.Insert(index, group);
        _fabricApiGroup = group;

        group.ItemActivated += (_, item) =>
        {
            _selectedFabricApiVersion = item.Id;
            group.SetPicked(item.Id);
            RefreshInstallRows();
            RefreshFooter();
        };
        group.ClearRequested += (_, _) => OnLoaderRowCleared("fabric_api");
    }

    // ============================================================
    //  左侧分类栏
    // ============================================================

    private void BuildNav()
    {
        foreach (var (group, items) in Nav)
        {
            if (group is not null)
            {
                // 分组标题**不可选**（点它不该把右边切走）：用禁用的项表达 ——
                // 禁用的 ListBoxItem 不吃鼠标事件，也就不会被选中
                NavList.Items.Add(new ListBoxItem
                {
                    Content = new TextBlock
                    {
                        Text = Localizer.Tr(group),
                        Style = (Style)FindResource("DownloadNavGroup"),
                    },
                    Style = (Style)FindResource("DownloadNavHeadItem"),
                    IsEnabled = false,
                });
            }

            foreach (var (key, text) in items)
            {
                NavList.Items.Add(new ListBoxItem
                {
                    // 图标 + 文字（图标跟着行的 Foreground 变色，见 NavIcons 的类注释）。
                    // 认不出来的 key 只显示文字，不报错。
                    Content = NavIcons.Row(key, Localizer.Tr(text)),
                    Tag = key,
                    ToolTip = Localizer.Tr(text),
                });
            }
        }
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is ListBoxItem { Tag: string key })
        {
            ShowCategory(key);
        }
    }

    /// <summary>切分类：先把左栏那一行选中，再切右侧面板。</summary>
    private void SelectCategory(string key)
    {
        foreach (var item in NavList.Items)
        {
            if (item is ListBoxItem { Tag: string tag } row && tag == key)
            {
                // 已经在那一行上时赋值不会触发 SelectionChanged，所以下面照样要切
                NavList.SelectedItem = row;
                break;
            }
        }
        ShowCategory(key);
    }

    /// <summary>
    /// `--page download:kbd`：把左栏的"焦点相关实况"打到标准输出（不切页面、不改数据）。
    ///
    /// ## 为什么要这么个东西
    /// 「焦点框有没有画出来」「Tab / 方向键还进不进得来」这两件事，**离屏截图证明不了**：
    /// 截图那条路是直接 Measure/Arrange 再 Render 控件树，既没有键盘、窗口也没 Show ——
    /// 焦点框那层 Adorner 根本不在渲染树里，"截出来没有框"不能说明样式写对了。
    /// 所以这里改成问**运行时的真实取值**：样式解析完了以后 FocusVisualStyle 到底是
    /// null 还是那套默认虚线框、IsTabStop/Focusable 有没有被谁关掉。
    /// （WinExe 平时没有控制台，但 stdout 被重定向时 Console.WriteLine 照样写得出来，
    ///   截图脚本把输出收下来就是证据。）
    /// </summary>
    private void ProbeNavKeyboard()
    {
        // ⚠️ 这里是在窗口 Show / 截图那次 Measure **之前**被调的（App.OnStartup 里
        //    ShowPage 就把它用掉了），整棵树还没有布局 —— 容器一个都没生成、
        //    ActualHeight 全是 0。所以先给 ListBox 单独跑一遍测量/排布，
        //    否则下面量到的全是假数（第一次写就踩了：全是 0）。
        try
        {
            NavList.Measure(new Size(320, 4000));
            NavList.Arrange(new Rect(0, 0, 320, Math.Max(NavList.DesiredSize.Height, 400)));
            NavList.UpdateLayout();
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"[nav-kbd] 强制布局失败（量不到行高）：{ex.Message}");
        }

        Console.WriteLine($"[nav-kbd] ListBox IsTabStop={NavList.IsTabStop} Focusable={NavList.Focusable} "
            + $"SelectionMode={NavList.SelectionMode} "
            + $"TabNavigation={KeyboardNavigation.GetTabNavigation(NavList)} "
            + $"DirectionalNavigation={KeyboardNavigation.GetDirectionalNavigation(NavList)}");

        var shown = 0;
        foreach (var item in NavList.Items)
        {
            if (item is not ListBoxItem row)
            {
                continue;
            }
            // 容器要等布局跑完才生成；没生成的话 FocusVisualStyle 还是"没套样式"的值
            var container = NavList.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem ?? row;
            // 模板根（那层带 Margin / MinHeight 的 Border）
            var shell = container.IsLoaded || VisualTreeHelper.GetChildrenCount(container) > 0
                ? VisualTreeHelper.GetChild(container, 0) as FrameworkElement
                : null;

            // 行高实测。⚠️ 用 ActualHeight 而不是"两行顶边相减"：后者要先
            //    TranslatePoint，而窗口没 Show 时那个调用会抛"没有 PresentationSource"。
            //    ListBoxItem 的 ActualHeight **已经含了模板里那层 Border 的 Margin**
            //    （Margin 算在布局槽里、算在父级要的高度里），所以它正好就是行距。
            Console.WriteLine($"[nav-kbd] 行[{shown}] 文本=\"{TextOf(row)}\" 可选={row.Tag is string} "
                + $"IsTabStop={container.IsTabStop} Focusable={container.Focusable} "
                + $"FocusVisualStyle={(container.FocusVisualStyle is null ? "null（不画焦点框）" : "非 null（会画！）")}"
                + $" 行高={container.ActualHeight:N2} 内层={shell?.ActualHeight ?? double.NaN:N2} "
                + $"内容={(shell is null ? "-" : (shell as Border)?.Child is FrameworkElement c ? c.ActualHeight.ToString("N2") : "-")}");
            shown++;
        }

        // 真把焦点放进去试试：焦点没丢才谈得上"键盘可达"
        if (NavList.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem first)
        {
            var ok = Keyboard.Focus(first);
            Console.WriteLine($"[nav-kbd] Keyboard.Focus(第一行) → {ok?.GetType().Name ?? "null"}，"
                + $"IsKeyboardFocused={first.IsKeyboardFocused} IsSelected={first.IsSelected}");
        }
    }

    /// <summary>诊断用：把项的内容说成人看得懂的名字（分组标题是个 TextBlock）。</summary>
    private static string TextOf(ListBoxItem row)
        => row.Content switch
        {
            TextBlock text => text.Text,
            string s => s,
            var other => other?.GetType().Name ?? "(null)",
        };

    private void ShowCategory(string key)
    {
        switch (key)
        {
            case "minecraft":
            case "install_minecraft":
                // 「安装包 → Minecraft」跟「版本 → Minecraft」是**同一件事**
                // （左边两个入口，右边同一份版本清单）。以前它掉到占位页，
                // 写着"各版本的原版安装包 / 这一栏还没做"—— 明明下面就有。
                ShowPane(VersionsPane);
                _ = EnsureManifestAsync();
                return;
            case "favorites":
                ShowFavorites();
                return;
            case "modpacks":
                ShowModpacksPane();
                return;
            case "datapacks":
                // 数据包要先知道放进哪一个存档 —— 那一步还没做，所以**不铺列表**，
                // 老老实实显示"还没接 + 为什么"（见 XAML 的 DatapackPane）。
                ShowPane(DatapackPane);
                return;
        }

        // 「安装包」里那几家加载器：点左栏直接进安装选项页，并把那一家展开选中。
        // （以前这些 key 全掉到占位页 —— 用户说的"只下载文件、不走安装流程"就是
        //   走到这儿看见一句"这一栏还没做"。）
        if (LoaderOrder.Any(l => string.Equals(l.Key, key, StringComparison.Ordinal)))
        {
            _ = OpenInstallSectionAsync(key);
            return;
        }

        // 「社区资源」其余的栏（模组 / 资源包 / 光影包）挂的是同一个共用页，
        // 只是类型参数不同 —— 类型表在 ModContentTypes.cs 那张 `ModContentType.All` 里。
        if (ModContentType.ForNavKey(key) is { } type)
        {
            ShowContentPane(type);
            return;
        }

        ShowPlaceholder(key);
    }

    private void ShowPane(UIElement target)
    {
        foreach (var pane in new UIElement[]
                 {
                     VersionsPane, InstallPane, PlaceholderPane, FavoritesHost,
                     ModpacksHost, ModsHost, DatapackPane,
                 })
        {
            pane.Visibility = ReferenceEquals(pane, target) ? Visibility.Visible : Visibility.Collapsed;
        }

        // 切走的那一页上如果还挂着加载条，**立刻**收掉（不等那 180ms）。
        // 理由：加载条是"这一页在等数据"的信号，人都不在这一页了还转着，
        // 回来的时候会看到一条莫名其妙的条 —— 数据其实早到了。
        if (!ReferenceEquals(target, InstallPane))
        {
            LoaderLoading.HideNow();
        }
        if (!ReferenceEquals(target, VersionsPane))
        {
            ManifestLoading.HideNow();
        }
    }

    private void ShowPlaceholder(string key)
    {
        PlaceholderTitle.Text = Localizer.Tr(TextOfCategory(key));
        PlaceholderDetail.Text = Localizer.Tr(Details.TryGetValue(key, out var detail) ? detail : string.Empty);
        PlaceholderHint.Text = Localizer.Tr(PlaceholderHintText);
        ShowPane(PlaceholderPane);
    }

    // ============================================================
    //  收藏夹（抄 PCL2 的作业）
    // ============================================================

    /// <summary>
    /// 铺「收藏夹」那一页：一行一个收藏的项目，点一行就跳回它自己那一栏的详情页。
    ///
    /// ⚠️ 收藏的是**项目**不是版本（PCL2 也是这个口径）：版本列表随时间变，
    ///    收藏某个版本没有意义；收藏项目，点进去再挑版本。
    /// ⚠️ 每次进来都**重新铺**：收藏可能在别的页面被改过（详情页那颗星），
    ///    缓存一份就得处理"怎么知道它变了"，不如直接重铺（几十条，快）。
    /// </summary>
    private void ShowFavorites()
    {
        FavoritesList.Children.Clear();

        var items = App.Favorites.Items;
        FavoritesEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FavoritesSubtitle.Text = items.Count == 0
            ? Localizer.Tr("收藏夹是空的")
            : Localizer.Tr("共 {n} 个项目　·　点一行直接进它的详情页", ("n", items.Count));

        foreach (var item in items)
        {
            FavoritesList.Children.Add(BuildContextFavoriteRow(item));
        }

        ShowPane(FavoritesHost);
    }

    /// <summary>一行收藏：图标 + 名字 + 类型 + 「移除」。</summary>
    private UIElement BuildContextFavoriteRow(Core.Services.FavoriteItem item)
    {
        var type = ModContentType.ForProjectType(item.ProjectType);

        var icon = new Image
        {
            Width = 32,
            Height = 32,
            Stretch = Stretch.Uniform,
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        if (item.IconUrl.Length > 0 && Uri.TryCreate(item.IconUrl, UriKind.Absolute, out var uri))
        {
            icon.Source = new System.Windows.Media.Imaging.BitmapImage(uri);
        }

        var title = new TextBlock
        {
            Text = item.Title,
            // ⚠️ 这几个 key 必须用**真实存在**的（`ModsPage` 搜结果卡那套）：
            //    我原来写的是 `ModCardTitle` / `SecondaryButton`，这两个 key **不存在**，
            //    `FindResource` 拿到的是"未解析的引用"，强转成 Style 就抛
            //    「Unable to cast MS.Internal.NamedObject to System.Windows.Style」
            //    —— 进收藏夹那一栏直接崩（2026-10-18 用户点到就报）。
            Style = (Style)FindResource("ModCardName"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var meta = new TextBlock
        {
            Text = (type?.Label ?? item.ProjectType)
                   + (item.AddedAt > DateTimeOffset.MinValue
                       ? "    ·    " + item.AddedAt.ToLocalTime().ToString("yyyy-MM-dd")
                       : string.Empty),
            Style = (Style)FindResource("ModCardMeta"),
            Margin = new Thickness(0, 3, 0, 0),
        };

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        text.Children.Add(title);
        text.Children.Add(meta);

        var open = new Button
        {
            Content = Localizer.Tr("打开"),
            Style = (Style)FindResource("GhostButton"),
            Padding = new Thickness(12, 0, 12, 0),
            Height = 28,
            VerticalAlignment = VerticalAlignment.Center,
        };
        open.Click += (_, e) =>
        {
            e.Handled = true;
            OpenFavorite(item);
        };

        var remove = new Button
        {
            Content = Localizer.Tr("移除"),
            Style = (Style)FindResource("DangerButton"),
            Padding = new Thickness(12, 0, 12, 0),
            Height = 28,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        remove.Click += (_, e) =>
        {
            e.Handled = true;
            App.Favorites.Remove(item.ProjectType, item.ProjectId);
            ShowFavorites();
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(open);
        buttons.Children.Add(remove);

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(icon);
        row.Children.Add(buttons);
        row.Children.Add(text);

        var card = new Border
        {
            Style = (Style)FindResource("Card"),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = row,
        };
        // 点整行也能进详情页（按钮那两块自己 Mark 了 Handled，不会误触发）
        card.MouseLeftButtonUp += (_, _) => OpenFavorite(item);
        return card;
    }

    /// <summary>
    /// 从收藏夹跳到那个项目：先切到它自己那一栏（模组 / 整合包 / 资源包 / 光影包），
    /// 再把收藏里存的那份搜索命中塞给详情页。
    /// </summary>
    private void OpenFavorite(Core.Services.FavoriteItem item)
    {
        var type = ModContentType.ForProjectType(item.ProjectType);
        if (type is null)
        {
            return;
        }

        // 切左栏（会走 OnNavSelectionChanged → ShowCategory → ShowContentPane）
        SelectNavKey(type.NavKey);

        if (_contentPages.TryGetValue(type.ProjectType, out var page))
        {
            page.OpenDetailFromFavorite(item.Hit);
        }
    }

    /// <summary>把左栏选中项切到某个 key（找不到就算了）。</summary>
    private void SelectNavKey(string navKey)
    {
        foreach (var item in NavList.Items)
        {
            if (item is ListBoxItem row && row.Tag is string key && key == navKey)
            {
                NavList.SelectedItem = item;
                return;
            }
        }
    }

    private static string TextOfCategory(string key)
    {
        foreach (var (_, items) in Nav)
        {
            foreach (var (itemKey, text) in items)
            {
                if (itemKey == key)
                {
                    return text;
                }
            }
        }
        return key;
    }

    /// <summary>
    /// 「社区资源 → 模组 / 资源包 / 光影包」：挂共用页（`ModsPage`）的**对应类型那一份**。
    ///
    /// ⚠️ 2026-10 之前这里只挂模组那一栏，理由是"那一页的搜索写死了 projectType=mod，
    ///    给光影包挂上搜出来的还是 mod，那是在骗人"。现在 `ModsPage` 认类型参数了
    ///    （搜索带对应的 `project_type` facet、标题/提示/落点都跟着走），
    ///    所以三栏都能挂了 —— 当时的顾虑已经不成立。
    /// </summary>
    private void ShowContentPane(ModContentType type)
    {
        if (type.Target == ModInstallTarget.ModpackInstance)
        {
            ShowModpacksPane();
            return;
        }

        EnsureContentPage(type, ModsHost);
        ShowPane(ModsHost);
    }

    /// <summary>
    /// 「社区资源 → 整合包」：下面那块是 Modrinth 上搜整合包（共用页，类型 modpack）。
    ///
    /// ⚠️ 上面那张"从本地文件装"的卡**平时是收起来的**（`ModpackPane` 在 XAML 里
    ///    就是 Collapsed）：它占一整块地方，平时什么都不干。只有真的拖进来一个包时
    ///    才由 <see cref="OfferLocalPack"/> 展开 —— 拖放本身走**主窗口那一级**，
    ///    所以在「启动」「下载」任何页面上把 zip 丢进来都算数。
    /// </summary>
    private void ShowModpacksPane()
    {
        EnsureContentPage(ModContentType.Modpack, ModpackBrowseHost);
        ShowPane(ModpacksHost);
    }

    /// <summary>
    /// 取（没有就建）某一栏的共用页。`host` 是它要挂进去的那个坑。
    ///
    /// ⚠️ `--query` 只在**建页那一刻**喂一次：那是给自动化截图用的
    ///    （见 <see cref="SetInitialQuery"/>），用户自己来回切栏时不该被反复搜一遍。
    /// </summary>
    private ModsPage EnsureContentPage(ModContentType type, System.Windows.Controls.ContentControl host)
    {
        if (!_contentPages.TryGetValue(type.ProjectType, out var page))
        {
            page = new ModsPage(type);
            _contentPages[type.ProjectType] = page;
            host.Content = page;

            // ⚠️ 整合包那一栏上面压着"从本地文件装"那张卡（平时是收起的）：
            //    进了详情页就把它收起来，不收的话详情页只剩三四行版本的位置
            //    （详情页本身是"整页"设计的）。
            // ⚠️ 返回列表时**不能无条件放回来**：那张卡默认是隐藏的，
            //    只有"这次真的拖进来过一个包"（`_packSummary` 非空）才该重新出现 ——
            //    否则用户点进一个包再返回，凭空多出一块从没见过的卡片。
            if (type.Target == ModInstallTarget.ModpackInstance)
            {
                page.DetailOpenedChanged += (_, opened) =>
                    ModpackPane.Visibility = opened || _packSummary is null
                        ? Visibility.Collapsed
                        : Visibility.Visible;
            }

            // `--query` 只喂**建页那一刻**（自动化截图用）。⚠️ 空串也算给了 ——
            // 那是"空查询按下载量"那一屏，没有关键词但要真搜一次。
            if (_initialQuery is not null)
            {
                page.RunQuery(_initialQuery);
            }
        }
        return page;
    }

    /// <summary>
    /// `--query &lt;关键词&gt;`：把关键词转给嵌进来的社区资源页（主窗口在建完本页、
    /// 切到某一栏之前调）。**必须在 `ShowSection` 之前调** —— 页面是切栏那一刻建的。
    /// </summary>
    public void SetInitialQuery(string? query) => _initialQuery = query;

    /// <summary>
    /// `--mc &lt;版本号&gt;`：安装选项页**认这一版**，不用最新正式版。
    ///
    /// ⚠️ 为什么需要：安装包那几家的版本列表**跟着游戏版本走**（Legacy Fabric 只有
    ///    1.8~1.14、LiteLoader 只到 1.12.2、Cleanroom 只认 1.12.2），
    ///    而截图默认挑"最新正式版" —— 那些分组永远是空的，等于没验过。
    ///    这跟 `--query` 一样，是**只给自动化截图用**的入口。
    /// </summary>
    public void SetInitialMcVersion(string? version) => _initialMcVersion = version;

    // ============================================================
    //  版本页：拉清单
    // ============================================================

    /// <summary>
    /// 清单只拉一次：已经拉到过就直接返回。
    /// 「刷新」会先把 <c>_manifestTask</c> 清掉，所以它照样能强制重拉。
    /// </summary>
    private Task EnsureManifestAsync() => _manifestTask ??= LoadManifestAsync();

    private async Task LoadManifestAsync()
    {
        VersionStatus.Text = Localizer.Tr("正在获取版本列表…");
        RefreshButton.IsEnabled = false;
        // 加载条：264 KB 的清单要联网，快的时候几百毫秒、慢的时候十几秒 ——
        // 光靠状态行一句话，用户分不清"在转"还是"卡死了"
        ManifestLoading.ShowState(Localizer.Tr("正在获取版本列表…"));
        var ok = false;
        try
        {
            var (data, info) = await _manifest.LoadAsync();
            if (data is null)
            {
                // ⚠️ 这句话是 1.0.0 的原文案，直接用它 —— "已经下载过的版本不受影响"
                //    这句能让用户知道**这不是他的问题**，本地的版本没坏
                VersionStatus.Text = Localizer.Tr(
                    "拿不到版本列表。请稍后再试，或者换个网络（必要时用代理 / VPN）。已经下载过的版本不受影响。");
                return;
            }

            await ApplyManifestAsync(data, info);
            ok = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or InvalidOperationException or IOException
                                      or System.Text.Json.JsonException)
        {
            VersionStatus.Text = Localizer.Tr("拿不到版本列表：{err}", ("err", ex.Message));
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            // ⚠️ 放 finally：失败的那两条路也得把加载条收掉 ——
            //    失败原因由状态行说，加载条不抢这个活（留着转反而像还在加载）
            ManifestLoading.HideWithDelay();
            // ⚠️ 成功就把任务留着 —— 主窗口每次切到本页都会调 PrepareLoaderSection()，
            //    不留的话每切一次就重读一遍 264 KB 的清单缓存。
            //    失败才清掉，这样下次切回来 / 点刷新还能再试。
            if (!ok)
            {
                _manifestTask = null;
            }
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        // 重新拉一遍（覆盖掉上次那个已经完成的任务）
        _manifestTask = LoadManifestAsync();
    }

    private async Task ApplyManifestAsync(JsonObject data, ManifestInfo info)
    {
        // 留着这一份：装完东西要重扫本地版本，"本地版本"那一组得跟着重算
        _manifestData = data;
        _manifestInfo = info;

        var buckets = VersionGroupOrder.ToDictionary(name => name, _ => new List<VersionRowItem>(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;

        foreach (var node in data["versions"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject entry)
            {
                continue;
            }
            var id = entry["id"]?.GetValue<string>() ?? string.Empty;
            if (id.Length == 0 || !seen.Add(id))
            {
                continue;
            }
            total++;

            var type = entry["type"]?.GetValue<string>() ?? string.Empty;
            if (!TypeGroup.TryGetValue(type, out var group))
            {
                // 以后新增的 type 先不放进任何组，免得列表里冒出没归类的项
                continue;
            }

            var releaseTime = entry["releaseTime"]?.GetValue<string>() ?? string.Empty;
            buckets[group].Add(new VersionRowItem(
                id, id, PublishedText(releaseTime), VersionIcons.NameFor(type),
                new VersionTarget(id, id, releaseTime)));
        }

        foreach (var name in VersionGroupOrder)
        {
            // 清单本来就是新的在前；这里再按发布时间排一次，是为了不依赖上游的顺序。
            var sorted = buckets[name]
                .OrderByDescending(x => (x.Payload as VersionTarget)?.ReleaseTime ?? string.Empty,
                    StringComparer.Ordinal)
                .ToList();
            buckets[name] = sorted;

            var group = _versionGroups[name];
            group.SetTitle(name);
            group.SetItems(sorted, DpiScale);
            group.SetExpanded(name == "正式版");
            // ⚠️ 空组不显示：显示出来只是一行占地方的字（1.0.0 也是这么做的）
            group.Visibility = sorted.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        FillLatest(LatestReleaseHost, buckets["正式版"].FirstOrDefault(), "最新正式版");
        FillLatest(LatestSnapshotHost, buckets["快照"].FirstOrDefault(), "最新预览版");

        var tail = info.Stale ? Localizer.Tr("（版本清单来自缓存，可能不是最新的）") : string.Empty;
        VersionStatus.Text = Localizer.Tr("共 {n} 个版本", ("n", total)) + tail;
    }

    private void FillLatest(StackPanel host, VersionRowItem? entry, string kind)
    {
        host.Children.Clear();
        if (entry is null)
        {
            return;
        }

        // ⚠️ 时间是**清单里的原始串**（`2026-09-15T11:23:02+00:00`），
        //    跟列表里那几行一样要格式化，不然这里会露出一截 ISO 时间戳
        var time = FormatReleaseTime((entry.Payload as VersionTarget)?.ReleaseTime ?? string.Empty);
        var item = entry with
        {
            Meta = Localizer.Tr("{kind}，发布于 {time}", ("kind", Localizer.Tr(kind)), ("time", time)),
        };

        var row = new VersionRow();
        row.Bind(item, DpiScale);
        row.Activated += OnVersionActivated;
        host.Children.Add(row);
    }

    /// <summary>`2026-09-15T11:23:02+00:00` → `2026-09-15 11:23`（不换算时区，跟 PCL 一样）。</summary>
    private static string FormatReleaseTime(string raw)
    {
        var text = raw ?? string.Empty;
        return text.Length >= 16 && text[10] == 'T'
            ? $"{text[..10]} {text.Substring(11, 5)}"
            : text;
    }

    private static string PublishedText(string raw)
        => Localizer.Tr("发布于 {time}", ("time", FormatReleaseTime(raw)));

    // ============================================================
    //  版本页 → 安装选项页
    // ============================================================

    /// <summary>点了一行版本：标成选中 → 进安装选项页（1.0.0 就是这个流程）。</summary>
    private void OnVersionActivated(object? sender, VersionRowItem item)
    {
        SelectVersion(item);
        ShowPane(InstallPane);
    }

    private void SelectVersion(VersionRowItem item, bool fetchLoaders = true)
    {
        _selectedVersion = item;
        _selectedLoaderKey = string.Empty;
        _selectedLoaderVersion = string.Empty;
        // 换游戏版本 = 重新开始选：OptiFine / Fabric API / 手改过的名字全部作废
        // （PCL 的 `EnterSelectPage()` 也是这么重置的）
        _selectedOptiFineVersion = string.Empty;
        _selectedFabricApiVersion = string.Empty;
        _fabricApiAutoPicked = false;
        _fabricApiLatest = string.Empty;
        _fabricApiLoaded = false;
        _versionNameCustom = string.Empty;

        foreach (var group in _versionGroups.Values)
        {
            group.SetPicked(item.Id);
        }

        // 名字栏左边那个图标：先按原版版本给（选了加载器之后换成加载器的，见 RefreshNameIcon）

        InstallTargetText.Text = Localizer.Tr("将安装到: {path}",
            ("path", App.Services.Games.MinecraftDir.Length > 0
                ? App.Services.Games.MinecraftDir
                : Localizer.Tr("（没设置游戏目录）")));

        ResetLoaderGroups();
        RefreshVersionName();
        RefreshFooter();
        SetInstallStatus(string.Empty);

        if (fetchLoaders)
        {
            _loaderTask = LoadLoaderCatalogAsync(item);
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => ShowPane(VersionsPane);

    // ============================================================
    //  安装选项页：五家加载器
    // ============================================================

    private void ResetLoaderGroups()
    {
        _loaderRows = Array.Empty<LoaderRow>();
        foreach (var (key, _) in LoaderOrder)
        {
            var group = _loaderGroups[key];
            group.SetItems(Array.Empty<VersionRowItem>(), DpiScale);
            group.SetNote(Localizer.Tr(LoaderLoadingNote));
            group.SetExpanded(false);
            group.SetPicked(null);
        }

        if (_fabricApiGroup is not null)
        {
            _fabricApiGroup.SetItems(Array.Empty<VersionRowItem>(), DpiScale);
            _fabricApiGroup.SetNote(string.Empty);
            _fabricApiGroup.SetPicked(null);
            _fabricApiGroup.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 拉五家的版本列表。
    ///
    /// ⚠️ 一家的接口挂了**不能**让其它四家都不显示 —— 引擎里每一家是单独 try 的，
    ///    状态落在 `LoaderRow.State` 上，这里照实说。
    /// </summary>
    private async Task LoadLoaderCatalogAsync(VersionRowItem version)
    {
        var target = version.Payload as VersionTarget;
        var mcVersion = target?.MinecraftVersion ?? version.Id;

        // 加载条：五家要发五个请求，全挂的时候要等超时（十几秒）——
        // 那段时间页面上如果只有五个"正在获取版本列表…"的副标题，看着像卡死了
        LoaderLoading.ShowState(Localizer.Tr("正在获取模组加载器…"));

        try
        {
            var rows = await App.Services.Loaders.CatalogAsync(mcVersion);
            // 拉的过程中用户可能已经换了版本，那就别把旧结果填进去
            if (!ReferenceEquals(_selectedVersion, version))
            {
                return;
            }
            _loaderRows = rows;
            FillLoaderGroups();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or InvalidOperationException or IOException)
        {
            if (!ReferenceEquals(_selectedVersion, version))
            {
                return;
            }
            // 一家都拉不到的时候，每一行都说"取不到版本列表"（RefreshInstallRows 之外的那条路：
            // 这里是"整个清单都没拿到"，连应用性都判断不了）
            foreach (var (key, _) in LoaderOrder)
            {
                _loaderGroups[key].SetNote(Localizer.Tr(LoaderFailNote));
            }
            SetInstallStatus(Localizer.Tr("拿不到加载器列表：{err}", ("err", ex.Message)));
        }
        finally
        {
            // ⚠️ 放 finally：失败 / 用户中途切了版本这两条路也得收 ——
            //    不收的话会留一条永远转下去的加载条。
            LoaderLoading.HideWithDelay();
        }
    }

    private void FillLoaderGroups()
    {
        var target = _selectedVersion?.Payload as VersionTarget;
        var mcVersion = target?.MinecraftVersion ?? string.Empty;

        foreach (var (key, title) in LoaderOrder)
        {
            var row = _loaderRows.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.Ordinal));
            var group = _loaderGroups[key];

            group.SetTitle(title);
            group.SetItems(BuildLoaderItems(key, mcVersion, row), DpiScale);
            group.SetExpanded(false);
            group.SetPicked(null);
        }

        // Fabric API 的版号要单独问一次 Modrinth（不跟加载器清单一起拉）
        _ = LoadFabricApiAsync(mcVersion);

        RefreshInstallRows();
    }

    /// <summary>
    /// 安装页每一行的**状态**（PCL 那一页的核心：不是"列出来就完了"）。
    ///
    /// 一行只有五种长相（照 PCL 的 `Load*GetError()` + `ReloadSelected()`）：
    ///   1. 整行不显示 —— 这一家根本不支持这个游戏版本（26.3 上没有 LiteLoader）；
    ///   2. 灰色一句原因 —— "与 Fabric 不兼容" / "需要 Fabric"；
    ///   3. 灰色一句"无可用版本"—— 支持这个版本，但这家还没发；
    ///   4. 正常（可以点开选版本）；
    ///   5. 已选 —— 显示版本号 + 右边的叉。
    ///
    /// ⚠️ 顺序不能乱：**先判能不能（兼容），再说有没有版本**。反过来的话
    ///    "Fabric 不兼容还显示无可用版本"会让人以为换个版本就能装。
    /// </summary>
    private void RefreshInstallRows()
    {
        var mcVersion = SelectedMinecraftVersion;

        foreach (var (key, title) in LoaderOrder)
        {
            var group = _loaderGroups[key];
            var row = _loaderRows.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.Ordinal));

            // ① 这一家跟这个游戏版本根本没关系 → 整行不显示
            if (!LoaderCompat.IsApplicable(key, mcVersion))
            {
                group.Visibility = Visibility.Collapsed;
                continue;
            }
            group.Visibility = Visibility.Visible;

            // ② 跟已经选上的那家冲突 → 灰掉 + 说明原因
            var conflict = LoaderCompat.IncompatibilityReason(key, SelectedPrimaryKey, mcVersion);
            if (conflict is not null)
            {
                group.SetUnavailable(true);
                group.SetNote(Localizer.Tr(conflict.Template, ("name", conflict.ArgTitle)));
                group.SetClearVisible(false);
                continue;
            }

            group.SetUnavailable(false);
            group.SetClearVisible(true);

            // ③ 兼容但没版本 / 已经选上了 → 两种说法
            var picked = key == "optifine" ? _selectedOptiFineVersion : (
                string.Equals(key, _selectedLoaderKey, StringComparison.Ordinal)
                    ? _selectedLoaderVersion
                    : string.Empty);
            group.SetNote(picked.Length > 0
                ? picked
                : LoaderNote(key, title, row));
        }

        RefreshFabricApiRow(mcVersion);
        RefreshInstallWarning();
    }

    /// <summary>
    /// Fabric API 那一行（PCL 里它排在 Fabric 后面，**选了 Fabric 就自动带上**）。
    ///
    /// ⚠️ 没选 Fabric / Quilt 时整行不显示：它是加载器的配套 mod，不是"另一家加载器"，
    ///    一直摆在那儿只会让人以为可以单独装。
    /// </summary>
    private void RefreshFabricApiRow(string mcVersion)
    {
        var group = _fabricApiGroup;
        if (group is null)
        {
            return;
        }

        var primary = SelectedPrimaryKey;
        var applicable = primary is "fabric" or "quilt";
        group.Visibility = applicable ? Visibility.Visible : Visibility.Collapsed;
        if (!applicable)
        {
            return;
        }

        group.SetUnavailable(false);
        group.SetClearVisible(true);

        var picked = _selectedFabricApiVersion.Length > 0;
        if (_fabricApiLatest.Length == 0)
        {
            // ⚠️ 别拿 `_fabricApiTask.IsCompleted` 判断"拉完了没有"：
            //    最后一次刷新就发生在任务完成之前（回调里刷的），所以界面会一直停在
            //    "正在获取版本…"上（实测，Quilt 那页就是这样）。用一个显式的"拉过了"标记。
            group.SetNote(_fabricApiLoaded
                ? Localizer.Tr("没有可用版本")
                : Localizer.Tr("正在获取版本…"));
        }
        else
        {
            group.SetNote(picked ? _selectedFabricApiVersion : Localizer.Tr("可以添加"));
        }

        // 自动带上（**只有 Fabric 会自动**：PCL 是"选了 Fabric 就连 Fabric API 一起选"；
        // Quilt 那边显示"可以添加"，因为它自己那套是 QFAPI / QSL）
        if (!picked && !_fabricApiAutoPicked && _fabricApiLatest.Length > 0
            && LoaderCompat.AutoBundledWith("fabric_api", primary))
        {
            _fabricApiAutoPicked = true;
            _selectedFabricApiVersion = _fabricApiLatest;
            group.SetPicked(_fabricApiLatest);
            group.SetNote(_fabricApiLatest);
            RefreshVersionName();
            RefreshInstallWarning();
        }

        _ = mcVersion;   // 版本号是拉列表时用掉的（见 LoadFabricApiAsync）
    }

    /// <summary>
    /// 行内警告条（PCL 的 `HintFabricAPI` / `HintLegacyFabricAPI` 那几条）。
    ///
    /// 我们的口径：
    ///   · 选了 Fabric 却没装 Fabric API → 红字（模组基本都要它）；
    ///   · 选了 Quilt → 提醒 QFAPI / QSL 还没接，可以先用 Fabric API 顶上
    ///     （PCL 原话："如果不安装 QFAPI / QSL，大多数模组都会无法使用"）。
    /// </summary>
    private void RefreshInstallWarning()
    {
        var primary = SelectedPrimaryKey;
        string? warning = null;

        if (primary == "fabric" && _selectedFabricApiVersion.Length == 0)
        {
            warning = "没装 Fabric API —— 大多数模组都会用不了，点上面那行把它加上";
        }
        else if (primary == "quilt")
        {
            warning = _selectedFabricApiVersion.Length == 0
                ? "Quilt 一般要装 QFAPI / QSL（我们还没接）；可以先勾上 Fabric API 顶一下"
                : "Quilt 一般要 QFAPI / QSL，我们还没接 —— 现在勾的是 Fabric API（大多数模组够用）";
        }

        if (warning is null)
        {
            InstallWarning.Visibility = Visibility.Collapsed;
            InstallWarningText.Text = string.Empty;
            return;
        }

        InstallWarningText.Text = Localizer.Tr(warning);
        InstallWarning.Visibility = Visibility.Visible;
    }

    /// <summary>当前选中的**主加载器**（OptiFine 不算主加载器，它有自己的槽位）。</summary>
    private string SelectedPrimaryKey => _selectedLoaderKey;

    /// <summary>当前选中的游戏版本号（`26.3`）。</summary>
    private string SelectedMinecraftVersion
        => (_selectedVersion?.Payload as VersionTarget)?.MinecraftVersion ?? _selectedVersion?.Id ?? string.Empty;

    // ============================================================
    //  版本名（用户可以随便改）
    // ============================================================

    /// <summary>
    /// 按 PCL 的规则拼出默认版本名（`26.3-Fabric_0.20.0`）。
    ///
    /// ⚠️ 用户手改过之后**不再自动改**（PCL 的 `isSelectNameEdited`）——
    ///    不然改完名字、再点一下加载器，名字又被冲掉了。
    /// </summary>
    private void RefreshVersionName()
    {
        if (_versionNameCustom.Length > 0)
        {
            return;   // 用户改过了，别动它
        }

        var parts = new List<(string Key, string Version)>();
        if (_selectedLoaderKey.Length > 0)
        {
            parts.Add((_selectedLoaderKey, _selectedLoaderVersion));
        }
        if (_selectedOptiFineVersion.Length > 0)
        {
            // OptiFine 在 PCL 的名字里是"去掉游戏版本前缀 + 空格换下划线"
            parts.Add(("optifine", _selectedOptiFineVersion.Replace(' ', '_')));
        }

        SetVersionNameText(LoaderCatalog.ComposeVersionName(SelectedMinecraftVersion, parts));
        RefreshNameIcon();
    }

    /// <summary>
    /// 名字栏左边那个图标 —— 跟着**当前选中的东西**变（用户 2026-10："然后自动显示图标？"）。
    ///
    /// · 选了加载器 → 用那家的图标（Forge 铁砧 / NeoForge 狐狸 / Quilt 拼布 / LabyMod…）；
    /// · 只选了 OptiFine → OptiFine 的图标；
    /// · 什么都没选 → 原版那个方块（发布版草地 / 快照命令方块 / 远古圆石）。
    ///
    /// ⚠️ 图标名走 `VersionIcons.NameFor(loader: key)`，认不出来的加载器会退成空串 ——
    ///    那时候就用原版的图标，**不要**留一块空白（空图标比错的图标更难看）。
    /// ⚠️ 尺寸按 DPI 重画：位图是按物理像素生成的，缩放变了必须重新 Load
    ///    （见 `VersionIcons.Load` 的注释，1280×820 那套截图对比靠它）。
    /// </summary>
    private void RefreshNameIcon()
    {
        var loaderKey = _selectedLoaderKey.Length > 0
            ? _selectedLoaderKey
            : (_selectedOptiFineVersion.Length > 0 ? "optifine" : string.Empty);

        var iconName = loaderKey.Length > 0
            ? VersionIcons.NameFor(loader: loaderKey)
            : _selectedVersion?.IconName ?? string.Empty;

        // 加载器没有对应图标（比如 LiteLoader）→ 退回原版那一张
        if (iconName.Length == 0)
        {
            iconName = _selectedVersion?.IconName ?? string.Empty;
        }

        var icon = VersionIcons.Load(iconName, VersionIcons.ChipSize + 4, DpiScale);
        NameIcon.Source = icon;
        NameIcon.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>程序改 TextBox 时别把它当成"用户手改了"。</summary>
    private void SetVersionNameText(string text)
    {
        if (VersionNameBox.Text == text)
        {
            return;
        }
        _versionNameGuard = true;
        VersionNameBox.Text = text;
        _versionNameGuard = false;
        RefreshVersionNameHint();
    }

    private void OnVersionNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_versionNameGuard)
        {
            return;
        }
        _versionNameCustom = VersionNameBox.Text.Trim();
        RefreshVersionNameHint();
        RefreshFooter();
    }

    /// <summary>
    /// 版本名的合法性（PCL 那边是 `FolderNameValidator(versions 目录)`：
    /// 非法字符 + 重名）。
    ///
    /// 返回 null = 能用；否则是给人看的原因。
    /// </summary>
    private string? VersionNameProblem()
    {
        var name = VersionNameBox.Text.Trim();
        if (name.Length == 0)
        {
            return "版本名不能是空的";
        }
        // Windows 目录名不能带的字符（另加控制字符）
        if (name.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0
            || name.Any(char.IsControl))
        {
            return "版本名里有不能用在文件夹名里的字符（\\ / : * ? \" < > |）";
        }
        if (name.EndsWith('.') || name.EndsWith(' '))
        {
            return "版本名结尾不能是点或空格（Windows 建不出这种目录）";
        }
        // 保留名（CON / PRN / AUX / NUL / COM1…）
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL"
            || (stem.Length == 4 && stem.StartsWith("COM", StringComparison.Ordinal)
                && char.IsDigit(stem[3]))
            || (stem.Length == 4 && stem.StartsWith("LPT", StringComparison.Ordinal)
                && char.IsDigit(stem[3])))
        {
            return "这个名字是 Windows 的保留名，建不出目录";
        }

        var mcDir = App.Services.Games.MinecraftDir;
        if (mcDir.Length > 0 && Directory.Exists(Path.Combine(mcDir, "versions", name)))
        {
            return "已经有一个同名的版本了 —— 换个名字，或者先把那个删掉";
        }
        return null;
    }

    private void RefreshVersionNameHint()
    {
        var problem = VersionNameProblem();
        VersionNameHint.Text = problem is null
            ? Localizer.Tr("这个名字会用作 versions\\ 下的目录名，可以随便改")
            : Localizer.Tr(problem);
    }

    /// <summary>Fabric API 的最新版号（拉到之后把那一行铺出来并刷新状态）。</summary>
    private async Task LoadFabricApiAsync(string mcVersion)
    {
        _fabricApiTask = Task.Run(async () =>
        {
            var version = await App.Services.Loaders.FabricApiVersionAsync(mcVersion).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                // 期间用户可能换了版本 —— 换了就丢掉这次结果
                if (!string.Equals(mcVersion, SelectedMinecraftVersion, StringComparison.Ordinal))
                {
                    return;
                }
                _fabricApiLatest = version ?? string.Empty;
                _fabricApiLoaded = true;
                if (_fabricApiGroup is not null && _fabricApiLatest.Length > 0)
                {
                    _fabricApiGroup.SetItems(new[]
                    {
                        new VersionRowItem(
                            _fabricApiLatest,
                            $"Fabric API {_fabricApiLatest}",
                            Localizer.Tr("配套 mod · 装进版本目录的 mods\\ 里"),
                            "fabric",
                            null),
                    }, DpiScale);
                }
                RefreshInstallRows();
                RefreshFooter();
            });
        });
        await ((Task)_fabricApiTask).ConfigureAwait(false);
    }


    /// <summary>
    /// 一家的版本行。
    ///
    /// ⚠️ 加载器的版本行**没有"发布时间"**这东西，第二行小字由这里给：
    ///    「可用于 26.3」。测试版额外标一句 —— 引擎里 `Stable` 是专门留着的
    ///    （Forge 不分稳定/测试，Fabric / Quilt 分），丢了用户就不知道哪个是正式版。
    /// </summary>
    private static IReadOnlyList<VersionRowItem> BuildLoaderItems(string key, string mcVersion, LoaderRow? row)
    {
        if (row is null || row.State != LoaderState.Ok)
        {
            return Array.Empty<VersionRowItem>();
        }

        return row.Versions
            .Select(v =>
            {
                var meta = Localizer.Tr("可用于 {version}", ("version", mcVersion));
                if (!v.Stable)
                {
                    meta += "    ·    " + Localizer.Tr("测试版");
                }
                return new VersionRowItem(
                    v.Version, v.Version, meta, VersionIcons.NameFor(loader: key), v);
            })
            .ToList();
    }

    /// <summary>
    /// 标题右边那句副标题。四种说法，一个字都不能混：
    /// · 有版本 → 「最新版 66.0.10（共 9 个）」（装不了的再加一句"安装还没接"）
    /// · `NotImplemented` → 「安装还没接」
    /// · `Error` → 「取不到版本列表（网络或接口问题）」—— **不能**说成"还没做"
    /// · 0 个 → 「这个版本没有可用的 OptiFine」（这是正常情况，不是错误）
    /// </summary>
    private string LoaderNote(string key, string title, LoaderRow? row)
    {
        if (row is null)
        {
            // 引擎那张表里根本没有这一家（= 接口还没接上）。
            // ⚠️ 这里**不能**说"取不到版本列表"：那是网络问题，会让人反复重试，
            //    而事实是我们没做。
            return Localizer.Tr(LoaderNotImplementedNote);
        }

        switch (row.State)
        {
            case LoaderState.NotImplemented:
                return Localizer.Tr(LoaderNotImplementedNote);
            case LoaderState.Error:
                return Localizer.Tr(LoaderFailNote);
        }

        if (row.Count == 0)
        {
            return Localizer.Tr("这个版本没有可用的 {name}", ("name", title));
        }

        var tail = App.Services.Loaders.CanInstall(key) ? string.Empty : Localizer.Tr(NotInstallableTail);
        return Localizer.Tr("最新版 {v}（共 {n} 个）", ("v", row.Latest), ("n", row.Count)) + tail;
    }

    /// <summary>
    /// 点了某一家的某个版本。
    ///
    /// ⚠️ OptiFine 是**单独一个槽位**（PCL 的 `selectedOptiFine` 跟
    ///    `selectedLoaderName` 是两个字段）：它不占"主加载器"的位置，
    ///    所以选了 Forge 之后还能再选 OptiFine。
    /// ⚠️ 换主加载器时**连带清掉**不兼容的配套（比如 Fabric → Forge，
    ///    Fabric API 要跟着掉）—— PCL 的 `Fabric_Clear` 就是这么连锁清的。
    /// </summary>
    private void OnLoaderVersionActivated(string key, VersionRowItem item)
    {
        var mcVersion = SelectedMinecraftVersion;
        var conflict = LoaderCompat.IncompatibilityReason(key, SelectedPrimaryKey, mcVersion);
        if (conflict is not null)
        {
            // 按钮已经灰了，正常点不到；曲线操作（键盘/程序调用）进来也别硬装
            SetInstallStatus(Localizer.Tr(conflict.Template, ("name", conflict.ArgTitle)));
            return;
        }

        if (key == "optifine")
        {
            _selectedOptiFineVersion = item.Id;
        }
        else
        {
            _selectedLoaderKey = key;
            _selectedLoaderVersion = item.Id;
        }

        // 主加载器互斥：标了这一行，其余主加载器全取消（OptiFine 那个槽位单独算）
        foreach (var (groupKey, group) in _loaderGroups)
        {
            if (groupKey == "optifine" || groupKey == "fabric_api")
            {
                continue;
            }
            group.SetPicked(string.Equals(groupKey, _selectedLoaderKey, StringComparison.Ordinal) &&
                            _selectedLoaderKey.Length > 0
                ? _selectedLoaderVersion
                : null);
        }
        _loaderGroups["optifine"].SetPicked(
            _selectedOptiFineVersion.Length > 0 ? _selectedOptiFineVersion : null);

        // 选完就把列表收起来（PCL 的卡片也是这样：选中之后 `IsSwapped = true`）。
        // 不收的话下面那几行全被这一家的几十个版本顶出屏幕 ——
        // 而且"我选了哪一家"从收起的那一行就看得出来。
        _loaderGroups[key].SetExpanded(false);

        // 换了主加载器：配套的 Fabric API 如果已经不该存在，就清掉
        if (!LoaderCompat.AutoBundledWith("fabric_api", _selectedLoaderKey))
        {
            _selectedFabricApiVersion = string.Empty;
            _fabricApiAutoPicked = false;
            _fabricApiGroup?.SetPicked(null);
        }

        RefreshInstallRows();
        RefreshVersionName();
        RefreshFooter();
    }

    /// <summary>某一行点了「×」：把那一项从选择里去掉（连带清掉它的配套）。</summary>
    private void OnLoaderRowCleared(string key)
    {
        if (key == "optifine")
        {
            _selectedOptiFineVersion = string.Empty;
        }
        else if (key == "fabric_api")
        {
            _selectedFabricApiVersion = string.Empty;
            // ⚠️ 不清 `_fabricApiAutoPicked`：用户主动取消之后就别再抢回来
            //    （PCL 的 `FabricApi_Clear` 也不重置那个 flag）
        }
        else if (string.Equals(key, _selectedLoaderKey, StringComparison.Ordinal))
        {
            _selectedLoaderKey = string.Empty;
            _selectedLoaderVersion = string.Empty;
            _selectedFabricApiVersion = string.Empty;
            _fabricApiAutoPicked = false;
            _fabricApiGroup?.SetPicked(null);
        }

        // ⚠️ Fabric API 那一组**不在 `_loaderGroups` 里**：它是在 `LoaderOrder` 之外
        //    单独建的（存在 `_fabricApiGroup`）。所以这里不能直接 `_loaderGroups[key]` ——
        //    点它那行的叉会抛 KeyNotFoundException（2026-10-18 用户点到：报的就是这个）。
        //    ⚠️ 以后再加"不在 LoaderOrder 里的行"都走 `GroupFor`，别在这里点名字。
        GroupFor(key)?.SetPicked(null);
        RefreshInstallRows();
        RefreshVersionName();
        RefreshFooter();
    }

    /// <summary>
    /// 按 key 找那一组的控件（`LoaderOrder` 里的走字典，Fabric API 那种单独建的走它自己）。
    /// 找不到返回 null —— 界面上"没有这一行"比抛异常合理。
    /// </summary>
    private CollapsibleGroup? GroupFor(string key)
        => _loaderGroups.TryGetValue(key, out var group)
            ? group
            : (key == "fabric_api" ? _fabricApiGroup : null);

    private void RefreshFooter()
    {
        var problem = VersionNameProblem();
        InstallButton.IsEnabled = !_busy && problem is null;
        if (problem is not null)
        {
            InstallHint.Text = Localizer.Tr(problem);
            return;
        }

        if (_selectedLoaderKey.Length == 0 && _selectedOptiFineVersion.Length == 0)
        {
            InstallHint.Text = Localizer.Tr("还没有选择加载器 —— 直接点「开始下载」只会装原版");
            return;
        }

        // 组合安装（Forge + OptiFine）我们还没接：照实说，别假装能装
        if (_selectedLoaderKey.Length > 0 && _selectedOptiFineVersion.Length > 0)
        {
            InstallHint.Text = Localizer.Tr(
                "将安装 {name} {ver} + OptiFine {opti} —— **组合安装还没接**，现在只能单独装一家",
                ("name", LoaderTitle(_selectedLoaderKey)), ("ver", _selectedLoaderVersion),
                ("opti", _selectedOptiFineVersion));
            return;
        }

        if (_selectedOptiFineVersion.Length > 0)
        {
            InstallHint.Text = Localizer.Tr("将安装 OptiFine {ver}", ("ver", _selectedOptiFineVersion));
            return;
        }

        var title = LoaderTitle(_selectedLoaderKey);
        var line = App.Services.Loaders.CanInstall(_selectedLoaderKey)
            ? Localizer.Tr("将安装 {name} {ver}", ("name", title), ("ver", _selectedLoaderVersion))
            : Localizer.Tr("{name} 的安装还没接", ("name", title));
        if (_selectedFabricApiVersion.Length > 0)
        {
            line += "    ·    " + Localizer.Tr("顺带装 Fabric API {ver}",
                ("ver", _selectedFabricApiVersion));
        }
        InstallHint.Text = line;
    }

    private static string LoaderTitle(string key)
        => LoaderOrder.FirstOrDefault(x => x.Key == key).Title ?? key;

    private void SetInstallStatus(string text)
    {
        InstallStatus.Text = text;
        InstallStatus.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ============================================================
    //  开始下载
    // ============================================================

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (_selectedVersion is null)
        {
            SetInstallStatus(Localizer.Tr("先回上一页选一个游戏版本"));
            return;
        }

        // ⚠️ "这家还没接"要在**点下去之前**就说清楚，不能假装在装：
        //    用户等半天等来一句"还没做"，比当场被告知糟得多
        if (_selectedLoaderKey.Length > 0 && !App.Services.Loaders.CanInstall(_selectedLoaderKey))
        {
            var title = LoaderTitle(_selectedLoaderKey);
            SetInstallStatus(Localizer.Tr(
                "{name} 的安装还没接 —— 换一家能装的，或者回上一页直接装原版", ("name", title)));
            return;
        }

        // ⚠️ 组合安装（Forge + OptiFine 这种）还没接：引擎一次只装一家。
        //    这里**必须**在动手前拦住，否则会装出半个组合，用户还以为成功了。
        if (_selectedLoaderKey.Length > 0 && _selectedOptiFineVersion.Length > 0)
        {
            SetInstallStatus(Localizer.Tr(
                "组合安装还没接 —— 现在只能单独装 {name} 或单独装 OptiFine",
                ("name", LoaderTitle(_selectedLoaderKey))));
            return;
        }

        var nameProblem = VersionNameProblem();
        if (nameProblem is not null)
        {
            SetInstallStatus(Localizer.Tr(nameProblem));
            return;
        }

        var target = _selectedVersion.Payload as VersionTarget;
        var mcVersion = target?.MinecraftVersion ?? _selectedVersion.Id;
        var mcDir = App.Services.Games.MinecraftDir;

        // 用户起的名字就是版本目录名（PCL 的 `targetInstanceName`）
        var versionId = VersionNameBox.Text.Trim();

        _busy = true;
        InstallButton.IsEnabled = false;

        // 下载进度窗口（= 1.0.0 那个"正在安装 …"）。装原版和装加载器共用它：
        // 两条路都是"下一批文件 → 再跑安装器"，用户看到的进度应该是同一套。
        var displayName = _selectedLoaderKey.Length > 0
            ? $"{LoaderTitle(_selectedLoaderKey)} {mcVersion}"
            : (_selectedOptiFineVersion.Length > 0 ? $"OptiFine {mcVersion}" : mcVersion);
        var window = App.OpenDownloadWindow(displayName);

        try
        {
            var progress = new Progress<LaunchProgress>(p =>
            {
                SetInstallStatus(p.Message);
                // 窗口里那句副标题：没有真的在下载时（跑安装器、校验中）就是它
                window.SetStageNote(p.Message);
            });

            if (_selectedLoaderKey.Length == 0 && _selectedOptiFineVersion.Length == 0)
            {
                // 没选加载器 = 只装原版（1.0.0 的原话）
                var result = await App.Services.Downloads.InstallVanillaAsync(mcVersion, mcDir, progress);
                SetInstallStatus(result.Message);
                window.Finish(result.Message);
                if (result.Ok)
                {
                    App.Settings.LastVersion = result.VersionId ?? App.Settings.LastVersion;
                }
            }
            else
            {
                // OptiFine 单独装：它自己也是一家加载器，走同一条安装路（key = optifine）
                var request = new LoaderInstallRequest(
                    MinecraftVersion: mcVersion,
                    LoaderKey: _selectedLoaderKey.Length > 0 ? _selectedLoaderKey : "optifine",
                    LoaderVersion: _selectedLoaderKey.Length > 0
                        ? _selectedLoaderVersion
                        : _selectedOptiFineVersion,
                    MinecraftDir: mcDir,
                    VersionId: versionId,
                    // 捆绑安装 Fabric API：PCL 那边就是"默认勾上 → 塞进安装请求"
                    InstallFabricApi: _selectedFabricApiVersion.Length > 0);

                var result = await App.Services.Loaders.InstallAsync(request, progress);

                // 引擎回 NotImplemented 时**照实说**，别把消息当成"装好了"
                var message = result.NotImplemented
                    ? Localizer.Tr("{name} 的安装还没接", ("name", LoaderTitle(_selectedLoaderKey)))
                    : result.Message;
                SetInstallStatus(message);
                window.Finish(message);

                if (result.Ok)
                {
                    App.Settings.LastVersion = result.VersionId ?? App.Settings.LastVersion;
                }
            }

            // 装完要重扫，否则「版本管理」页看不见它
            await RefreshAfterInstallAsync();
        }
        catch (OperationCanceledException)
        {
            SetInstallStatus(Localizer.Tr("已取消"));
            window.Finish(Localizer.Tr("已取消"));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            SetInstallStatus(Localizer.Tr("安装失败：{err}", ("err", ex.Message)));
            window.Finish(Localizer.Tr("安装失败：{err}", ("err", ex.Message)));
        }
        finally
        {
            _busy = false;
            InstallButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 装完重扫一遍本地版本（别的页要看到刚装的那个），并把清单列表重铺一遍。
    ///
    /// ⚠️ 下载页本身**不显示本地版本**（见 <see cref="VersionGroupOrder"/> 的注释），
    ///    但重扫不能省：`Games.Installed` 是全局的，版本页 / 启动页 / 模组页都读它。
    /// </summary>
    private async Task RefreshAfterInstallAsync()
    {
        try
        {
            await App.Services.Games.RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidOperationException)
        {
            _ = ex;
        }

        if (_manifestData is not null && _manifestInfo is not null)
        {
            await ApplyManifestAsync(_manifestData, _manifestInfo);
        }
    }

    // ============================================================
    //  整合包（社区资源 → 整合包）：拖进来 / 选文件 → 认格式 → 装
    // ============================================================

    /// <summary>刚拖进来（或选中）的那个包是什么（null = 还没有包）。</summary>
    private PackSummary? _packSummary;

    /// <summary>它的字节。检测时已经读过一次，装的时候直接用（别读第二遍）。</summary>
    private byte[]? _packBlob;

    private void OnBrowseModpackClick(object sender, MouseButtonEventArgs e) => PickModpackFile();

    private void PickModpackFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localizer.Tr("选择整合包"),
            // ⚠️ `.zip` 必须放进过滤器：国内发的大半是 zip（CurseForge 的、或者纯覆盖包），
            //    只给 .mrpack 的话用户会觉得"我这包它不认"。
            Filter = "整合包 (*.mrpack;*.zip)|*.mrpack;*.zip|Modrinth 整合包 (*.mrpack)|*.mrpack|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
        {
            OfferLocalPack(dialog.FileName);
        }
    }

    // ---------- 拖放 ----------
    //
    // ⚠️ 这三种事件的 `Effects` 要**处处设**：只在 Drop 里判断的话，鼠标经过时
    //    光标一直是"禁止"图标，用户会以为这块不能拖。

    private void OnPackDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var ok = PackDrop.TryGetSinglePack(e.Data, out _);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropZoneActive(ok);
    }

    private void OnPackDragLeave(object sender, DragEventArgs e) => SetDropZoneActive(false);

    private void OnPackDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        SetDropZoneActive(false);
        if (PackDrop.TryGetSinglePack(e.Data, out var path))
        {
            OfferLocalPack(path);
        }
    }

    /// <summary>拖到上面时把框点亮（用主题里的强调色，跟"选中"是一套视觉）。</summary>
    private void SetDropZoneActive(bool active)
    {
        ModpackDropZone.BorderBrush = (Brush)FindResource(
            active ? "AccentBorderBrush" : "BorderBrush");
        ModpackDropZone.Background = active
            ? (Brush)FindResource("AccentBgBrush")
            : Brushes.Transparent;
        ModpackDropZone.BorderThickness = new Thickness(active ? 2 : 1);
        // 描边变粗会让里面的内容跳 1px，把内边距让出来
        ModpackDropZone.Padding = new Thickness(16, active ? 13 : 14, 16, active ? 13 : 14);
    }

    /// <summary>
    /// 把一个本地包交给这张卡：**认格式 → 填预览 → 决定能不能装**。
    ///
    /// 拖放、点选、主窗口转发（把 zip 直接拖到窗口上）全走这里 ——
    /// 三个入口共用一条判断，才不会出现"拖进来能装、点选装不了"这种事。
    /// </summary>
    public void OfferLocalPack(string path)
    {
        // 拖进来一个包 = 这张卡该现身了（平时它是收起的，见 ShowModpacksPane 的注释）
        ModpackPane.Visibility = Visibility.Visible;

        _packSummary = null;
        _packBlob = null;
        _modpackPath = path;
        InstallModpackButton.IsEnabled = false;

        var fileName = Path.GetFileName(path);
        byte[] blob;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                ModpackPreview.Text = Localizer.Tr("找不到这个文件：{path}", ("path", path));
                return;
            }
            if ((long)info.Length > ModpackInstaller.MaxMrpackBytes)
            {
                ModpackPreview.Text = Localizer.Tr("这个包太大了，不敢装。");
                return;
            }
            blob = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModpackPreview.Text = Localizer.Tr("读不了这个文件：{err}", ("err", ex.Message));
            return;
        }

        var summary = PackArchive.Detect(blob);
        _packSummary = summary;
        _packBlob = blob;

        var lines = new List<string> { fileName };
        switch (summary.Kind)
        {
            case PackKind.Modrinth:
            {
                lines.Add(Localizer.Tr("Modrinth 整合包（.mrpack）"));
                // 用服务层的 Inspect 再解析一次（它带路径安全检查与"跳过服务端文件"的计数）
                var plan = App.Services.Modpacks.Inspect(blob, out var error);
                if (plan is null)
                {
                    lines.Add(error);
                    break;
                }
                // ⚠️ 不把 plan 存进字段：装的时候服务层会按路径**重新读一遍**
                //    （用户可能在这中间把文件删了/换了），存一份旧的只会误导。
                InstallModpackButton.IsEnabled = true;

                var (loaderKey, loaderVersion) = plan.Loader;
                lines.Add($"{plan.Name}    ·    {plan.VersionId}");
                lines.Add(Localizer.Tr("要装 {n} 个文件（{size}）",
                    ("n", plan.Files.Count), ("size", DownloadManager.HumanSize(plan.TotalBytes))));
                lines.Add(Localizer.Tr("Minecraft {version}    ·    {loader}",
                    ("version", plan.Minecraft.Length > 0 ? plan.Minecraft : "?"),
                    ("loader", loaderKey.Length > 0
                        ? $"{loaderKey} {loaderVersion}"
                        : Localizer.Tr("原版"))));
                if (plan.Skipped > 0)
                {
                    lines.Add(Localizer.Tr("{n} 个文件是服务端专用的，会跳过", ("n", plan.Skipped)));
                }
                if (plan.Overrides.Count > 0)
                {
                    lines.Add(Localizer.Tr("带 {n} 个覆盖层文件（配置、资源等）", ("n", plan.Overrides.Count)));
                }
                break;
            }

            case PackKind.Overlay:
            {
                lines.Add(Localizer.Tr("覆盖包（没有清单：只有 mods / config 这类文件）"));
                var root = TargetGameDir(out var version);
                if (root.Length == 0 || version is null)
                {
                    lines.Add(Localizer.Tr("还没有装任何版本 —— 覆盖包要解压进一个已有的版本里。"));
                    break;
                }
                InstallModpackButton.IsEnabled = true;
                lines.Add(Localizer.Tr("要解压 {n} 个文件", ("n", summary.OverlayCount)));
                lines.Add(Localizer.Tr("装进版本：{name}", ("name", version.Id)));
                lines.Add(Localizer.Tr("会解压到：{path}", ("path", root)));
                break;
            }

            case PackKind.CurseForge:
            {
                lines.Add(Localizer.Tr("CurseForge 整合包"));
                lines.Add($"{summary.Name}    ·    Minecraft {summary.Minecraft}"
                          + (summary.Loader.Length > 0
                              ? $"    ·    {summary.Loader} {summary.LoaderVersion}"
                              : string.Empty));
                lines.Add(Localizer.Tr("包里有 {n} 个 mod、{m} 个覆盖层文件",
                    ("n", summary.FileCount), ("m", summary.OverlayCount)));
                lines.Add(Localizer.Tr("这个包暂时装不了：里面每个 mod 的下载地址要用 CurseForge 的接口换，这一步还没接。"));
                break;
            }

            default:
                lines.Add(Localizer.Tr("认不出来这是什么包。"));
                break;
        }

        // ⚠️ 安全告警要**显眼**：里面有 `../` 之类的路径时引擎已经跳过了，
        //    但必须让用户看见 —— 这是安全事件，不该静默
        foreach (var note in summary.Notes ?? Array.Empty<string>())
        {
            lines.Add("⚠ " + note);
        }

        ModpackPreview.Text = string.Join("\n", lines);
    }

    /// <summary>
    /// 覆盖包要解压进的那个版本的游戏目录（`mods/` 的**上一级**）。
    ///
    /// ⚠️ 不能直接用 `<游戏目录>`：开了版本隔离时 mods 在
    ///    `versions/&lt;id&gt;/mods`，解压到根目录等于装到别的版本里去
    ///    （而且那个版本根本看不到这些文件）。所以走
    ///    `IModService.ContentDirFor` —— 跟"模组装到哪"是**同一份判断**。
    /// </summary>
    private string TargetGameDir(out GameVersion? version)
    {
        version = null;
        var versions = App.Services.Games.Installed;
        if (versions.Count == 0)
        {
            return string.Empty;
        }

        var wanted = App.Settings.LastVersion;
        version = versions.FirstOrDefault(v => v.Id == wanted) ?? versions[0];

        var contentDir = App.Services.Mods.ContentDirFor(
            App.Services.Games.MinecraftDir,
            "mod",
            version.MinecraftVersion,
            version.LoaderKey ?? string.Empty,
            version) ?? string.Empty;

        return contentDir.Length == 0
            ? App.Services.Games.MinecraftDir
            : (Path.GetDirectoryName(contentDir) ?? App.Services.Games.MinecraftDir);
    }

    private async void OnInstallModpackClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _packSummary is null || _packBlob is null)
        {
            return;
        }

        _busy = true;
        InstallModpackButton.IsEnabled = false;
        try
        {
            if (_packSummary.Kind == PackKind.Overlay)
            {
                await InstallOverlayPackAsync();
            }
            else
            {
                await InstallModrinthPackAsync();
            }
        }
        finally
        {
            _busy = false;
            InstallModpackButton.IsEnabled = true;
        }
    }

    /// <summary>Modrinth 包：交给工程已有的整合包服务（下文件 + 展开覆盖层 + 装加载器）。</summary>
    private async Task InstallModrinthPackAsync()
    {
        // 下载进度窗口：本地拖进来的包也有下载阶段（它要下里面那些 mod）
        var window = App.OpenDownloadWindow(
            _packSummary?.Name is { Length: > 0 } name ? name : Path.GetFileNameWithoutExtension(_modpackPath));

        try
        {
            var progress = new Progress<LaunchProgress>(p =>
            {
                ModpackPreview.Text = p.Message;
                window.SetStageNote(p.Message);
            });
            var result = await App.Services.Modpacks.InstallAsync(new ModpackInstallRequest(
                MrpackPath: _modpackPath,
                MinecraftDir: App.Services.Games.MinecraftDir,
                // 用整合包自己的名字（服务里会消毒成合法目录名）
                InstanceName: null,
                InstallLoader: true), progress);

            ModpackPreview.Text = result.Message;
            window.Finish(result.Message);
            if (result.Ok)
            {
                App.Settings.LastVersion = result.VersionId ?? App.Settings.LastVersion;
                await RefreshAfterInstallAsync();
            }
        }
        catch (OperationCanceledException)
        {
            ModpackPreview.Text = Localizer.Tr("已取消");
            window.Finish(Localizer.Tr("已取消"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidOperationException)
        {
            ModpackPreview.Text = Localizer.Tr("装整合包失败：{err}", ("err", ex.Message));
            window.Finish(Localizer.Tr("装整合包失败：{err}", ("err", ex.Message)));
        }
    }

    /// <summary>
    /// 覆盖包：把它展开到选定版本的游戏目录里（**不建新版本、不装加载器**）。
    ///
    /// ⚠️ 解压在**后台线程**做：几百 MB 的包在 UI 线程上展开，窗口会白屏几十秒
    ///    （`ApplyOverrides` 是同步的，进度回调里再切回 UI 线程刷文字）。
    /// </summary>
    private async Task InstallOverlayPackAsync()
    {
        var root = TargetGameDir(out var version);
        if (root.Length == 0 || version is null)
        {
            ModpackPreview.Text = Localizer.Tr("还没有装任何版本 —— 覆盖包要解压进一个已有的版本里。");
            return;
        }

        var blob = _packBlob!;
        var plan = PackArchive.OverlayPlan(blob, Path.GetFileNameWithoutExtension(_modpackPath));
        var written = 0;
        var errors = new List<string>();

        try
        {
            var result = await Task.Run(() => ModpackInstaller.ApplyOverrides(
                blob, plan, root,
                (done, total) => Dispatcher.BeginInvoke(() =>
                {
                    ModpackPreview.Text = Localizer.Tr("正在解压…（{a}/{b}）", ("a", done), ("b", total));
                })));
            written = result.Written;
            errors = result.Errors;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidOperationException or ModpackException)
        {
            ModpackPreview.Text = Localizer.Tr("解压覆盖包失败：{err}", ("err", ex.Message));
            return;
        }

        var lines = new List<string>
        {
            Localizer.Tr("已解压到 {name}：写入 {n} 个文件", ("name", version.Id), ("n", written)),
        };
        if (errors.Count > 0)
        {
            lines.Add(Localizer.Tr("有 {n} 个文件没写进去：", ("n", errors.Count)));
            lines.AddRange(errors.Take(5));
        }
        ModpackPreview.Text = string.Join("\n", lines);
    }

    // ============================================================
    //  对外（主窗口 / 自动化截图）
    // ============================================================

    /// <summary>
    /// 切到某一段。**公开**是为了自动化截图能直接截任意一段
    /// （`--page download:loader`）—— 离屏截图只会停在默认那一段，
    /// 别的段就永远没人验过。
    ///
    /// 认的 key：
    /// · 老的三个段名（`vanilla` / `loader` / `modpack`）照旧能用
    /// · 任何左栏分类的 key（`mods` / `resourcepacks` / `shaders` / `datapacks` …）
    /// · `&lt;分类&gt;:detail` —— 进那一栏并**直接打开第一个结果的详情页**
    ///   （详情页是"点一张卡"点出来的，离屏截图点不了鼠标）。
    ///   配合 `--query &lt;关键词&gt;` 用，见 <see cref="SetInitialQuery"/>。
    /// · `loader:forge` —— 进安装选项页并**直接选中 Forge 的第一个版本**
    /// · `kbd` —— 只打诊断：左栏那些行的焦点框 / Tab 可达性现在到底是什么值
    ///   （见 <see cref="ProbeNavKeyboard"/>）
    /// </summary>
    public void ShowSection(string key)
    {
        // `loader:forge` / `resourcepacks:detail` 这种带子段的写法
        var sub = string.Empty;
        var colon = key.IndexOf(':');
        if (colon > 0)
        {
            sub = key[(colon + 1)..];
            key = key[..colon];
        }

        switch (key)
        {
            case "vanilla":
                SelectCategory("minecraft");
                return;
            case "loader":
                // 安装选项页要先有一个版本，没有就替用户挑最新的正式版
                _ = OpenInstallSectionAsync(sub);
                return;
            case "modpack":
            case "modpacks":
                SelectCategory("modpacks");
                ForwardSubSection("modpacks", sub);
                return;
            case "kbd":
                // `--page download:kbd`：只打诊断，不切页面
                ProbeNavKeyboard();
                return;
        }

        foreach (var (_, items) in Nav)
        {
            foreach (var (itemKey, _) in items)
            {
                if (itemKey == key)
                {
                    SelectCategory(key);
                    ForwardSubSection(key, sub);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// 把 `&lt;分类&gt;:detail` 里的子段转给那一栏的共用页。
    ///
    /// ⚠️ 必须**在 SelectCategory 之后**调：页面是切栏那一刻才建的，
    ///    提前转就是转给一个还不存在的对象。
    /// </summary>
    private void ForwardSubSection(string navKey, string sub)
    {
        if (sub.Length == 0)
        {
            return;
        }
        if (ModContentType.ForNavKey(navKey) is { } type
            && _contentPages.TryGetValue(type.ProjectType, out var page))
        {
            page.ShowSection(sub);
        }
    }

    /// <summary>`--page download:loader`：挑一个版本进安装选项页。</summary>
    private async Task OpenInstallSectionAsync(string loaderKey = "")
    {
        SelectCategory("minecraft");
        try
        {
            await EnsureManifestAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or InvalidOperationException or IOException)
        {
            return;
        }

        // 已经选过版本就用它；否则拿最新的正式版（列表第一个就是）
        // ⚠️ `--mc` 给了就**认那一版**（见 SetInitialMcVersion）：加载器的版本列表
        //    跟着游戏版本走，拿最新正式版去看 LiteLoader / Cleanroom 永远是空的。
        var target = _selectedVersion
                     ?? (_initialMcVersion is { Length: > 0 } wanted ? FindVersionItem(wanted) : null)
                     ?? ReleaseGroup.Rows.FirstOrDefault()?.Item;
        if (target is null)
        {
            return;
        }
        SelectVersion(target);
        ShowPane(InstallPane);

        if (loaderKey.Length > 0)
        {
            await PickLoaderVersionAsync(loaderKey);
        }
    }

    /// <summary>在某三个版本分组里按 id 找一行（`--mc` 用；找不到返回 null）。</summary>
    private VersionRowItem? FindVersionItem(string id)
    {
        // ⚠️ 走 `Find`（查全部条目）不是查 `Rows`（只查已经建出来的行）：
        //    `1.8` 那种在"远古版本"里、默认收起，`Rows` 是空的。
        foreach (var group in new[] { ReleaseGroup, SnapshotGroup, OldGroup })
        {
            if (group.Find(id) is { } item)
            {
                return item;
            }
        }
        return null;
    }

    /// <summary>展开某一家并选中它的第一个版本（下面是人手点两下的那两步）。</summary>
    private async Task PickLoaderVersionAsync(string loaderKey)
    {
        // 等这一次的加载器清单落地（五家要发五个请求，慢的时候十几秒）。
        // ⚠️ 别用"轮询 _loaderRows 有没有值"：那只能猜它什么时候好，猜早了就白等。
        if (_loaderTask is not null)
        {
            await _loaderTask;
        }

        if (!_loaderGroups.TryGetValue(loaderKey, out var group))
        {
            return;
        }
        group.SetExpanded(true);
        if (group.Rows.FirstOrDefault()?.Item is { } first)
        {
            OnLoaderVersionActivated(loaderKey, first);
        }
    }

    /// <summary>
    /// 主窗口切到本页时调。
    ///
    /// 原来这里是"把游戏版本下拉和加载器列表备好"；重做之后**没有那个下拉了**
    /// （版本从版本列表里点），所以这里只负责把清单先拉起来 ——
    /// 用户切过来的时候列表已经在路上了。
    /// </summary>
    public void PrepareLoaderSection() => _ = EnsureManifestAsync();

    // ============================================================
    //  杂项
    // ============================================================

    /// <summary>当前屏幕缩放（125% 的系统返回 1.25）。控件还没进视觉树时退回 1.0。</summary>
    private double DpiScale
    {
        get
        {
            try
            {
                var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
                return scale > 0 ? scale : 1.0;
            }
            catch (InvalidOperationException)
            {
                return 1.0;
            }
        }
    }

    /// <summary>
    /// 控件进视觉树之后再校一次 DPI。
    ///
    /// 构造期读到的缩放不可靠（还没连到 PresentationSource），那时候建的图标是按
    /// 1.0 画的 —— 125% 的屏上会糊。这里发现缩放不一样就按新的重建一遍。
    /// </summary>
    private void OnLoadedOnce(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOnce;
        var scale = DpiScale;
        if (Math.Abs(scale - _dpiScale) < 0.001)
        {
            return;
        }
        _dpiScale = scale;

        // 已经建好的行是拿旧缩放画的图，直接清掉重来（数据还在，重建很便宜）
        foreach (var name in VersionGroupOrder)
        {
            var group = _versionGroups[name];
            group.SetItems(ItemsOf(group), scale);
        }
        foreach (var (key, _) in LoaderOrder)
        {
            var group = _loaderGroups[key];
            group.SetItems(ItemsOf(group), scale);
        }
        if (_selectedVersion is not null)
        {
            // 图标是按物理像素画的，缩放变了得重画（见 VersionIcons 的类注释）
            RefreshNameIcon();
            // 重建会把"选中"标记一起清掉，补回来
            foreach (var group in _versionGroups.Values)
            {
                group.SetPicked(_selectedVersion.Id);
            }
        }
    }

    private static IReadOnlyList<VersionRowItem> ItemsOf(CollapsibleGroup group)
        => group.Rows.Select(r => r.Item).OfType<VersionRowItem>().ToList();

    /// <summary>一个版本条目：装哪个、用哪个游戏版本去查加载器（本地版本这两个不一样）。</summary>
    private sealed record VersionTarget(string Id, string MinecraftVersion, string ReleaseTime = "");
}
