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

    /// <summary>左侧分类。`Group` 为 null = 这一项自己就是一级、没有分组标题。</summary>
    private static readonly (string? Group, (string Key, string Text)[] Items)[] Nav =
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
        ("安装包", new[]
        {
            ("install_minecraft", "Minecraft"),
            ("optifine", "OptiFine"),
            ("forge", "Forge"),
            ("neoforge", "NeoForge"),
            ("cleanroom", "Cleanroom"),
            ("fabric", "Fabric"),
            ("legacy_fabric", "Legacy Fabric"),
            ("quilt", "Quilt"),
            ("labymod", "LabyMod"),
            ("liteloader", "LiteLoader"),
        }),
    };

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

    /// <summary>安装选项页的五家（顺序固定：Forge 在最上面，跟 1.0.0 的截图一致）。</summary>
    private static readonly (string Key, string Title)[] LoaderOrder =
    {
        ("forge", "Forge"),
        ("neoforge", "NeoForge"),
        ("fabric", "Fabric"),
        ("quilt", "Quilt"),
        ("optifine", "OptiFine"),
    };

    /// <summary>占位页的说明文案（逐条照 1.0.0 的 DETAILS 抄）。</summary>
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
        ["optifine"] = "OptiFine 各版本",
        ["forge"] = "Forge 各版本（含推荐版和最新版）",
        ["neoforge"] = "NeoForge 各版本",
        ["cleanroom"] = "Cleanroom 各版本（还没研究过怎么下）",
        ["fabric"] = "Fabric 加载器各版本",
        ["legacy_fabric"] = "Legacy Fabric（还没研究过）",
        ["quilt"] = "Quilt 加载器各版本（还没研究过）",
        ["labymod"] = "LabyMod（还没研究出来怎么下）",
        ["liteloader"] = "LiteLoader（太老，还没确定怎么下）",
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

    public DownloadPage()
    {
        InitializeComponent();

        _versionGroups["正式版"] = ReleaseGroup;
        _versionGroups["快照"] = SnapshotGroup;
        _versionGroups["远古版本"] = OldGroup;
        foreach (var (key, title) in LoaderOrder)
        {
            var group = key switch
            {
                "forge" => ForgeGroup,
                "neoforge" => NeoForgeGroup,
                "fabric" => FabricGroup,
                "quilt" => QuiltGroup,
                _ => OptifineGroup,
            };
            group.SetTitle(title);
            _loaderGroups[key] = group;
        }

        BuildNav();

        // 分组里点一行 → 进安装选项页
        foreach (var group in _versionGroups.Values)
        {
            group.ItemActivated += OnVersionActivated;
        }
        foreach (var (key, _) in LoaderOrder)
        {
            var loaderKey = key;
            _loaderGroups[key].ItemActivated += (_, item) => OnLoaderVersionActivated(loaderKey, item);
        }

        // 进页面先落在「版本 → Minecraft」上，并把清单拉起来
        SelectCategory("minecraft");

        // 真机是 125% 缩放的屏幕时，构造期读到的 DPI 还不可靠（控件没进视觉树）。
        // 进树之后补一次：缩放变了就按新缩放重建图标，不然那些方块是糊的。
        Loaded += OnLoadedOnce;
    }

    /// <summary>入场动效：返回 null = 这一页不做整页入场（行太多，逐行滑入会拖沓）。</summary>
    public Panel? AnimationHost => null;

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
                ShowPane(VersionsPane);
                _ = EnsureManifestAsync();
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
                     VersionsPane, InstallPane, PlaceholderPane, ModpacksHost, ModsHost, DatapackPane,
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

        foreach (var group in _versionGroups.Values)
        {
            group.SetPicked(item.Id);
        }

        ChipName.Text = item.DisplayName;
        var chipIcon = VersionIcons.Load(item.IconName, VersionIcons.ChipSize, DpiScale);
        ChipIcon.Source = chipIcon;
        ChipIcon.Visibility = chipIcon is null ? Visibility.Collapsed : Visibility.Visible;

        InstallTargetText.Text = Localizer.Tr("将安装到: {path}",
            ("path", App.Services.Games.MinecraftDir.Length > 0
                ? App.Services.Games.MinecraftDir
                : Localizer.Tr("（没设置游戏目录）")));

        ResetLoaderGroups();
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
            group.SetNote(LoaderNote(key, title, row));
            group.SetItems(BuildLoaderItems(key, mcVersion, row), DpiScale);
            group.SetExpanded(false);
            group.SetPicked(null);
        }
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
            return Localizer.Tr(LoaderFailNote);
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

    private void OnLoaderVersionActivated(string key, VersionRowItem item)
    {
        _selectedLoaderKey = key;
        _selectedLoaderVersion = item.Id;

        // 五家之间互斥：标了这一行，其余全取消（不然会出现"选了两家"的假象）
        foreach (var (groupKey, group) in _loaderGroups)
        {
            group.SetPicked(groupKey == key ? item.Id : null);
        }

        RefreshFooter();
    }

    private void RefreshFooter()
    {
        if (_selectedLoaderKey.Length == 0)
        {
            InstallHint.Text = Localizer.Tr("还没有选择加载器 —— 直接点「开始下载」只会装原版");
            return;
        }

        var title = LoaderTitle(_selectedLoaderKey);
        InstallHint.Text = App.Services.Loaders.CanInstall(_selectedLoaderKey)
            ? Localizer.Tr("将安装 {name} {ver}", ("name", title), ("ver", _selectedLoaderVersion))
            : Localizer.Tr("{name} 的安装还没接 —— 现在能装的是原版 / Forge / Fabric", ("name", title));
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
                "{name} 的安装还没接 —— 换 Forge / Fabric，或者回上一页直接装原版", ("name", title)));
            return;
        }

        var target = _selectedVersion.Payload as VersionTarget;
        var mcVersion = target?.MinecraftVersion ?? _selectedVersion.Id;
        var mcDir = App.Services.Games.MinecraftDir;

        _busy = true;
        InstallButton.IsEnabled = false;

        // 下载进度窗口（= 1.0.0 那个"正在安装 …"）。装原版和装加载器共用它：
        // 两条路都是"下一批文件 → 再跑安装器"，用户看到的进度应该是同一套。
        var window = App.OpenDownloadWindow(
            _selectedLoaderKey.Length == 0
                ? mcVersion
                : $"{LoaderTitle(_selectedLoaderKey)} {mcVersion}");

        try
        {
            var progress = new Progress<LaunchProgress>(p =>
            {
                SetInstallStatus(p.Message);
                // 窗口里那句副标题：没有真的在下载时（跑安装器、校验中）就是它
                window.SetStageNote(p.Message);
            });

            if (_selectedLoaderKey.Length == 0)
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
                var result = await App.Services.Loaders.InstallAsync(new LoaderInstallRequest(
                    MinecraftVersion: mcVersion,
                    LoaderKey: _selectedLoaderKey,
                    LoaderVersion: _selectedLoaderVersion,
                    MinecraftDir: mcDir), progress);

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
        var target = _selectedVersion ?? (ReleaseGroup.Rows.FirstOrDefault()?.Item);
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
            var icon = VersionIcons.Load(_selectedVersion.IconName, VersionIcons.ChipSize, scale);
            ChipIcon.Source = icon;
            ChipIcon.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
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
