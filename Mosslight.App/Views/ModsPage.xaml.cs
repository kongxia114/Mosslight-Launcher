using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mosslight.App.Infrastructure;
using Mosslight.Core.Localization;
using Mosslight.Core.Services;
using Mosslight.Core.Services.Engine;

namespace Mosslight.App.Views;

/// <summary>
/// 「社区资源」那几栏共用的页面（模组 / 整合包 / 数据包 / 资源包 / 光影包）。
/// 移植自 1.0.0 的 `ui/pages/mods_page.py`（收窄版，见 XAML 注释）。
///
/// ## 类型参数（2026-10）
/// 这一页认一个 <see cref="ModContentType"/>：搜索时带对应的 `project_type` facet，
/// 标题 / 副标题 / 搜索框提示 / 空结果文案 / 图标兜底 / 下载落点全跟着它走。
/// 差别只有五处，全在 `ModContentTypes.cs` 那张表里 —— **这里没有**
/// "如果是资源包就…"这种分支。参数不传就是模组（老调用点一行不用改）。
///
/// ## 装到哪个目录
/// 一律问引擎（`IModService.ContentDirFor`）—— 它跟**启动时**用的是同一套判断
/// （版本隔离的版本装进版本目录，否则装共用的目录；资源包/光影只是最后那段
/// 目录名不同）。自己在这里拼路径迟早会跟引擎判得不一样，那种 bug 表现为
/// "东西装上了但游戏里没有"。
///
/// ## 整合包为什么是另一条路
/// 整合包不落进 `mods/`：它自带 mod + 配置 + 加载器，装成一个**独立实例**
/// （`versions/&lt;实例名&gt;/`）。所以它走工程已有的 <see cref="IModpackService"/>
/// （`.mrpack` 那条流水线），这里只负责把 Modrinth 上那个 `.mrpack` 的地址交给它。
///
/// ## 排序（页顶那排标签 2026-10 删掉了）
/// 只剩一条规则：**有关键词按相关度、关键词为空按下载量**（<see cref="SortFor"/>）。
///
/// ## 网络不通要说清楚
/// 1.0.0 专门有一句「网络环境不佳」的告警条。这里照做 ——
/// "搜不到结果"和"网络连不上"是两回事，混在一起用户只会以为这个项目不存在。
///
/// ## 卡片上的图标
/// 结果卡的布局照 `parts/80-mods.qss` 的 #ModCard 一族重建：图标 + 名称 +
/// 作者 + 两行简介 + 底部元信息。图标从 `hit["icon_url"]` **异步下载 + 内存缓存**
/// （<see cref="ModIcons"/>），没下来之前和失败之后都显示内置的兜底方块图
/// （**按类型挑**：模组→forge、整合包→pack、资源包→GrassPath、光影→RedstoneLampOn）。
/// </summary>
public partial class ModsPage : UserControl, IMosslightPage
{
    /// <summary>
    /// Modrinth 的 `project_type` → 界面文案（1.0.0 的 `MOD_TYPES`）。
    /// 认不出来就原样显示 —— 别把没见过的类型显示成空白。
    ///
    /// ⚠️ 跟 Core 的 `ModrinthApi.TypeLabel` 是两份：那份是**下载任务标签**用的
    ///    （认不出来当 mod），这份是**卡片徽章**用的（认不出来显示接口原文）。
    ///    两边的兜底不一样是故意的，别顺手合并。
    /// </summary>
    private static readonly Dictionary<string, string> TypeLabels = new(StringComparer.Ordinal)
    {
        ["mod"] = "模组",
        ["modpack"] = "整合包",
        ["datapack"] = "数据包",
        ["resourcepack"] = "资源包",
        ["shader"] = "光影包",
    };

    /// <summary>
    /// 加载器徽章的显示名（Modrinth 的 categories 值 → 产品名）。
    /// 1.0.0 的 `LOADER_LABELS` 抄过来的；顺序也有意义：**只挑这四家**，
    /// 其余分类（optimization / technology …）不上卡片 —— 那是"分类"不是"加载器"。
    /// </summary>
    private static readonly (string Key, string Label)[] LoaderLabels =
    {
        ("fabric", "Fabric"),
        ("forge", "Forge"),
        ("neoforge", "NeoForge"),
        ("quilt", "Quilt"),
    };

    /// <summary>这一页是「社区资源」里的哪一栏（决定 facets / 文案 / 落点）。</summary>
    private readonly ModContentType _type;

    private bool _searching;

    /// <summary>
    /// 正在往搜索卡那四个下拉里铺候选。铺的过程会触发 `SelectionChanged`。
    ///
    /// ⚠️ 不拦住它的话，"页面一构造就铺表"会被当成"用户改了筛选"：轻则一进来
    ///    就自己搜一把，重则把刚预选好的那一项（当前版本 + 当前加载器）覆盖掉 ——
    ///    那种 bug 看着像"默认值没生效"，查起来要人命。
    /// </summary>
    private bool _fillingFilters;

    /// <summary>
    /// 上一轮搜索还没收尾时用户又改了筛选 → 收尾之后补搜一次。
    ///
    /// 不记这一笔的话，连着改两下只有第一下生效（第二下被 <see cref="_searching"/>
    /// 挡掉），而屏幕上那批结果已经是**上一个条件**的了 —— 用户看到的和条件对不上。
    /// </summary>
    private bool _pendingSearch;

    /// <summary>
    /// 下载会落到哪（详情页底部那行提示用）。整合包是"实例名的占位"，
    /// 见 <see cref="RefreshDetailHint"/>。
    /// </summary>
    private string _contentDir = string.Empty;

    private GameVersion? _targetVersion;

    /// <summary>
    /// 这一次搜索的任务。留着是为了 `--page mods:detail`：
    /// 那条路要"搜完再进详情页"，得有个东西能 await（见 <see cref="ShowSection"/>）。
    /// </summary>
    private Task? _searchTask;

    /// <summary>
    /// 问接口要候选（游戏版本 / 分类）的那个任务。
    /// 留着同样是为了 `--page mods:options`：那一段必须**等接口回来**才有得看。
    /// </summary>
    private Task? _optionsTask;

    /// <summary>当前详情页展示的那条搜索结果（null = 不在详情页）。</summary>
    private JsonObject? _detailHit;

    /// <summary>详情页图标坑位的边长（= QSS 里 #ModDetailIcon 的 96×96）。</summary>
    private const int DetailIconSize = 96;

    /// <summary>模组页（老调用点：主窗口的「模组」页、`--page mods`）。</summary>
    public ModsPage() : this(ModContentType.Mod)
    {
    }

    public ModsPage(ModContentType type)
    {
        _type = type;
        InitializeComponent();

        // 标题 / 副标题 / 空结果文案 / 搜索框提示都跟类型走。
        // ⚠️ 走 Localizer 而不是写在 XAML 里：这些 key 在 5 个语言文件里都查得到
        //    （写死中文就等于把翻译丢了）。
        PageTitleText.Text = Localizer.Tr(_type.Title);
        PageSubtitleText.Text = Localizer.Tr(_type.Subtitle);
        EmptyText.Text = Localizer.Tr("没有找到{kind}，换个关键词或放宽筛选试试", ("kind", _type.Label));

        // 搜索框那句提示语（WPF 没有 placeholder，是自己叠的一层 TextBlock）。
        // 它跟副标题是**两句话**：副标题说"这一栏是什么"，提示语说"这里能怎么用"。
        QueryHint.Text = Localizer.Tr("输入关键词搜索{kind}，留空看最热门的", ("kind", _type.Label));
        QueryBox.ToolTip = QueryHint.Text;
        QueryBox.TextChanged += (_, _) => UpdateQueryHint();
        QueryBox.GotKeyboardFocus += (_, _) => UpdateQueryHint();
        QueryBox.LostKeyboardFocus += (_, _) => UpdateQueryHint();
        UpdateQueryHint();

        ResolveTargetVersion();

        // ⚠️ 必须在 ResolveTargetVersion 之后：那一步要先把"装到哪个版本上"定下来，
        //    四个下拉的默认值（游戏版本 = 那个版本的游戏版本、加载器 = 它装的那家）
        //    才知道该预选什么。
        InitFilters();        // 候选表要问接口（分类会改名、版本会新增）。**不 await**：下拉先用兜底表
        // 铺好，接口回来再就地换内容 —— 不让网络决定"页面什么时候能点"。
        _optionsTask = RefreshFilterOptionsAsync();

        // 进这一页**先搜一屏**（空关键词 = 按下载量排的热门）。
        //
        // ⚠️ 不搜的话页面上只有"没有找到模组，换个关键词或放宽筛选试试"那句空状态 ——
        //    用户点进"模组"看到的是一句像出错的话，还得自己想到去按搜索。
        //    1.0.0 也是这个路子（`set_project_type` 切一栏就重搜：切了什么都不发生
        //    会让人以为坏了）。
        // ⚠️ 挂在 **Dispatcher**（不是 `Loaded` 事件）上：离屏截图模式**从不 Show
        //    窗口**，`Loaded` 永远不会触发 —— 挂在它上面的话，"进页面先搜一屏"这件事
        //    在截图里根本不会发生（界面空着，而且这条路径永远没人验过）。
        //    Dispatcher 的 `Loaded` 优先级排在 Render 之后，正好是布局好了再动手；
        //    它也给 `--query` 那条路留了先手（那一页在 ShowPage 里建完就立刻 RunQuery）。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(OnFirstLoaded));

        BackButton.Content = Localizer.Tr("← 返回");

        // 详情页图标那圈圆角。⚠️ **在构造函数里挂一次**，不要放进 FillDetailCard ——
        // 每进一次详情就往同一个控件上再挂一个 SizeChanged，反复进出会越挂越多
        // （WPF 的 Border 不会裁子元素，Clip 是手工给的圆角矩形）。
        _detailIconClip.RadiusX = 10;
        _detailIconClip.RadiusY = 10;
        DetailIconWell.Clip = _detailIconClip;
        DetailIconWell.SizeChanged += (_, _) =>
            _detailIconClip.Rect = new Rect(0, 0, DetailIconWell.ActualWidth, DetailIconWell.ActualHeight);
    }

    /// <summary>详情页图标的圆角裁剪（尺寸变了要按新尺寸重算，见构造函数）。</summary>
    private readonly RectangleGeometry _detailIconClip = new();

    /// <summary>
    /// 进了详情页 / 从详情页返回（true = 进详情页）。
    ///
    /// 给"这一页上面还压着别的东西"的宿主用：下载页的「整合包」那一栏上面就是
    /// 那张本地 `.mrpack` 卡 —— 进详情页时得把它收起来，不然详情页被挤掉一截
    /// （版本列表只剩三四行）。
    /// </summary>
    public event EventHandler<bool>? DetailOpenedChanged;

    public Panel? AnimationHost => null;

    /// <summary>
    /// 搜索框那句提示语的显示/隐藏。
    ///
    /// 只在"框里没字 **而且** 焦点不在框里"时显示 —— 有焦点时还留着，
    /// 那句话会跟光标和输入内容打架（用户一打字它就该让位）。
    /// </summary>
    private void UpdateQueryHint()
        => QueryHint.Visibility = QueryBox.Text.Length == 0 && !QueryBox.IsKeyboardFocusWithin
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>
    /// `--query &lt;关键词&gt;`：把关键词填进搜索框并**直接搜一把**。
    ///
    /// 存在的理由跟 <see cref="DownloadPage.ShowSection"/> 一样 ——
    /// 搜索结果是点出来的，离屏截图点不了鼠标。没有这个入口，
    /// 「结果卡上的图标到底出没出来」就只能靠人肉开程序看。
    /// 对真人用户没有影响：不传 `--query` 时这就是个没人调的方法。
    /// </summary>
    public void RunQuery(string query)
    {
        QueryBox.Text = query;
        _searchTask = SearchAsync();
    }

    /// <summary>
    /// `--page mods:detail`：先等搜索落地，再**直接进第一个结果的详情页**。
    ///
    /// 跟 <see cref="RunQuery"/> 一个理由：详情页是"点一张卡"点出来的，
    /// 离屏截图点不了鼠标 —— 没有这个入口，详情页（返回按钮 / 详情卡 /
    /// 版本折叠组 / 每行的下载按钮 / 底部落点提示）就永远没人验过。
    ///
    /// ⚠️ 必须**等搜索真的结束**再点：搜索结果是一张张加进列表的，
    ///    提前点会点到空列表上（`_searchTask` 就是为这件事留的）。
    /// </summary>
    public void ShowSection(string key)
    {
        if (string.Equals(key, "detail", StringComparison.Ordinal))
        {
            // ⚠️ 排到 `Background` 优先级（比构造里那次 `Loaded` 自动搜索更晚）：
            //    不排的话它会在"搜索还没开跑"的时候就去等 `_searchTask`，
            //    看到 null 就直接返回 —— `--page mods:detail` 会**什么都没做**，
            //    而 CI 里那一项是绿的（截图里就是一张列表页）。
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background, new Action(() => _ = OpenFirstResultDetailAsync()));
            return;
        }

        // `mods:kbd`：只打诊断（焦点框 / Tab 可达性），不切页面、不改数据
        if (string.Equals(key, "kbd", StringComparison.Ordinal) && _searchTask is not null)
        {
            _ = ProbeAfterSearchAsync();
            return;
        }

        // `mods:options`：只打诊断（四个下拉里到底铺了什么），不切页面、不改数据
        if (string.Equals(key, "options", StringComparison.Ordinal))
        {
            _ = ProbeFilterOptionsAsync();
            return;
        }

        // `mods:filters`：程序化走一遍"改筛选 → 重置"，验证事件链路
        if (string.Equals(key, "filters", StringComparison.Ordinal))
        {
            ProbeFilterRoundTrip();
            return;
        }

        // `mods:scroll`：程序化把两处列表滚到底，验证"续加载"真的会触发
        if (string.Equals(key, "scroll", StringComparison.Ordinal))
        {
            _ = ProbeScrollLoadingAsync();
            return;
        }

        // `mods:target`：把「装到哪儿？」那个对话框建出来（离屏截图用）
        if (string.Equals(key, "target", StringComparison.Ordinal))
        {
            ActiveDialog = BuildTargetDialog() ?? (Window?)null;
        }
    }

    /// <summary>离屏截图要渲染的那个对话框（`--page mods:target` 建的；没建过是 null）。</summary>
    public Window? ActiveDialog { get; private set; }

    /// <summary>
    /// 「装到哪儿？」：给出"每个已装版本 → 它该落到的目录"那些选项。
    ///
    /// ⚠️ 目录是 `IModService.ContentDirFor` 算的 —— 跟**启动时**用的是同一套判断
    ///    （版本隔离的版本会落到 `versions/&lt;id&gt;/mods`），所以选完装进去，
    ///    游戏里一定看得见。
    /// </summary>
    private List<(string VersionId, string Dir)> BuildTargetOptions()
    {
        var options = new List<(string, string)>();
        foreach (var version in App.Services.Games.Installed)
        {
            var dir = App.Services.Mods.ContentDirFor(
                App.Services.Games.MinecraftDir,
                _type.ProjectType,
                version.MinecraftVersion,
                version.LoaderKey ?? string.Empty,
                version);
            if (!string.IsNullOrEmpty(dir))
            {
                options.Add((version.Id, dir));
            }
        }
        return options;
    }

    /// <summary>
    /// 建一个「装到哪儿？」对话框（不 Show）。
    /// 界面上点下载走 <see cref="AskTargetDir"/>；这里给离屏截图用。
    /// </summary>
    private InstallTargetDialog? BuildTargetDialog()
    {
        var options = BuildTargetOptions();
        if (options.Count == 0)
        {
            return null;
        }
        return new InstallTargetDialog(options, _contentDir);
    }

    /// <summary>
    /// 点下载时先问一句"装到哪儿"（= PCL 的做法）。返回 null 表示用户取消了。
    ///
    /// ⚠️ 截图模式下**不问**（返回默认目录）：`ShowDialog` 是模态的，
    ///    离屏那条路没人点按钮，会直接卡死在那儿。
    /// </summary>
    private string? AskTargetDir()
    {
        if (App.ScreenshotMode)
        {
            return _contentDir;
        }

        var dialog = BuildTargetDialog();
        if (dialog is null)
        {
            ReportError(Localizer.Tr("还没有本地版本 —— 先去「下载」页装一个，再回来装{kind}。",
                ("kind", _type.Label)));
            return null;
        }

        var owner = Window.GetWindow(this);
        if (owner is not null && owner.IsLoaded)
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true && dialog.ChosenDir.Length > 0
            ? dialog.ChosenDir
            : null;
    }

    /// <summary>
    /// `--page mods:scroll`：**真滚一次**两处列表，把"滚前 / 滚后"的条数打到标准输出。
    ///
    /// 跟其它探针一个理由：滚动是鼠标动作，离屏截图做不出来 —— 而"滑到底会不会继续加载"
    /// 正是这次新加的、最容易写成"看着有、其实不触发"的东西（阈值算错、事件没接、
    /// 游标没记住都会静默失败）。这里走的是真事件（`ScrollIntoView` / `ScrollToEnd`），
    /// 不是直接调内部方法。
    /// </summary>
    private async Task ProbeScrollLoadingAsync()
    {
        // ① 搜索结果：搜完一屏 → 滚到底 → 应该自动接上第二页
        //
        // ⚠️ 进来时 `_searchTask` 可能**还没建**（自动搜索排在 Dispatcher 上，比这里晚），
        //    直接判 null 就跳过的话，探针会拿"第一页还在路上"当"没有第二页"。
        for (var i = 0; i < 20; i++)
        {
            if (_searchTask is { IsCompleted: true })
            {
                break;
            }
            await Task.Delay(500);
        }

        Console.WriteLine($"[scroll] 结果页：滚前 {ResultList.Items.Count} 条 / 共 {_resultTotal}");
        if (ResultList.Items.Count > 0)
        {
            ResultList.ScrollIntoView(ResultList.Items[^1]);
        }
        await Task.Delay(4000);     // 等那一页真的回来
        Console.WriteLine($"[scroll] 结果页：滚后 {ResultList.Items.Count} 条（游标 {_resultOffset}）");

        // ② 详情页：进第一个结果的详情 → 滚到底 → 分组应该续铺
        await OpenFirstResultDetailAsync();
        await Task.Delay(4000);     // 等版本列表落地
        var before = _detailGroupsShown;

        // ⚠️ 离屏跑的时候窗口**从来没 Show 过**，布局不会自己跑 —— 不先量一次的话
        //    视口/内容都是 0，`ScrollToEnd()` 等于什么都没做（探针第一版就撞在这儿：
        //    "滚后 12 组（12 → 12）"，看着像功能没生效，其实是没布局）。
        UpdateLayout();
        Console.WriteLine($"[scroll] 详情页：滚前已铺 {before} 组 / 共 {_detailGroups.Count} 组"
                          + $"（视口 {DetailScroll.ViewportHeight:N0} / 内容 {DetailScroll.ExtentHeight:N0}）");

        DetailScroll.ScrollToEnd();
        await Task.Delay(1500);
        DetailScroll.ScrollToEnd();
        await Task.Delay(1500);
        Console.WriteLine($"[scroll] 详情页：滚后已铺 {_detailGroupsShown} 组"
                          + $"（{before} → {_detailGroupsShown}）");
    }

    /// <summary>
    /// `--page mods:filters`：**程序化**改一次筛选、再点一次「重置」，把每一步的
    /// 选中值打到标准输出。
    ///
    /// 跟 <see cref="ProbeFilterOptionsAsync"/> 一个理由：离屏截图点不了鼠标，
    /// 而 `SelectionChanged` / `Click` 这两条链路正是最容易"编译通过、一点就崩"
    /// 的地方（控件还没建好就触发事件、`_fillingFilters` 忘了压住导致一进来就自搜）。
    /// 这里走的是**真事件**（`RaiseEvent`），不是直接调方法。
    /// </summary>
    private void ProbeFilterRoundTrip()
    {
        Console.WriteLine("[filter-roundtrip] 起始：" + DescribeFilters());

        // 1. 改分类（会触发 SelectionChanged → RequestSearch）
        if (CategoryBox.Items.Count > 1)
        {
            CategoryBox.SelectedIndex = 1;
            Console.WriteLine("[filter-roundtrip] 改分类后：" + DescribeFilters()
                + $" 搜索中={_searching} 待补搜={_pendingSearch}");
        }

        // 2. 改排序（同上）
        if (SortBox.Items.Count > 1)
        {
            SortBox.SelectedIndex = 1;
            Console.WriteLine("[filter-roundtrip] 改排序后：" + DescribeFilters()
                + $" 搜索中={_searching} 待补搜={_pendingSearch}");
        }

        // 3. 点「重置」（真 Click 事件）
        ResetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Console.WriteLine("[filter-roundtrip] 重置后：" + DescribeFilters()
            + $" 搜索中={_searching} 待补搜={_pendingSearch}");
    }

    /// <summary>四个下拉当前选中的接口值 + 关键词（诊断用）。</summary>
    private string DescribeFilters()
        => $"关键词=「{QueryBox.Text}」 游戏版本={SelectedValue(GameVersionBox)}"
            + $" 加载器={(LoaderBox.Visibility == Visibility.Visible ? SelectedValue(LoaderBox) : "<收起>")}"
            + $" 分类={(CategoryBox.Visibility == Visibility.Visible ? SelectedValue(CategoryBox) : "<收起>")}"
            + $" 排序={SelectedValue(SortBox)}";

    /// <summary>
    /// `--page mods:options`：把搜索卡那四个下拉的**实际候选**打到标准输出。
    ///
    /// 跟 <see cref="ProbeListKeyboard"/> 一个理由：下拉是"点开才看得见"的东西，
    /// 离屏截图拍不到 —— 而"候选铺了几项、用的是接口回来的数据还是兜底表、
    /// 没有加载器 / 分类那一维的栏有没有把整格收起来"正是这次改动最需要证明的地方。
    /// 对真人用户没有影响：不传这个子段时这就是个没人调的方法。
    /// </summary>
    private async Task ProbeFilterOptionsAsync()
    {
        try
        {
            if (_optionsTask is not null)
            {
                await _optionsTask;
            }
        }
        catch (Exception ex) when (IsOptionFetchFailure(ex))
        {
            // 问接口那两条路自己已经吞了异常，这里只是兜底
        }

        Console.WriteLine("[filter-options]");
        DumpCombo("游戏版本", GameVersionBox);
        DumpCombo("加载器", LoaderBox);
        DumpCombo("分类", CategoryBox);
        DumpCombo("排序", SortBox);
        Console.WriteLine($"[filter-options] 卡片标题={SearchCardTitle.Text} 按钮={SearchButton.Content}");
    }

    /// <summary>一个下拉的实际内容（只打前几项，够看出"铺的是什么"就行）。</summary>
    private static void DumpCombo(string name, ComboBox box)
    {
        var items = box.Items.OfType<ComboBoxItem>()
            .Select(item => item.Tag as string ?? "?")
            .ToList();
        var hidden = box.Visibility != Visibility.Visible ? "（整格已收起）" : string.Empty;
        var preview = string.Join(" | ", items.Take(8)) + (items.Count > 8 ? " …" : string.Empty);
        Console.WriteLine(
            $"  {name}: {items.Count} 项{hidden} 选中={SelectedValue(box)} → {preview}");
    }

    /// <summary>`mods:kbd` 要等搜索落地才有行可量（理由同 <see cref="OpenFirstResultDetailAsync"/>）。</summary>
    private async Task ProbeAfterSearchAsync()
    {
        try
        {
            await _searchTask!;
        }
        catch (Exception ex) when (ex is ModrinthException or HttpRequestException
                                      or TaskCanceledException or InvalidOperationException
                                      or NotSupportedException)
        {
            return;
        }
        ProbeListKeyboard();
    }

    /// <summary>
    /// `--page mods:kbd`：把结果列表那些卡片行的"焦点相关实况"打到标准输出。
    ///
    /// 跟 <see cref="DownloadPage.ShowSection"/> 的 `kbd` 分支一个理由：
    /// 「点过的卡片会不会留一圈焦点框」这件事**离屏截图证明不了** ——
    /// 截图那条路没有键盘、窗口也没 Show，焦点框那层 Adorner 根本不在渲染树里。
    /// 所以改成问运行时的真实取值。
    /// </summary>
    private void ProbeListKeyboard()
    {
        // ⚠️ 先给列表单独跑一遍布局：这个诊断是在窗口 Show / 截图那次 Measure
        //    **之前**调的，不强制的话容器一个都没生成、量到的全是 0。
        try
        {
            ResultList.Measure(new Size(600, 2000));
            ResultList.Arrange(new Rect(0, 0, 600, Math.Max(ResultList.DesiredSize.Height, 400)));
            ResultList.UpdateLayout();
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"[mods-kbd] 强制布局失败（量不到行高）：{ex.Message}");
        }

        Console.WriteLine($"[mods-kbd] 结果列表 行数={ResultList.Items.Count} "
            + $"IsTabStop={ResultList.IsTabStop} Focusable={ResultList.Focusable} "
            + $"SelectionMode={ResultList.SelectionMode} "
            + $"TabNavigation={KeyboardNavigation.GetTabNavigation(ResultList)} "
            + $"DirectionalNavigation={KeyboardNavigation.GetDirectionalNavigation(ResultList)}");

        var shown = 0;
        foreach (var item in ResultList.Items)
        {
            if (item is not ListBoxItem row || shown >= 3)
            {
                continue;
            }
            var container = ResultList.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem ?? row;
            Console.WriteLine($"[mods-kbd] 卡片行[{shown}] "
                + $"IsTabStop={container.IsTabStop} Focusable={container.Focusable} "
                + $"FocusVisualStyle={(container.FocusVisualStyle is null ? "null（不画焦点框）" : "非 null（会画！）")}"
                + $" 行高={container.ActualHeight:N2}");
            shown++;
        }
    }

    private async Task OpenFirstResultDetailAsync()
    {
        if (_searchTask is not null)
        {
            try
            {
                await _searchTask;
            }
            catch (Exception ex) when (ex is ModrinthException or HttpRequestException
                                          or TaskCanceledException or InvalidOperationException
                                          or NotSupportedException)
            {
                // 搜索自己已经把错说给用户了（告警条），这里只是没得可点
                return;
            }
        }

        if (ResultList.Items.Count > 0
            && ResultList.Items[0] is ListBoxItem { Tag: JsonObject hit })
        {
            OpenDetail(hit);
        }
    }

    /// <summary>
    /// 装到哪个版本上去：优先配置里的 `last_version`，否则第一个。
    /// 一个都没有的话退回共用的目录（引擎那条路的第 3 条，**不是错误路径**）。
    ///
    /// ⚠️ `_contentDir` 的用处只有一个：详情页底部那行"…下载会落到：{path}"。
    ///    它现在是**按类型**算的（`IModService.ContentDirFor`）：
    ///    mod → `mods`、资源包 → `resourcepacks`、光影 → `shaderpacks`，
    ///    而且**版本隔离的判断跟启动时是同一份**（见 ModsDirectory.ContentDirFor）。
    /// </summary>
    private void ResolveTargetVersion()
    {
        var versions = App.Services.Games.Installed;
        var wanted = App.Settings.LastVersion;
        _targetVersion = versions.FirstOrDefault(v => v.Id == wanted) ?? versions.FirstOrDefault();

        if (_type.Target == ModInstallTarget.ModpackInstance)
        {
            // 整合包不落在"游戏目录下的某个子目录"里，而是装成一个独立实例：
            // `versions/<整合包名>/`。名字要等 `.mrpack` 下下来才知道，
            // 所以这里只放一个占位（尖括号是"这里是变量"的通用写法，
            // 免得用户以为真有个叫"整合包自己的名字"的目录）。
            _contentDir = Path.Combine(
                App.Services.Games.MinecraftDir, "versions",
                "<" + Localizer.Tr("整合包自己的名字") + ">");
            return;
        }

        _contentDir = App.Services.Mods.ContentDirFor(
            App.Services.Games.MinecraftDir,
            _type.ProjectType,
            _targetVersion?.MinecraftVersion ?? string.Empty,
            // ⚠️ 这里要的是"**这个版本装了什么**"（TargetLoader），不是"这次搜什么"
            //    （LoaderFilter）—— 这一步跑在铺下拉之前，那时下拉里还什么都没有。
            TargetLoader,
            _targetVersion) ?? string.Empty;
    }

    /// <summary>
    /// 用户本地这个版本装的是哪家加载器（给 mod 的加载器下拉做**预选**）。
    ///
    /// ⚠️ 跟 <see cref="LoaderFilter"/> 不是一回事：这个是"**我装了什么**"，
    ///    那个是"**这次搜什么**"。详情页底部"下载会落到哪个目录"、版本隔离的判断
    ///    用的是这个（用户没选过任何东西之前，那两个地方也得知道加载器）。
    /// </summary>
    private string TargetLoader => _type.UsesLoader
        ? _targetVersion?.LoaderKey ?? ModFilters.Any
        : ModFilters.Any;

    /// <summary>
    /// 这一次搜索实际带哪个加载器 = **用户在下拉里选的那个**。
    ///
    /// ⚠️ 没有加载器这一维的栏（数据包 / 资源包 / 光影包）一律「全部」：Modrinth 给
    ///    资源包 / 光影包的 `loaders` 是 `["minecraft"]`，塞 `categories:fabric`
    ///    会**静默**把结果筛没（实测 36437 → 16、983 → 5）—— 那种"搜什么都没结果"
    ///    最难查。（这几栏的加载器下拉本来就被收起来了，见 <see cref="InitFilters"/>。）
    /// </summary>
    private string LoaderFilter => _type.ShowsLoaderFilter ? SelectedValue(LoaderBox) : ModFilters.Any;

    /// <summary>
    /// 这一次搜索按哪个分类筛 = 用户选的那个（「全部」= 不给 facets 加条件）。
    ///
    /// ⚠️ 分类**必须按项目类型取**（见 <see cref="ModFilters.CategoriesFor"/>）：
    ///    拿 mod 的分类去筛资源包会静默返回 0 条。
    /// </summary>
    private string CategoryFilter
        => ModFilters.CategoriesFor(_type.ProjectType).Count > 0
            ? SelectedValue(CategoryBox)
            : ModFilters.Any;

    // ---------- 筛选（搜索卡上那四个下拉） ----------

    /// <summary>
    /// 铺搜索卡那四个下拉，并把默认值选好。
    ///
    /// 默认值是"**当前这个版本**"：游戏版本 = 它要的 MC 版本、加载器 = 它装的那家。
    /// 用户点进这一栏九成是想给这个版本找东西，进去就看到那一组最省事。
    /// 而四个下拉是**明摆着的**，所以他随时能改 —— 这比"偷偷按本地版本筛、
    /// 界面上完全看不出来"强得多（那种做法下"怎么搜都少一半"根本没法排查）。
    /// </summary>
    private void InitFilters()
    {
        // 卡片标题和搜索按钮都跟着类型走（= 1.0.0 的 _fill_type_tabs）：左栏换一栏
        // 之后，"现在搜的是哪一类"只能从这两处看出来（顶上那排类型标签早就删了）。
        var title = Localizer.Tr("搜索{kind}", ("kind", _type.Label));
        SearchCardTitle.Text = title;
        SearchButton.Content = title;

        FillCombo(GameVersionBox,
            ModFilters.FallbackVersions.Select(v => (Value: v, Label: v)),
            TargetMcVersion);

        // 加载器：只有 mod 和整合包有这一维。别的栏**整格收起来**，而不是留一个
        // 只有「全部」的下拉 —— 那种下拉看着就像坏了，用户还会怀疑是自己点错了。
        if (_type.ShowsLoaderFilter)
        {
            FillCombo(LoaderBox,
                ModFilters.Loaders.Select(o => (Value: o.Value, Label: OptionLabel(o))),
                TargetLoader);
        }
        else
        {
            HideFilterCell(LoaderLabel, LoaderBox, firstColumn: 2);
        }

        // 分类：数据包一个分类都没有（Modrinth 的 tag 表里就是空的），同样整格收起
        var categories = ModFilters.CategoriesFor(_type.ProjectType);
        if (categories.Count > 0)
        {
            FillCombo(CategoryBox,
                categories.Select(o => (Value: o.Value, Label: OptionLabel(o))),
                ModFilters.Any);
        }
        else
        {
            HideFilterCell(CategoryLabel, CategoryBox, firstColumn: 4);
        }

        // ⚠️ 排序**不带**「全部」那一项：排序不存在"不排"这种选法（1.0.0 也一样）
        FillCombo(SortBox,
            ModFilters.Sorts.Select(o => (Value: o.Value, Label: OptionLabel(o))),
            ModFilters.DefaultSort,
            includeAny: false);
    }

    /// <summary>一个候选的显示文案（产品名不过翻译表，见 <see cref="ModFilterOption.Translate"/>）。</summary>
    private static string OptionLabel(ModFilterOption option)
        => option.Translate ? Localizer.Tr(option.Label) : option.Label;

    /// <summary>
    /// 重建一个下拉的内容，并把 <paramref name="prefer"/> 选上。
    ///
    /// ⚠️ 全程压着 <see cref="_fillingFilters"/>：不压的话，"铺表"这件事本身就会被
    ///    当成"用户改了筛选"，页面一构造就自己搜一把。
    /// </summary>
    private void FillCombo(
        ComboBox box,
        IEnumerable<(string Value, string Label)> options,
        string prefer,
        bool includeAny = true)
    {
        _fillingFilters = true;
        try
        {
            box.Items.Clear();
            if (includeAny)
            {
                // 第一项永远是「全部」（= 不给 facets 加条件）。这个字符串要过
                // Localizer：模块级写死中文的话，切语言之后它不会跟着变。
                box.Items.Add(new ComboBoxItem
                {
                    Content = Localizer.Tr(ModFilters.Any),
                    Tag = ModFilters.Any,
                });
            }
            foreach (var (value, label) in options)
            {
                box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
            }
            SelectValue(box, prefer);
        }
        finally
        {
            _fillingFilters = false;
        }
    }

    /// <summary>
    /// 在一个下拉里选中 <paramref name="value"/>。
    ///
    /// 表里没有这个值时**把它加在最前面再选上**：比如本地那个版本是 `1.21.9-pre1`，
    /// 而接口给的正式版列表里没有它。悄悄退回「全部」是不行的 ——
    /// 那等于在用户不知情的时候把筛选条件改掉了。
    /// </summary>
    private static void SelectValue(ComboBox box, string? value)
    {
        var wanted = string.IsNullOrEmpty(value) ? ModFilters.Any : value;
        foreach (var item in box.Items)
        {
            if (item is ComboBoxItem option
                && string.Equals(option.Tag as string, wanted, StringComparison.Ordinal))
            {
                box.SelectedItem = option;
                return;
            }
        }

        var added = new ComboBoxItem { Content = wanted, Tag = wanted };
        box.Items.Insert(0, added);
        box.SelectedItem = added;
    }

    /// <summary>一个下拉当前选中的接口值（一项都没选的话是「全部」）。</summary>
    private static string SelectedValue(ComboBox box)
        => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? ModFilters.Any;

    /// <summary>
    /// 把一格筛选（标签 + 下拉）从布局里摘掉。
    ///
    /// ⚠️ 光 `Visibility.Collapsed` 不够：那两列是按 `*` 分宽度的，格子里没东西了
    ///    **列还在** —— 中间会留一段空白，后面的「分类」看着就跟标签对不上。
    ///    所以列宽一起归零（这两格在页面上只出现一次，摘掉不用复原）。
    /// </summary>
    private void HideFilterCell(FrameworkElement label, FrameworkElement box, int firstColumn)
    {
        label.Visibility = Visibility.Collapsed;
        box.Visibility = Visibility.Collapsed;
        FilterGrid.ColumnDefinitions[firstColumn].Width = new GridLength(0);
        FilterGrid.ColumnDefinitions[firstColumn + 1].Width = new GridLength(0);
    }

    /// <summary>当前这个版本要的 MC 版本（没装版本时「全部」）—— 游戏版本下拉的预选值。</summary>
    private string TargetMcVersion
        => _targetVersion?.MinecraftVersion is { Length: > 0 } mc ? mc : ModFilters.Any;

    /// <summary>改了筛选 → 直接重搜（下拉是"改了就生效"那种，没有单独的"应用"）。</summary>
    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingFilters)
        {
            return;     // 是我们在铺表 / 重置，不是用户在选
        }
        RequestSearch();
    }

    /// <summary>
    /// 第一次显示出来 → 搜一屏热门（见构造函数里挂 Dispatcher 那段说明）。
    ///
    /// ⚠️ 只该被调一次（构造函数里排一次）—— 切页 / 切语言重建是**新的一页实例**，
    ///    各自搜各自的，不会把用户已经翻到的结果冲掉。
    /// </summary>
    private void OnFirstLoaded()
    {
        if (_searchTask is not null)
        {
            return;     // `--query` 那条路已经搜过了
        }
        RequestSearch();
    }

    /// <summary>
    /// 重置：回到**刚进这一页时**的样子。
    ///
    /// 注意不是"回到什么都不筛"（1.0.0 的 `do_reset` 是那样）：这一页进来就预选了
    /// 当前版本的游戏版本 / 加载器，重置要是把它们清成「全部」，用户会以为
    /// "重置把我要的版本弄丢了"。重置 = 回到默认值。
    /// </summary>
    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        _fillingFilters = true;
        try
        {
            QueryBox.Text = string.Empty;
            SelectValue(GameVersionBox, TargetMcVersion);
            if (_type.ShowsLoaderFilter)
            {
                SelectValue(LoaderBox, TargetLoader);
            }
            if (ModFilters.CategoriesFor(_type.ProjectType).Count > 0)
            {
                SelectValue(CategoryBox, ModFilters.Any);
            }
            SelectValue(SortBox, ModFilters.DefaultSort);
        }
        finally
        {
            _fillingFilters = false;
        }

        UpdateQueryHint();
        RequestSearch();
    }

    /// <summary>
    /// 请一次搜索：没在搜就直接开；正在搜就记一笔，等这一轮收尾再补
    /// （理由见 <see cref="_pendingSearch"/>）。
    /// </summary>
    private void RequestSearch()
    {
        if (_searching)
        {
            _pendingSearch = true;
            return;
        }
        // 顺手记住这一次的任务：`--page mods:detail` 要等它落地才点得到第一张卡
        _searchTask = SearchAsync();
    }

    /// <summary>
    /// 把游戏版本 / 分类的候选换成接口给的**真实数据**（兜底表会慢慢变旧：
    /// 新版本它没有、分类改名了它还留着旧的 —— 旧分类**不报错，只是 0 条结果**）。
    ///
    /// ⚠️ 失败就什么都不做：兜底表还在，页面照样能搜。这里**不弹告警条** ——
    ///    "下拉里少一个刚出的版本"不值得在结果页顶上挂一条红字；真连不上 Modrinth
    ///    的话，用户一按搜索就会有明确报错，比这里更早也更准。
    /// ⚠️ 两条**互不牵连**（分开 try）：两个不同的接口，一条挂了不该拖累另一条。
    /// </summary>
    private Task RefreshFilterOptionsAsync()
        => Task.WhenAll(RefreshGameVersionsAsync(), RefreshCategoriesAsync());

    private async Task RefreshGameVersionsAsync()
    {
        try
        {
            var versions = await ModrinthApi.ReleaseVersionsAsync();
            if (versions.Count == 0)
            {
                return;
            }
            // ⚠️ 选中的值要在**铺之前**记下来：接口回来这半秒里用户很可能已经点过了，
            //    铺完不还回去就等于把他的选择悄悄改成「第一个」。
            var keep = SelectedValue(GameVersionBox);
            FillCombo(GameVersionBox, versions.Select(v => (Value: v, Label: v)), keep);
        }
        catch (Exception ex) when (IsOptionFetchFailure(ex))
        {
            // 兜底表已经铺好了，什么也不做
        }
    }

    private async Task RefreshCategoriesAsync()
    {
        if (ModFilters.CategoriesFor(_type.ProjectType).Count == 0)
        {
            return;     // 这一栏没有分类这一维（数据包），不用问
        }

        try
        {
            var live = await ModrinthApi.CategoriesAsync(_type.ProjectType);
            if (live.Count == 0)
            {
                return;
            }
            var keep = SelectedValue(CategoryBox);
            FillCombo(CategoryBox,
                live.Select(slug => (Value: slug, Label: ModFilters.CategoryLabel(slug))),
                keep);
        }
        catch (Exception ex) when (IsOptionFetchFailure(ex))
        {
            // 同 RefreshGameVersionsAsync：兜底表顶住
        }
    }

    /// <summary>这几类异常在"问接口要候选"这件事上都只算"这次没拿到"，不该冒到界面上。</summary>
    private static bool IsOptionFetchFailure(Exception ex)
        => ex is ModrinthException or HttpRequestException or TaskCanceledException
            or InvalidOperationException or NotSupportedException;

    // ---------- 排序 ----------

    /// <summary>
    /// 这一次搜索用哪个排序 = 用户选的那个（Modrinth 的 `index` 参数原文）。
    ///
    /// 2026-10 之前页顶有一排「相关度 / 下载量 / 关注数 / 最新 / 最近更新」的标签，
    /// 后来整排删掉、只剩一条"有关键词按相关度、空查询按下载量"的硬规则。现在这一维
    /// 回到搜索卡的「排序」下拉里，用户自己挑。
    ///
    /// ⚠️ 空查询配 `relevance` 仍然**没有意义**（没有关键词就没有"相关"可言，
    ///    Modrinth 会返回一串看不出规律的东西），所以引擎里留着同一层兜底：
    ///    `ModrinthApi.SearchProjectsAsync` 遇到空查询 + relevance 会换成 downloads。
    ///    两处都在是故意的：万一以后有别的地方直接调服务，行为也一致。
    /// </summary>
    private string SelectedSort
    {
        get
        {
            var sort = SelectedValue(SortBox);
            return sort.Length == 0 || string.Equals(sort, ModFilters.Any, StringComparison.Ordinal)
                ? ModFilters.DefaultSort
                : sort;
        }
    }

    // ---------- 搜索 ----------

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RequestSearch();
        }
    }

    private void OnSearchClick(object sender, RoutedEventArgs e) => RequestSearch();

    private async Task SearchAsync()
    {
        if (_searching)
        {
            return;
        }
        _searching = true;
        SearchButton.IsEnabled = false;
        HideNotice();

        var query = QueryBox.Text.Trim();
        ResultList.Items.Clear();
        EmptyText.Visibility = Visibility.Collapsed;
        // 新的一轮搜索 = 游标归零（续取那边靠它判断还有没有下一页）
        _resultOffset = 0;
        _resultTotal = 0;
        // 忙行单独存一份（下面的 finally 要拿它做"这句话是不是我写的"的判断）
        var busy = query.Length > 0
            ? Localizer.Tr("正在搜索「{q}」…", ("q", query))
            : Localizer.Tr("正在搜索…");
        StatusText.Text = busy;
        // 加载条负责"在等"这件事，状态行留给结果（1.0.0 也是加载条 + 结果两处分别说）
        SearchLoading.ShowState(Localizer.Tr("正在搜索…"));

        try
        {
            // 四个筛选条件全都**由用户在下拉里定**（默认值是当前这个版本，
            // 见 InitFilters）：游戏版本 / 加载器 / 分类 / 排序。
            // ⚠️ 加载器只有 mod 和整合包带（LoaderFilter），资源包塞加载器 facet
            //    会静默返回 0 条；分类同理（CategoryFilter）。
            // ⚠️ 这套条件要**记下来**：滑到底续取下一页时得原样再用一次
            //    （不能到时候重读下拉 —— 用户可能已经改了但还没按搜索）。
            _resultQuery = query;
            _resultGameVersion = SelectedValue(GameVersionBox);
            _resultLoader = LoaderFilter;
            _resultCategory = CategoryFilter;
            _resultSort = SelectedSort;

            var data = await App.Services.Mods.SearchAsync(
                query: _resultQuery,
                gameVersion: _resultGameVersion,
                loader: _resultLoader,
                category: _resultCategory,
                sort: _resultSort,
                limit: ResultPageSize,
                offset: 0,
                projectType: _type.ProjectType);

            ShowResults(data, query, append: false);
        }
        catch (ModrinthException ex)
        {
            ShowNotice(ex.Message);
            StatusText.Text = string.Empty;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or InvalidOperationException or NotSupportedException)
        {
            ShowNotice(Localizer.Tr("连不上 Modrinth（网络或代理）"));
            StatusText.Text = string.Empty;
        }
        finally
        {
            _searching = false;
            SearchButton.IsEnabled = true;
            // ⚠️ 放 finally：抛异常的分支也得收，不然会留一条永远转下去的加载条。
            //    错误本身由上面的告警条说，加载条不抢这个活。
            SearchLoading.HideWithDelay();

            // ⚠️ 「忙行不许活过 finally」—— 这条是 2026-10 排查"列表页挂着一句
            //    「正在获取 xxx 的版本…」"时补的：
            //    状态行是**这一页**的，可写它的地方不止这一页（那次是列表卡的「安装」，
            //    那次收尾按"当前看得见哪个面板"分流，用户中途点开过详情页就再没人回来清它）。
            //    现在这里做个兜底：这次搜索结束时状态行**要是还停在我刚写的那句忙行上**，
            //    就说明没人给过结论（异常类型没被上面的 catch 接住之类），清掉它 ——
            //    留一句"还在搜索"在结果已经出来之后，是最容易被当成"卡住了"的东西。
            //    注意是**按内容比对**：正常路径上 ShowResults / 上面两个 catch 早就
            //    把状态行换成了结果数或空串，这里不会去动它们。
            if (string.Equals(StatusText.Text, busy, StringComparison.Ordinal))
            {
                StatusText.Text = string.Empty;
            }

            // 搜索期间用户又改了筛选 → 现在补一次（见 _pendingSearch）。
            // ⚠️ 必须放在**这里的最后**：`_searching` 已经归 false，
            //    这一次才真的能开跑（放在前面会被自己刚立的标志挡住）。
            if (_pendingSearch)
            {
                _pendingSearch = false;
                _ = SearchAsync();
            }
        }
    }

    /// <summary>
    /// 把一页搜索结果铺进列表（<paramref name="append"/> = 追加到已有结果后面）。
    ///
    /// ⚠️ 追加时**不清空**列表，也不重置 `_resultOffset` —— 那是"滑到底续取"的游标，
    ///    清掉的话会一直重复取第一页。
    /// </summary>
    private void ShowResults(JsonObject data, string query, bool append)
    {
        var hits = (data["hits"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        var total = data["total_hits"]?.GetValue<int>() ?? hits.Count;

        if (!append)
        {
            ResultList.Items.Clear();
        }
        _resultOffset = append ? _resultOffset + hits.Count : hits.Count;
        _resultTotal = total;

        StatusText.Text = _resultOffset == 0
            ? string.Empty
            : Localizer.Tr("共 {n} 个结果，显示前 {m} 个", ("n", total), ("m", _resultOffset));

        EmptyText.Visibility = _resultOffset == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var hit in hits)
        {
            ResultList.Items.Add(new ListBoxItem
            {
                Content = BuildResultCard(hit),
                // Tag 留给"点进详情页"用（卡片自己也知道 hit，但列表项这一层
                // 是 `--page mods:detail` 找第一个结果时唯一拿得到的东西）
                Tag = hit,
                Style = (Style)FindResource("ModCardRow"),
            });
        }
    }

    // ---------- 滑到底续取（搜索结果） ----------

    /// <summary>已经取回来多少条（游标）。</summary>
    private int _resultOffset;

    /// <summary>这次搜索一共多少条（`total_hits`）。</summary>
    private int _resultTotal;

    /// <summary>这一次搜索用的那套条件（续取下一页要原样再用一次，见 <see cref="LoadMoreResultsAsync"/>）。</summary>
    private string _resultQuery = string.Empty;

    private string _resultGameVersion = ModFilters.Any;
    private string _resultLoader = ModFilters.Any;
    private string _resultCategory = ModFilters.Any;
    private string _resultSort = ModFilters.DefaultSort;

    /// <summary>每页取多少条（= 1.0.0 的 `PAGE_SIZE`）。</summary>
    private const int ResultPageSize = 20;

    /// <summary>正在取下一页（挡住 ScrollChanged 连发导致的重复请求）。</summary>
    private bool _loadingMore;

    /// <summary>
    /// 结果列表滚到**快见底**就续取下一页（= 1.0.0 的 `_maybe_load_more`）。
    ///
    /// 1.0.0 就是这么干的：`竖着拖到底 → 再抓 20 个`，没有"下一页"按钮。
    /// 我们之前只取第一页 20 条，底下那句"显示前 20 个"就永远停在 20 —— 用户 2026-10 报的。
    /// </summary>
    private async void OnResultsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 提前量压到 96（≈ 一张结果卡的高度）：真滑到底才续取。
        // 原来给 240 = 还剩两三张卡就开始下一批，用户只是往下瞄一眼列表就自己长出来，
        // 看着像"界面在抢操作"（2026-10-18 反馈）。
        if (!ScrollLoader.ShouldLoadMore(
                e.VerticalOffset, e.ViewportHeight, e.ExtentHeight, threshold: 96))
        {
            return;
        }
        await LoadMoreResultsAsync();
    }

    /// <summary>再取一页结果追加到列表后面（搜索中 / 已经在取 / 没有更多，都不动）。</summary>
    private async Task LoadMoreResultsAsync()
    {
        if (_searching || _loadingMore)
        {
            return;
        }
        if (_resultOffset <= 0 || _resultOffset >= _resultTotal)
        {
            return;     // 还没搜过 / 已经全取回来了
        }

        _loadingMore = true;
        var busy = Localizer.Tr("正在加载更多…");
        StatusText.Text = busy;
        SearchLoading.ShowState(busy);

        try
        {
            // ⚠️ 条件要用**这一次搜索时定的那套**（`_resultQuery` + 四个下拉），
            //    不能重新读输入框：用户可能已经改了关键词还没按搜索 ——
            //    那样取回来的会是另一组结果，跟列表里已有的混在一起。
            var data = await App.Services.Mods.SearchAsync(
                query: _resultQuery,
                gameVersion: _resultGameVersion,
                loader: _resultLoader,
                category: _resultCategory,
                sort: _resultSort,
                limit: ResultPageSize,
                offset: _resultOffset,
                projectType: _type.ProjectType);

            ShowResults(data, _resultQuery, append: true);
        }
        catch (ModrinthException ex)
        {
            ShowNotice(ex.Message);
            StatusText.Text = string.Empty;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or InvalidOperationException or NotSupportedException)
        {
            ShowNotice(Localizer.Tr("连不上 Modrinth（网络或代理）"));
            StatusText.Text = string.Empty;
        }
        finally
        {
            _loadingMore = false;
            SearchLoading.HideWithDelay();
            if (string.Equals(StatusText.Text, busy, StringComparison.Ordinal))
            {
                StatusText.Text = string.Empty;
            }
        }
    }

    // ---------- 结果卡（1.0.0 的 #ModCard） ----------

    /// <summary>
    /// 一条搜索结果 = 一张 1.0.0 的 #ModCard：
    ///
    ///     ┌──────────────────────────────────────────────────────────────┐
    ///     │ ┌────┐ Sodium                作者：jellysquid              ›  │
    ///     │ │ 64 │ 现代渲染优化，显著提升帧率…                            │
    ///     │ └────┘ [模组] [Fabric]  支持 8 个游戏版本   下载 1.5M  关注 12.3K │
    ///     └──────────────────────────────────────────────────────────────┘
    ///
    /// 所有尺寸/字号/颜色都走 <c>Themes\Controls.xaml</c> 里的 ModCard* 样式，
    /// **这里一个色值都不写** —— 换主题时才不用回来改。
    ///
    /// ⚠️ 行尾那个「›」是**指向符，不是按钮**（见下面那段注释）：卡片上的动作只剩
    ///    "点开进详情页"这一个，二级页面里的绿色「下载」才是唯一的安装入口。
    ///
    /// ⚠️ 取值全部走**防呆**（`Str` / `Num`）：Modrinth 的字段不是每个项目都有
    ///    （`author` / `icon_url` 尤其常见缺失），直接索引会 NRE ——
    ///    而一个字段缺失不该让整页搜不出来。
    /// </summary>
    private Border BuildResultCard(JsonObject hit)
    {
        // 接口没给 project_type 时按**这一页的类型**兜底（不是硬编码 "mod"）：
        // 资源包那一栏里缺字段的一条结果，徽章和兜底图标都该是"资源包"那份。
        var projectType = Str(hit, "project_type", _type.ProjectType);

        var root = new Border
        {
            Style = (Style)FindResource("ModCard"),
            Cursor = Cursors.Hand,
            // 整条 hit 留着：点卡片进详情页要用它的 slug / project_id
            // （详情页那套内容全部来自这一条，不再多打一次 /project/{id}）
            Tag = hit,
            // 卡片之间的间距：QSS 里靠布局的 spacing，WPF 这边由行自己带
            Margin = new Thickness(0, 3, 0, 3),
            ToolTip = CardTooltip(hit),
        };
        // 点卡片 = 进详情页（1.0.0 的 _ModCard.mouseReleaseEvent 也是这个行为）。
        root.MouseLeftButtonUp += (_, _) => OpenDetail(hit);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 图标
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });   // 中间
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 行尾指向符

        // ----- 图标：底块 + 图两层（图没下来也不会是空洞） -----
        var iconWell = new Border { Style = (Style)FindResource("ModCardIconWell") };
        var iconClip = new RectangleGeometry
        {
            RadiusX = ModIcons.CardCornerRadius,
            RadiusY = ModIcons.CardCornerRadius,
        };
        iconWell.Clip = iconClip;
        // ⚠️ Clip 的矩形不会跟着 Border 自动更新，尺寸一变就得重算。
        //    这里尺寸是样式写死的 64，所以只算一次。
        iconWell.SizeChanged += (_, _) => iconClip.Rect = new Rect(0, 0, iconWell.ActualWidth, iconWell.ActualHeight);

        var icon = new Image
        {
            Width = ModIcons.CardSize,
            Height = ModIcons.CardSize,
            Stretch = Stretch.Uniform,
        };
        // 缩图质量：源的 64/128 缩到 64 逻辑像素，HighQuality 在这档最干净。
        // ⚠️ RenderOptions 是**附加属性**，不能用对象初始化器写，只能这么设。
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        iconWell.Child = icon;
        Grid.SetColumn(iconWell, 0);
        row.Children.Add(iconWell);

        // 先挂兜底（项目类型决定用哪张内置方块图），真图下来了再换掉。
        // 这样"正在下载"和"下载失败"看起来都是"有个方块"，而不是一个空框。
        var fallbackName = ModIcons.FallbackNameFor(projectType);
        icon.Source = VersionIcons.Load(fallbackName, ModIcons.CardSize, DpiScale);
        StartIconLoad(icon, hit);

        // ----- 中间：名字 + 作者 / 简介 / 元信息 -----
        var mid = new StackPanel
        {
            // 图标和文字之间空出来的那 12px 就是 1.0.0 的 `spacing: 14`（那边的图标坑位
            // 右边自带一段空白）。⚠️ 别把它调到 0：试过，文字会贴着图标，很挤。
            //（原来右边还给绿色「安装」按钮留过位，按钮去掉之后中间这列可以吃满宽度了。）
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var nameRow = new Grid();
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameText = new TextBlock
        {
            Text = Str(hit, "title", "?"),
            Style = (Style)FindResource("ModCardName"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(nameText, 0);
        nameRow.Children.Add(nameText);

        // 作者：**有才加**（1.0.0 也是 `if author:`）—— 缺字段时不要留一行"作者：—"
        if (Str(hit, "author", string.Empty) is { Length: > 0 } author)
        {
            var authorText = new TextBlock
            {
                Text = Localizer.Tr("作者：{name}", ("name", author)),
                Style = (Style)FindResource("ModCardAuthor"),
                Margin = new Thickness(8, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(authorText, 1);
            nameRow.Children.Add(authorText);
        }

        mid.Children.Add(nameRow);

        var desc = new TextBlock
        {
            Text = Str(hit, "description", string.Empty),
            Style = (Style)FindResource("ModCardDesc"),
            Margin = new Thickness(0, 4, 0, 0),
        };
        // **最多两行**：第二行放不下就省略号（WPF 没有 max-lines，见 ModTextLines）
        ModTextLines.Clamp(desc, 2);
        mid.Children.Add(desc);

        mid.Children.Add(BuildMetaRow(hit, projectType));

        Grid.SetColumn(mid, 1);
        row.Children.Add(mid);

        // ----- 行尾：一个「›」 -----
        //
        // 这里原来是一个绿色的「安装」按钮，2026-10 删掉。理由（用户提的，两条都成立）：
        //   · 二级页面（列表 → 详情）做好之后它是**第二个安装入口**：版本是在详情页里挑的，
        //     列表上这个按钮只会"拿最新匹配的一版直接装"，跟详情页的行为重叠；
        //   · 它挨着"点卡片进详情"的整块热区，想点开详情的人很容易误点到它、直接装了个东西。
        //
        // 删掉之后行尾空出一大块、卡片看着不像能点，所以补一个**低调的**指向符：
        //   · 不是控件、不可点、不抢焦点 —— 图标、名称、作者、描述、徽章、
        //     "支持 N 个游戏版本 · 下载 x · 关注 y" 一个都没少，只是右侧多了个"还有一层"的暗示；
        //   · 样式走 Controls.xaml 的 ModCardChevron：卡片里"中灰"那一档，**不引入任何强调色**
        //     —— 卡片上唯一允许抢眼的东西仍然是 hover 时的强调色描边；
        //   · 也**没有**用"点开选版本"这种小字：`›` 是个符号，不用翻译，
        //     5 个语言文件不会因为它多出一条只有中文的文案。
        // 卡片"能点"这件事主要还是靠 hover（浮起底 + 强调色描边，见 Controls.xaml 的 ModCard），
        // 鼠标指针也是手型（root.Cursor）。
        var chevron = new TextBlock
        {
            Text = "›",
            Style = (Style)FindResource("ModCardChevron"),
            Margin = new Thickness(10, 0, 0, 0),
        };
        Grid.SetColumn(chevron, 2);
        row.Children.Add(chevron);

        root.Child = row;
        return root;
    }

    /// <summary>
    /// 底部那一行元信息：类型徽章 + 加载器徽章（左）、支持版本数 + 下载量 + 关注数（右）。
    ///
    /// 分左右两组是 1.0.0 的排法（`meta.addStretch()` 把两边顶开）：
    /// 左边是"这个东西是什么"，右边是"它有多热 / 能不能装上" —— 扫一眼就分得开。
    ///
    /// ## 「支持 N 个游戏版本」为什么又加回来了
    /// 上一版**故意没放**：那时右边还占着 320px 的「已装的模组」栏，
    /// 卡片实测放不下（四个加载器徽章 + 统计数字已经贴着内沿，再加这串 118px
    /// 的小字，最后那个徽章必然被裁掉一半）。
    /// 那块面板 2026-10 删掉之后结果栏是整页宽，地方够了 —— 加回来。
    ///
    /// ⚠️ 加回来就必须**保证它不被截断**：所以它自己占一个 **Auto 列**
    ///    （见下面三列的排法）。WPF 的 Grid 在宽度不够时先压 `*` 列，Auto 列
    ///    拿到的永远是内容宽度 —— 于是徽章先被裁，这行小字反而是安全的。
    ///    反过来把它塞进左边那个 `ClipToBounds` 的徽章条里，它就会跟着被裁。
    ///
    /// ⚠️ 右边那组的容器**不能**设 HorizontalAlignment=Right —— 那是 WPF 的经典坑：
    ///    在 Grid 的 `*` 列里，右对齐的子元素会把整列位置都算成"它自己要的地方"，
    ///    于是 Auto 列量出来是满宽、`*` 列被压成 0，左边的徽章只剩半个
    ///    （实测撞到过：卡片上只剩"模组"和一个"F"）。
    ///    要贴右边就让**它所在的列**是 Auto：Auto 列自己缩到内容宽度，
    ///    而左边那列是 `*`，剩下的宽度自然都归徽章。
    /// </summary>
    private Grid BuildMetaRow(JsonObject hit, string projectType)
    {
        var meta = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        // 0：徽章（*，徽章是按名称算宽的，一般够；不够就裁它）
        // 1：支持 N 个游戏版本（Auto，一个都不许被切）
        // 2：统计数字（Auto，同上）
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            // ⚠️ 卡片的可用宽度是从外面一路传下来的，极端窄的时候徽章一行会比它长。
            //    不裁的话会画到卡片外面、叠在统计数字上（实测撞到过）。
            //    裁掉最后一个徽章比叠上去强。
            ClipToBounds = true,
        };
        left.Children.Add(Badge(TypeLabel(projectType), "ModTypeBadge"));

        // 加载器徽章：搜索接口只在 categories / display_categories 里给加载器名
        foreach (var (key, label) in LoaderLabels)
        {
            if (HasCategory(hit, key))
            {
                var tag = Badge(label, "BadgeAccent");
                tag.Margin = new Thickness(6, 0, 0, 0);
                left.Children.Add(tag);
            }
        }

        Grid.SetColumn(left, 0);
        meta.Children.Add(left);

        // 「支持 N 个游戏版本」：1.0.0 的排法（夹在加载器徽章和统计数字中间）。
        // 接口不给 `versions` 时**整块不显示** —— 写个"支持 0 个"比不写更糟。
        if (hit["versions"] is JsonArray versions && versions.Count > 0)
        {
            var versionCount = new TextBlock
            {
                Text = Localizer.Tr("支持 {n} 个游戏版本", ("n", versions.Count)),
                Style = (Style)FindResource("ModCardMeta"),
                Margin = new Thickness(10, 0, 0, 0),
                // 明确写出来：这一行**不换行、不省略**（见方法注释）
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.None,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(versionCount, 1);
            meta.Children.Add(versionCount);
        }

        // 右边两个统计数字：1.0.0 的 format_number → 1.5M / 12.3K
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            // 徽章和统计数字之间留最小间隙，免得贴在一起
            Margin = new Thickness(8, 0, 0, 0),
        };
        right.Children.Add(new TextBlock
        {
            Text = Localizer.Tr("下载 {n}", ("n", FormatNumber(Num(hit, "downloads")))),
            Style = (Style)FindResource("ModCardMeta"),
        });
        right.Children.Add(new TextBlock
        {
            Text = Localizer.Tr("关注 {n}", ("n", FormatNumber(Num(hit, "follows")))),
            Style = (Style)FindResource("ModCardMeta"),
            Margin = new Thickness(12, 0, 0, 0),
        });

        Grid.SetColumn(right, 2);
        meta.Children.Add(right);

        return meta;
    }

    /// <summary>
    /// 卡片的 ToolTip：标题 + 作者 + 支持多少个游戏版本。
    ///
    /// ⚠️ 「支持 N 个版本」现在**卡片上也写了**（见 <see cref="BuildMetaRow"/>），
    ///    这里保留是因为 ToolTip 顺带给了完整标题 —— 名字被省略号截掉时，
    ///    鼠标停一下还能看全。
    /// </summary>
    private string CardTooltip(JsonObject hit)
    {
        var lines = new List<string> { Str(hit, "title", "?") };
        if (Str(hit, "author", string.Empty) is { Length: > 0 } author)
        {
            lines.Add(Localizer.Tr("作者：{name}", ("name", author)));
        }
        if (hit["versions"] is JsonArray versions && versions.Count > 0)
        {
            lines.Add(Localizer.Tr("支持 {n} 个游戏版本", ("n", versions.Count)));
        }
        return string.Join("\n", lines);
    }

    private Border Badge(string text, string styleKey) => new()
    {
        Style = (Style)FindResource(styleKey),
        Child = new TextBlock { Text = text },
    };

    /// <summary>项目类型 → 界面文案（认不出来原样返回，1.0.0 的 `project_type_label`）。</summary>
    private static string TypeLabel(string projectType)
    {
        var key = (projectType ?? string.Empty).ToLowerInvariant();
        return TypeLabels.TryGetValue(key, out var label) ? Localizer.Tr(label) : key;
    }

    /// <summary>这个项目在不在某个分类里（`display_categories` 优先，退回 `categories`）。</summary>
    private static bool HasCategory(JsonObject hit, string category)
    {
        var cats = (hit["display_categories"] as JsonArray) ?? (hit["categories"] as JsonArray);
        if (cats is null)
        {
            return false;
        }
        foreach (var node in cats)
        {
            if (string.Equals(node?.GetValue<string>(), category, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // ---------- 图标 ----------

    /// <summary>
    /// 起一次图标加载：下好了把 <see cref="Image.Source"/> 换掉。
    ///
    /// ⚠️ 回来的时候要**核对 url**（1.0.0 的 `_load_icon` 也这么做）：列表是会被
    ///    重建的（换排序、再搜一次），`await` 期间这张卡可能已经换了内容 ——
    ///    对不上就直接丢，别把 A 项目的图标贴到 B 项目上。
    ///
    /// ⚠️ 也**绝不抛**：图标是锦上添花，失败就留着已经挂好的兜底方块图。
    /// </summary>
    private void StartIconLoad(Image icon, JsonObject hit)
    {
        var url = Str(hit, "icon_url", string.Empty);

        // url 为空：兜底图已经在 <see cref="BuildResultCard"/> 里挂了，到此为止。
        // （这一条要**先判**，不然会白起一个什么都不做的 async 状态机）
        if (url.Length == 0)
        {
            return;
        }

        icon.Tag = url;
        _ = LoadIconAsync(icon, url);
    }

    private async Task LoadIconAsync(Image icon, string url)
    {
        try
        {
            var image = await ModIcons.LoadAsync(url, ModIcons.CardSize, DpiScale);
            if (image is null || !ReferenceEquals(icon.Tag as string, url))
            {
                return;
            }
            icon.Source = image;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // ModIcons 自己已经把网络/解码的错吞了；能漏到这里的是"图坏了"这类
            // 边角情况 —— 照样留着兜底图，不往上报。
        }
    }

    private static string Str(JsonObject obj, string key, string fallback)
        => obj[key]?.GetValue<string>() is { Length: > 0 } text ? text : fallback;

    /// <summary>取一个数字字段（缺字段 / 类型不对都当 0，不抛）。</summary>
    private static long Num(JsonObject obj, string key)
    {
        try
        {
            return obj[key]?.GetValue<long>() ?? 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return 0;
        }
    }

    /// <summary>
    /// `1234567` → `1.2M`、`12345` → `12.3K`（1.0.0 的 `format_number`）：
    /// 卡片上放不下完整数字，而且"12.3K"扫一眼就知道量级。
    ///
    /// ⚠️ 用 K / M 而不是万 / 亿：Modrinth 是国际站，界面语言有 5 种，
    ///    中文的"万"在英文/日文界面里莫名其妙（1.0.0 用的也是 K / M）。
    /// </summary>
    private static string FormatNumber(long value)
    {
        if (value >= 1_000_000)
        {
            return (value / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M";
        }
        if (value >= 1_000)
        {
            return (value / 1_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "K";
        }
        return value.ToString(CultureInfo.InvariantCulture);
    }

    // ---------- 安装 ----------

    // ⚠️ 这里原来还有一个「装最新匹配的那一版」的入口
    //    （`OnInstallClick` + `MatchingVersionsAsync`，配列表卡右侧那个绿色「安装」按钮），
    //    2026-10 整个删掉了。删的理由有两层：
    //
    //    一、界面上是重复入口：二级页面做好之后，版本是在**详情页**里挑的，
    //        列表上那个按钮只会"挑最新一版直接装"，跟详情页的「下载」重叠、还容易误点。
    //
    //    二、它留下了本页唯一一处"没人负责收尾"的状态：那条忙行
    //        「正在获取 {name} 的版本…」是**写在列表页状态行**上的，可收尾
    //        （进度 / 报错 / 成功）却由 `ReportProgress` / `ReportError` 按
    //        "当前看得见哪个面板"分流 —— 只要用户在这期间点开过详情页再返回，
    //        这句忙行就**永远留在列表页上**（看着像卡住了）。排查记录见
    //        `SearchAsync` 里那段"忙行不许活过 finally"的注释。
    //
    //    现在安装只剩一条路：详情页每一行右侧的绿色「下载」→ `InstallVersionAsync`。

    /// <summary>
    /// 真装某一版。进度和失败原因都写到**当前看得见的那一处**（详情页底部那行 /
    /// 列表页的状态行 + 告警条），见 <see cref="ReportProgress"/>。
    ///
    /// 三条路，按 <see cref="ModContentType.Target"/> 分：
    ///   · <see cref="ModInstallTarget.ContentDir"/>（模组 / 资源包 / 光影）
    ///     → `IModService.InstallAsync`，目录由引擎按类型算
    ///   · <see cref="ModInstallTarget.ModpackInstance"/>（整合包）
    ///     → 先把 `.mrpack` 的地址交给 `IModpackService`（它自己下 + 装成独立实例）
    ///   · <see cref="ModInstallTarget.NotImplemented"/>（数据包）
    ///     → **明说还没接**，不装、也不假装在装
    /// </summary>
    private async Task InstallVersionAsync(JsonObject version)
    {
        if (_type.Target == ModInstallTarget.NotImplemented)
        {
            // 理论上到不了这里（下载页那一栏走的是「还没接」说明页，压根没有列表），
            // 但列表页真被别处直接建出来时，这句比"点了没反应"强得多。
            ReportError(Localizer.Tr("{kind}的下载还没接 —— 数据包要先进某一个存档。",
                ("kind", _type.Label)));
            return;
        }

        if (_targetVersion is null)
        {
            // ⚠️ 别静默返回：`_targetVersion` 为空时详情页照样列得出"全部版本"，
            //    点了「下载」却什么都不发生是最没头没脑的一种反馈。
            ReportError(Localizer.Tr(
                "还没有本地版本 —— 先去「下载」页装一个，再回来装{kind}。", ("kind", _type.Label)));
            return;
        }

        if (_type.Target == ModInstallTarget.ModpackInstance)
        {
            await InstallModpackAsync(version);
            return;
        }

        // ⚠️ 先问"装到哪儿"（= PCL 那种先选目录再下载）。以前是直接下到
        //    "上次启动的那个版本"里，用户完全没得选。
        //    整合包不走这条（它装成一个独立实例，落点由整合包自己的名字决定）。
        var targetDir = AskTargetDir();
        if (targetDir is null)
        {
            return;     // 用户取消了：什么都不做，也别报"失败"
        }

        HideNotice();

        // 下载进度窗口（= 1.0.0 那个"正在下载 …"）。标题用版本名（`Sodium 1.0.1` 那种）。
        var window = App.OpenDownloadWindow(Str(version, "name", Str(version, "version_number", _type.Label)));

        try
        {
            var progress = new Progress<LaunchProgress>(p =>
            {
                ReportProgress(p.Message);
                window.SetStageNote(p.Message);
            });
            var result = await App.Services.Mods.InstallAsync(
                version, App.Services.Games.MinecraftDir,
                // ⚠️ 下面这三个（游戏版本 / 加载器 / 版本信息）只在**没给 targetDir** 时
                //    才被引擎用来算目录；这次目录是用户选的，传什么都不会影响落点 ——
                //    仍然照实传当前版本的信息，免得以后引擎里加了别的用途就对不上。
                _targetVersion.MinecraftVersion,
                _targetVersion.LoaderKey ?? string.Empty,
                _targetVersion, progress,
                // ⚠️ 类型走**具名参数**传在末尾（接口上那个参数是追加的、默认 "mod"）：
                //    目录由引擎按类型算，这里不再自己拼路径
                projectType: _type.ProjectType,
                // 用户在「装到哪儿？」里选的那个目录
                targetDir: targetDir);

            ReportProgress(result.Message);
            window.Finish(result.Message);
            if (!result.Ok)
            {
                ReportError(result.Message);
            }
        }
        catch (ModrinthException ex)
        {
            ReportError(ex.Message);
            window.Finish(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or IOException or UnauthorizedAccessException
                                      or InvalidOperationException)
        {
            ReportError(Localizer.Tr("{kind}装失败：{err}", ("kind", _type.Label), ("err", ex.Message)));
            window.Finish(Localizer.Tr("{kind}装失败：{err}", ("kind", _type.Label), ("err", ex.Message)));
        }
    }

    /// <summary>
    /// 装一个 Modrinth 上的整合包。
    ///
    /// ⚠️ **不自己写整合包逻辑**：把 `.mrpack` 的下载地址交给工程已有的
    ///    <see cref="IModpackService"/>（`RealModpackService`）——
    ///    它自己下到临时文件、解析、建 `versions/&lt;实例名&gt;/`、铺 mod 和配置、
    ///    装加载器、写版本 JSON。这里只负责挑出"该下哪个文件"和把进度说出来。
    ///
    /// 落点跟"从本地选 .mrpack"那条路**完全一样**（同一条流水线），
    /// 所以详情页底部那句提示写的是实例目录，不是 `mods/`。
    /// </summary>
    private async Task InstallModpackAsync(JsonObject version)
    {
        var file = ModrinthApi.PrimaryFile(version);
        var url = file?["url"]?.GetValue<string>() ?? string.Empty;
        if (url.Length == 0)
        {
            ReportError(Localizer.Tr("这个整合包版本没有可下载的文件"));
            return;
        }

        HideNotice();

        // 下载进度窗口：标题用这个整合包的版本名（装的过程是"先下 .mrpack，再下里面那些 mod"）
        var window = App.OpenDownloadWindow(Str(version, "name", _type.Label));

        try
        {
            var progress = new Progress<LaunchProgress>(p =>
            {
                ReportProgress(p.Message);
                window.SetStageNote(p.Message);
            });
            var result = await App.Services.Modpacks.InstallAsync(new ModpackInstallRequest(
                // 本地路径给空串：这次是从 Modrinth 直接装，让服务自己去下（见 MrpackUrl）
                MrpackPath: string.Empty,
                MinecraftDir: App.Services.Games.MinecraftDir,
                // null = 用整合包自己的名字（服务里消毒成合法目录名）
                InstanceName: null,
                InstallLoader: true,
                MrpackUrl: url), progress);

            ReportProgress(result.Message);
            window.Finish(result.Message);
            if (!result.Ok)
            {
                ReportError(result.Message);
            }
            else
            {
                // 装完要重扫本地版本，否则「版本管理」页看不到这个新实例
                await RefreshAfterInstallAsync();
            }
        }
        catch (OperationCanceledException)
        {
            ReportProgress(Localizer.Tr("已取消"));
            window.Finish(Localizer.Tr("已取消"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or IOException or UnauthorizedAccessException
                                      or InvalidOperationException)
        {
            ReportError(Localizer.Tr("装整合包失败：{err}", ("err", ex.Message)));
            window.Finish(Localizer.Tr("装整合包失败：{err}", ("err", ex.Message)));
        }
    }

    /// <summary>
    /// 装完重扫一遍本地版本。⚠️ 扫不动**不该**把"装好了"这句话改掉：
    /// 文件已经落盘了，扫不动只是列表暂时看不到，所以这里失败就算了（静默）。
    /// </summary>
    private static async Task RefreshAfterInstallAsync()
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
    }

    // ---------- 详情页（列表 → 详情 的二级页面） ----------

    /// <summary>
    /// 进详情页：把列表整块换掉（两个面板叠在同一个 Grid 里，切 Visibility）。
    ///
    /// 移植自 1.0.0 的 `mods_page` 详情页那一段。详情卡的内容全部来自
    /// **搜索结果那一条 hit**（title / slug / author / description / icon_url /
    /// downloads / follows / categories 都在里面），不再多打一次 `/project/{id}`
    /// —— 少一次联网，而且列表和详情显示的天然是同一份数据。
    /// **版本列表**是唯一要额外拉的东西（搜索回包里只有一个版本 id 数组）。
    /// </summary>
    private void OpenDetail(JsonObject hit)
    {
        _detailHit = hit;
        HideNotice();

        ResultsPane.Visibility = Visibility.Collapsed;
        DetailPane.Visibility = Visibility.Visible;
        // 宿主（下载页的整合包那一栏）据此把上面那张本地 .mrpack 卡收起来
        DetailOpenedChanged?.Invoke(this, true);

        FillDetailCard(hit);
        RefreshFavoriteButton();
        _ = LoadDetailVersionsAsync(hit);
    }

    /// <summary>
    /// 从收藏夹点进来：直接把收藏里存的那份搜索命中塞给详情页。
    ///
    /// ⚠️ 走的是**同一个** <see cref="OpenDetail"/>：收藏夹里的项目和搜出来的项目
    ///    在详情页上不该有任何区别（版本列表、装到哪儿、下载按钮都一样）。
    /// </summary>
    public void OpenDetailFromFavorite(JsonObject hit) => OpenDetail(hit);

    // ---------- 收藏 ----------

    /// <summary>按当前详情页的项目刷那颗星（没进详情页就藏起来）。</summary>
    private void RefreshFavoriteButton()
    {
        if (_detailHit is null)
        {
            FavoriteButton.Content = string.Empty;
            FavoriteButton.Visibility = Visibility.Collapsed;
            return;
        }

        FavoriteButton.Visibility = Visibility.Visible;
        var saved = App.Favorites.Contains(_type.ProjectType, _favoriteId);
        FavoriteButton.Content = saved ? Localizer.Tr("★ 已收藏") : Localizer.Tr("☆ 收藏");
        FavoriteButton.ToolTip = saved ? Localizer.Tr("再点一下取消收藏") : Localizer.Tr("收藏这个项目");
    }

    /// <summary>收藏用的项目 id（Modrinth 的 `project_id`，没有就用 slug 兜底）。</summary>
    private string _favoriteId => _detailHit is null
        ? string.Empty
        : Str(_detailHit, "project_id", Str(_detailHit, "slug", string.Empty));

    private void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (_detailHit is null || _favoriteId.Length == 0)
        {
            return;
        }

        var added = App.Favorites.Toggle(new Core.Services.FavoriteItem(
            ProjectType: _type.ProjectType,
            ProjectId: _favoriteId,
            Title: Str(_detailHit, "title", "?"),
            IconUrl: Str(_detailHit, "icon_url", string.Empty),
            // ⚠️ 存**整份**命中（详情页要 project_id 拉版本列表，图标/标题/摘要也都在里头）
            Hit: (JsonObject)_detailHit.DeepClone(),
            AddedAt: DateTimeOffset.Now));
        RefreshFavoriteButton();
        // ⚠️ 用**浮层提示**，不是页面里那条 `NoticeBanner`：
        //    那条是排在布局里的（一出现就把下面的东西顶下去）、而且得有人负责收掉。
        //    收藏是"点一下就该过去"的反馈，用 toast 更合适（见 `MainWindow.ShowToast`）。
        MainWindow.Current?.ShowToast(added
            ? Localizer.Tr("已加入收藏夹")
            : Localizer.Tr("已从收藏夹移除"));
    }

    private void OnBackToResultsClick(object sender, RoutedEventArgs e)
    {
        // ⚠️ 返回时把详情页的加载条**立刻**收掉：用户可能在加载途中就返回了，
        //    不收的话列表页上会挂着一条"还在转"的条（数据其实早到了 / 早不来了）。
        DetailLoading.HideNow();
        _detailHit = null;
        DetailPane.Visibility = Visibility.Collapsed;
        ResultsPane.Visibility = Visibility.Visible;
        DetailOpenedChanged?.Invoke(this, false);
    }

    /// <summary>详情页顶上那张信息卡（图标 + 名字 + slug + 作者 + 简介 + 徽章 + 统计）。</summary>
    private void FillDetailCard(JsonObject hit)
    {
        // 同搜索卡：接口没给 project_type 时按这一页的类型兜底
        var projectType = Str(hit, "project_type", _type.ProjectType);

        DetailName.Text = Str(hit, "title", "?");
        DetailSlug.Text = Str(hit, "slug", string.Empty);
        DetailSlug.Visibility = DetailSlug.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var lines = new List<string>();
        if (Str(hit, "author", string.Empty) is { Length: > 0 } author)
        {
            lines.Add(Localizer.Tr("作者：{name}", ("name", author)));
        }
        lines.Add(TypeLabel(projectType));
        DetailMeta.Text = string.Join("    ·    ", lines);

        var description = Str(hit, "description", string.Empty);
        DetailDesc.Text = description;
        DetailDesc.Visibility = description.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // 简介最多四行：1.0.0 那边详情卡里的简介是**不限行**的（页面按固定高度排），
        // WPF 这边详情卡高度自适应，一段两千字的简介会把版本列表挤没。
        // ⚠️ 用 ModTextLines 而不是 XAML 里写 MaxHeight：那个是按"猜的行高"截的，
        //    字号 / DPI 一变就会把最后一行切成半行。
        ModTextLines.Clamp(DetailDesc, 4);

        DetailBadges.Children.Clear();
        DetailBadges.Children.Add(Badge(TypeLabel(projectType), "ModTypeBadge"));
        foreach (var (key, label) in LoaderLabels)
        {
            if (HasCategory(hit, key))
            {
                var tag = Badge(label, "BadgeAccent");
                tag.Margin = new Thickness(6, 0, 0, 0);
                DetailBadges.Children.Add(tag);
            }
        }

        var stats = new List<string>
        {
            Localizer.Tr("下载 {n}", ("n", FormatNumber(Num(hit, "downloads")))),
            Localizer.Tr("关注 {n}", ("n", FormatNumber(Num(hit, "follows")))),
        };
        if (hit["versions"] is JsonArray versions && versions.Count > 0)
        {
            stats.Add(Localizer.Tr("支持 {n} 个游戏版本", ("n", versions.Count)));
        }
        DetailStats.Text = string.Join("    ·    ", stats);

        FillDetailIcon(hit, projectType);
    }

    /// <summary>
    /// 详情页的大图标。⚠️ 跟搜索卡一个做法：先挂内置兜底方块图，真图下来了再换 ——
    /// 图没下来 / 下载失败时看到的是"一个方块"，不是一块空白。
    /// </summary>
    private void FillDetailIcon(JsonObject hit, string projectType)
    {
        DetailIcon.Source = VersionIcons.Load(ModIcons.FallbackNameFor(projectType), DetailIconSize, DpiScale);

        var url = Str(hit, "icon_url", string.Empty);
        // url 为空：兜底图已经挂上了，到此为止（别白起一个什么都不做的 async 状态机）
        if (url.Length == 0)
        {
            return;
        }
        DetailIcon.Tag = url;
        _ = LoadDetailIconAsync(url);
    }

    private async Task LoadDetailIconAsync(string url)
    {
        try
        {
            var image = await ModIcons.LoadAsync(url, DetailIconSize, DpiScale);
            // 跟搜索卡一样要**核对 url**：加载途中用户可能已经返回 / 点了别的项目
            if (image is null || !ReferenceEquals(DetailIcon.Tag as string, url))
            {
                return;
            }
            DetailIcon.Source = image;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // 图标是锦上添花，失败就留着兜底方块图
        }
    }

    /// <summary>
    /// 拉这个项目的版本列表，按「游戏版本 × 加载器 × 正式/预览」**分成组**铺出来。
    ///
    /// ## 为什么是"分组"而不是"只留匹配的"
    /// 2.0 早先只把"匹配当前版本"的那些铺成一组，别的版本**根本看不到** ——
    /// 而"我要 1.12.2 那一版"恰恰是最常干的事（1.0.0 里 Fabric API 有四百多组）。
    /// 现在是：全部分组都铺，**对得上筛选的排最前并默认展开**，其余折叠在后面。
    ///
    /// ⚠️ 回来的时候要核对还是不是同一个项目（<see cref="_detailHit"/>）：
    ///    用户可能已经返回、又点了另一个 mod —— 不核对就会把 A 的版本铺到 B 的详情页上
    ///    （1.0.0 的 `_load_detail` 里也是同一条判断）。
    /// </summary>
    private async Task LoadDetailVersionsAsync(JsonObject hit)
    {
        var projectId = ProjectIdOf(hit);

        // 先把上一轮的分组清掉：清得晚一步，屏幕上就会留着上一个项目的版本
        _detailGroups = Array.Empty<ModVersionGroup>();
        _detailGroupsShown = 0;
        ClearDetailGroups();
        RefreshDetailHint(0, 0);

        if (projectId.Length == 0)
        {
            ShowDetailGroupMessage(Localizer.Tr("这个项目没有可下载的版本"));
            return;
        }

        DetailLoading.ShowState(Localizer.Tr("正在加载项目详情…"));
        try
        {
            var versions = await App.Services.Mods.ProjectVersionsAsync(projectId);
            if (!ReferenceEquals(_detailHit, hit))
            {
                return;      // 用户已经走了（或换了项目），别再动界面
            }

            // 置顶哪一组 = **用户在下拉里选的那个**（默认值就是本地版本，见 InitFilters）：
            // 底部那句话说"已按你筛选的 … 置顶"，这里就必须真的是"筛选的那个"。
            // ⚠️ 加载器只有 mod / 整合包有这一维（`ModContentType.ShowsLoaderFilter`）：
            //    资源包 / 光影包的版本把 `loaders` 当"能用在谁身上"（光影包常常同时标
            //    iris + optifine），按它分组会把同一批版本列成两套 —— 所以这两类传 false。
            var groups = App.Services.Mods.GroupVersions(
                versions,
                SelectedValue(GameVersionBox),
                _type.ShowsLoaderFilter ? LoaderFilter : ModrinthApi.AnyOption,
                splitByLoader: _type.ShowsLoaderFilter);

            if (groups.Count == 0)
            {
                ShowDetailGroupMessage(Localizer.Tr("这个项目没有可下载的版本"));
                return;
            }

            ApplyDetailGroups(groups);
            RefreshDetailHint(groups.Count, groups.Count(g => g.MatchesFilter));
        }
        catch (ModrinthException ex)
        {
            if (ReferenceEquals(_detailHit, hit))
            {
                ShowDetailGroupMessage(Localizer.Tr(
                    "拿不到项目数据。请稍后再试，或者换个网络（必要时用代理 / VPN）。"));
                DetailHint.Text = ex.Message;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or InvalidOperationException or NotSupportedException)
        {
            if (ReferenceEquals(_detailHit, hit))
            {
                ShowDetailGroupMessage(Localizer.Tr(
                    "拿不到项目数据。请稍后再试，或者换个网络（必要时用代理 / VPN）。"));
                DetailHint.Text = Localizer.Tr("连不上 Modrinth（网络或代理）");
            }
        }
        finally
        {
            // ⚠️ 放 finally：失败 / 用户中途返回这两条路也得收，不然会留一条永远转下去的条
            DetailLoading.HideWithDelay();
        }
    }

    // ---------- 版本分组（详情页中间那一摞） ----------

    /// <summary>这次详情页的全部分组（排序已经由引擎做好：匹配的在前）。</summary>
    private IReadOnlyList<ModVersionGroup> _detailGroups = Array.Empty<ModVersionGroup>();

    /// <summary>已经铺出来了几组（滑到底会往后加）。</summary>
    private int _detailGroupsShown;

    /// <summary>
    /// 每次「滑到底」追加几组。
    ///
    /// 为什么要有这个东西：Fabric API 那种项目**四百多组**，一次全建出来
    /// 就是四百个折叠标题 + 每个组内部的控件，界面会卡住好几秒。
    /// 12 是个折中值：一屏能看两三组，翻两次就到几十组。
    /// </summary>
    private const int DetailGroupBatch = 12;

    /// <summary>
    /// 详情页滚到**快见底**就续铺下一批分组（= 1.0.0 的 `_maybe_load_more_groups`）。
    ///
    /// ⚠️ 阈值给 160：等真滚到最底才加载的话，用户会看到"卡在最后一行等一下"，
    ///    留一点提前量才像"一直有内容"。
    /// ⚠️ 只认**竖向**变化：内容变化（增删行）也会触发 ScrollChanged，
    ///    不判 `ExtentHeight <= 0` 之类的早退条件会在铺行过程中反复进来。
    /// </summary>
    private void OnDetailScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ScrollLoader.ShouldLoadMore(e.VerticalOffset, e.ViewportHeight, e.ExtentHeight))
        {
            AppendDetailGroups();
        }
    }

    /// <summary>换一个项目 → 清空重来。</summary>
    private void ApplyDetailGroups(IReadOnlyList<ModVersionGroup> groups)
    {
        _detailGroups = groups;
        _detailGroupsShown = 0;
        ClearDetailGroups();
        AppendDetailGroups();
    }

    private void ClearDetailGroups()
    {
        DetailGroups.Children.Clear();
    }

    /// <summary>
    /// 把下一批分组铺出来（第一次调用 = 铺开头那批）。
    ///
    /// ⚠️ 铺完还要**看一眼有没有把一屏填满**：窗口很高、或者这个项目分组很少的时候，
    ///    12 组可能还不够一屏 —— 那样滚动条根本不会出现，用户永远触发不了"滑到底"，
    ///    剩下的分组就再也看不到了。所以铺完立刻补：只要"内容还没超过视口"就继续铺，
    ///    直到铺满一屏或者没有更多（组数有上限，不会死循环）。
    /// </summary>
    private void AppendDetailGroups()
    {
        if (_detailGroupsShown >= _detailGroups.Count)
        {
            return;
        }

        var end = Math.Min(_detailGroups.Count, _detailGroupsShown + DetailGroupBatch);
        for (var i = _detailGroupsShown; i < end; i++)
        {
            DetailGroups.Children.Add(BuildDetailGroup(_detailGroups[i]));
        }
        _detailGroupsShown = end;

        if (_detailGroupsShown < _detailGroups.Count)
        {
            // ⚠️ 等一轮布局再量：刚加进去的控件还没有实际高度，
            //    当场量 ExtentHeight 会一直是旧值。
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_detailGroupsShown >= _detailGroups.Count)
                {
                    return;
                }
                DetailScroll.UpdateLayout();

                // ⚠️ **视口还没量出来就别继续**：详情页刚建出来那一瞬间（或者面板还没
                //    显示的时候）`ViewportHeight` 是 0，于是"内容 > 视口"恒不成立 →
                //    这里会一路把 **415 组全铺出来**（实测踩过：打开详情页就卡一下，
                //    `--page mods:scroll` 的探针里直接看到"滚前已铺 415 组"）。
                if (DetailScroll.ViewportHeight <= 0)
                {
                    return;
                }

                if (DetailScroll.ExtentHeight <= DetailScroll.ViewportHeight + 1)
                {
                    AppendDetailGroups();     // 还没填满一屏，接着铺
                }
            }));
        }
    }

    /// <summary>一组的控件：标题 + 匹配说明 + 行 + 每行的「下载」。</summary>
    private CollapsibleGroup BuildDetailGroup(ModVersionGroup group)
    {
        var widget = new CollapsibleGroup { Margin = new Thickness(0, 0, 0, 10) };
        widget.SetTitle(ModVersionGroups.Title(group));
        // ⚠️「匹配你选的版本」放在**右边那行副标题**上，不拼进标题：
        //    CollapsibleGroup 会在标题后面挂一个条目数，拼在一起会插进句子中间。
        widget.SetNote(group.MatchesFilter ? Localizer.Tr("匹配你选的版本") : string.Empty);
        widget.SetItems(BuildDetailRows(group.Versions), DpiScale);

        // ⚠️ **不挂 SetAction**：每行右侧那个绿色「下载」按钮 2026-10 按用户要求去掉了，
        //    改成**点这一行就下载**（= "把下载按钮优化成点击标签"）。
        //    `VersionRow` 本来就带 Cursor=Hand + 整行 MouseLeftButtonUp，
        //    所以这里接 `ItemActivated` 就行，一个控件都不用新加。
        //    （下载页那几列版本行不挂 Action、也不接 Activated，行为不受影响。）
        widget.ItemActivated += OnDetailVersionDownload;

        // 对得上的默认展开（用户就是奔着它来的），其余折叠成目录
        widget.SetExpanded(group.MatchesFilter);
        return widget;
    }

    /// <summary>没有分组时的那句话（没有版本 / 拿不到数据）。</summary>
    private void ShowDetailGroupMessage(string text)
    {
        ClearDetailGroups();
        DetailGroups.Children.Add(new TextBlock
        {
            Text = text,
            Style = (Style)FindResource("HintText"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 10),
        });
    }

    /// <summary>加载器 key → 产品名（认不出来就原样返回，别显示成空白）。</summary>
    private static string LoaderTitleOf(string? key)
    {
        var text = (key ?? string.Empty).Trim();
        foreach (var (name, label) in LoaderLabels)
        {
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
            {
                return label;
            }
        }
        return text;
    }

    /// <summary>
    /// 版本行：名字 + （类型 · 发布日期 · 文件名）+ 行尾的绿色「下载」。
    ///
    /// 1.0.0 的 `_VersionRow` 是"名字 + 类型徽章 + 元信息 + 下载按钮"，
    /// 这里把类型并进元信息那一行 —— 复用现成的 VersionRow，
    /// 不为一个徽章再养一个行控件（代价是类型不再是彩色徽章，见 Controls.xaml 的说明）。
    /// </summary>
    private IReadOnlyList<VersionRowItem> BuildDetailRows(IReadOnlyList<JsonObject> versions)
    {
        var icon = RowIconName();
        var rows = new List<VersionRowItem>(versions.Count);

        foreach (var version in versions)
        {
            var id = Str(version, "id", string.Empty);
            if (id.Length == 0)
            {
                continue;
            }
            var name = Str(version, "name", string.Empty);
            if (name.Length == 0)
            {
                name = Str(version, "version_number", id);
            }

            var meta = new List<string> { VersionTypeLabel(Str(version, "version_type", string.Empty)) };
            if (Str(version, "date_published", string.Empty) is { Length: >= 10 } published)
            {
                meta.Add(published[..10]);
            }
            if (FileNameOf(version) is { Length: > 0 } file)
            {
                meta.Add(file);
            }

            rows.Add(new VersionRowItem(id, name, string.Join("    ·    ", meta), icon, version));
        }

        return rows;
    }

    /// <summary>
    /// 详情页里一行版本左边那张内置图。
    ///
    /// · 模组：按加载器（Fabric 的纺锤 / Forge 的铁砧 …）
    /// · 整合包：箱子（`pack`）—— 它自带加载器，标加载器反而误导
    /// · 资源包 / 光影包 / 数据包：**这个类型的兜底方块图**
    ///   （草径 / 红石灯 / 红石块）—— 它们没有加载器这一维，
    ///   不给就成空图标了（`VersionIcons.NameFor("")` 返回空串 → 图是 null）
    /// </summary>
    private string RowIconName()
    {
        if (_type.Target == ModInstallTarget.ModpackInstance)
        {
            return VersionIcons.NameFor(pack: true);
        }
        return _type.UsesLoader
            ? VersionIcons.NameFor(loader: _targetVersion?.LoaderKey ?? string.Empty)
            : _type.FallbackIcon;
    }

    /// <summary>版本文件的名字（`files[0].filename`；没有就返回空串）。</summary>
    private static string FileNameOf(JsonObject version)
        => version["files"] is JsonArray { Count: > 0 } files && files[0] is JsonObject first
            ? Str(first, "filename", string.Empty)
            : string.Empty;

    /// <summary>Modrinth 的 `version_type` → 界面文案（认不出来原样返回）。</summary>
    private static string VersionTypeLabel(string versionType) => (versionType ?? string.Empty).ToLowerInvariant() switch
    {
        "release" => Localizer.Tr("正式版"),
        "beta" => Localizer.Tr("测试版"),
        "alpha" => Localizer.Tr("预览版"),
        var other => other,
    };

    /// <summary>
    /// 详情页底部那行"…下载会落到：{path}"（= 1.0.0 的 #ModPickHint）。
    ///
    /// ⚠️ 模组那两句是 1.0.0 的原文案（5 个语言文件里都查得到），不要自己另写 ——
    ///    "装到哪"这个问题用户问过一次就会问第二次，措辞得跟 1.0.0 对得上。
    ///
    /// ⚠️ 另外几个类型**必须另说**，不能套用模组那两句：
    ///    · 资源包 / 光影包没有"版本分组 + 加载器"这回事，句子里的 `{loader}` 会是「全部」，
    ///      读起来像筛过、其实没筛；
    ///    · 整合包的落点是 `versions/&lt;实例名&gt;/`（独立实例，不是 mods/），
    ///      路径写错比不写更糟 —— 用户会照着那句去 `mods/` 里找。
    /// </summary>
    /// <param name="groupCount">这次铺出来多少**版本分组**（不是多少个版本）。</param>
    /// <param name="pinnedCount">其中有多少组"对得上筛选"（会被置顶展开）。</param>
    private void RefreshDetailHint(int groupCount, int pinnedCount)
    {
        _ = pinnedCount;    // 目前句子里只需要总数；留着参数是为了以后要分开说时不用改签名

        if (App.Services.Games.MinecraftDir.Length == 0)
        {
            // ⚠️ 指向「版本」页而不是「设置」页：游戏目录那张卡已经跟 1.0.0 对齐
            //    从设置页删掉了（见 SettingsPage.xaml 文件头说明），现在只有
            //    「版本」页右侧那一列能改它。指引写着一个不存在的控件比不写还糟。
            DetailHint.Text = Localizer.Tr("还没设置游戏目录 —— 去「版本」页选到 .minecraft 那一层");
            return;
        }

        // ⚠️ 下面这两句里的"你筛选的 …"**必须真的是搜索卡下拉里选的那个**
        //    （以前写的是本地版本，句子却说是"你筛选的" —— 那是两回事）。
        // ⚠️ 选了「全部」的时候也要写「全部」，不能留空：
        //    1.0.0 的原文案是 `version or tr("全部")`，空串会渲染成
        //    "已按你筛选的 1.20.4 +  置顶"（中间两个空格，看着像写坏了）。
        var versionPicked = SelectedValue(GameVersionBox);
        var versionLabel = string.Equals(versionPicked, ModFilters.Any, StringComparison.Ordinal)
            ? Localizer.Tr("全部")
            : versionPicked;

        var loaderPicked = _type.ShowsLoaderFilter ? SelectedValue(LoaderBox) : ModFilters.Any;
        var loaderLabel = string.Equals(loaderPicked, ModFilters.Any, StringComparison.Ordinal)
            ? Localizer.Tr("全部")
            : LoaderTitleOf(loaderPicked);

        // 还没接的类型（数据包）：**别写"下载会落到 …"** —— 根本没有落点，
        // 写了就是编一个路径骗人。（正常走不到这里：下载页那一栏是「还没接」说明页。）
        if (_type.Target == ModInstallTarget.NotImplemented)
        {
            DetailHint.Text = Localizer.Tr("{kind}的下载还没接 —— 数据包要先进某一个存档。",
                ("kind", _type.Label));
            return;
        }

        // 整合包：落点是一个独立实例目录，不是 mods/
        if (_type.Target == ModInstallTarget.ModpackInstance)
        {
            DetailHint.Text = (groupCount == 0
                ? Localizer.Tr("共 {n} 个版本。整合包会装成独立实例：{path}",
                    ("n", 0), ("path", _contentDir))
                : Localizer.Tr("共 {n} 个版本，已按 {version} 挑过。整合包会装成独立实例：{path}",
                    ("n", groupCount), ("version", versionLabel), ("path", _contentDir)))
                + Environment.NewLine
                + Localizer.Tr("整合包自带 mod 和配置，都会落在这个实例目录里（版本隔离），不跟别的版本共用 mods/");
            return;
        }

        // 模组：保持 1.0.0 的原文案（有加载器那一句）
        if (_type.ShowsLoaderFilter)
        {
            DetailHint.Text = groupCount == 0
                ? Localizer.Tr("共 {n} 个版本分组。下载会落到：{path}", ("n", 0), ("path", _contentDir))
                : Localizer.Tr(
                    "共 {n} 个版本分组，已按你筛选的 {version} + {loader} 置顶。下载会落到：{path}",
                    ("n", groupCount),
                    ("version", versionLabel),
                    ("loader", loaderLabel),
                    ("path", _contentDir));
            return;
        }

        // 资源包 / 光影包：只按游戏版本挑，句子里不提加载器
        DetailHint.Text = groupCount == 0
            ? Localizer.Tr("共 {n} 个版本。下载会落到：{path}", ("n", 0), ("path", _contentDir))
            : Localizer.Tr("共 {n} 个版本，已按 {version} 挑过。下载会落到：{path}",
                ("n", groupCount), ("version", versionLabel), ("path", _contentDir));
    }

    /// <summary>详情页某一行点了「下载」：装**这一版**（不是"最新匹配版"）。</summary>
    private async void OnDetailVersionDownload(object? sender, VersionRowItem item)
    {
        if (item.Payload is not JsonObject version)
        {
            return;
        }
        await InstallVersionAsync(version);
    }

    /// <summary>项目 id：`project_id` 优先，退回 `slug`（两个都可能缺，返回空串）。</summary>
    private static string ProjectIdOf(JsonObject hit)
    {
        var id = Str(hit, "project_id", string.Empty);
        return id.Length > 0 ? id : Str(hit, "slug", string.Empty);
    }

    // ---------- 告警条 ----------

    private void ShowNotice(string text)
    {
        NoticeText.Text = text;
        NoticeBanner.Visibility = Visibility.Visible;
    }

    private void HideNotice() => NoticeBanner.Visibility = Visibility.Collapsed;

    /// <summary>
    /// 说一句"出问题了"。⚠️ 详情页和列表页各有一套说事儿的地方：告警条在**列表页**里
    /// （详情页看不见它），详情页底部那行 Hint 才在眼前 —— 按当前在哪一页选一个。
    /// 不这么办的话，用户在详情页点了「下载」、失败信息却写在看不见的那一页上。
    /// </summary>
    private void ReportError(string text)
    {
        if (DetailPane.Visibility == Visibility.Visible)
        {
            DetailHint.Text = text;
            return;
        }
        ShowNotice(text);
    }

    /// <summary>报进度，位置同上（详情页底部那行 / 列表页的状态行）。</summary>
    private void ReportProgress(string text)
    {
        if (DetailPane.Visibility == Visibility.Visible)
        {
            DetailHint.Text = text;
            return;
        }
        StatusText.Text = text;
    }

    // ---------- 杂项 ----------

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
    /// 把 TextBlock 夹到最多 N 行（放不下的第二行用省略号收尾）。
    ///
    /// ⚠️ WPF 的 TextBlock **没有** MaxLines（那是 WinUI 的 API），只给
    ///    `TextTrimming` 的话是"能显示几行就几行"、不会在第二行截断；
    ///    只给 `MaxHeight` 的话行高是猜的，字号/DPI 一变就截半行。
    ///    所以这里**问 TextBlock 自己一行有多高**（`TextBlock.GetLineHeight`，
    ///    它就是字体度量算出来的那个值），再乘 N 设成 MaxHeight ——
    ///    这一步必须在有 FontFamily/FontSize 的样式已经套上之后做。
    /// </summary>
    private static class ModTextLines
    {
        private const int MaxLines = 2;

        /// <summary>夹到两行（1.0.0 的卡片高度只有 96px，简介本来也只放得下两行）。</summary>
        public static void Clamp(TextBlock text) => Clamp(text, MaxLines);

        public static void Clamp(TextBlock text, int maxLines)
        {
            var lineHeight = TextBlock.GetLineHeight(text);
            if (double.IsNaN(lineHeight) || lineHeight <= 0)
            {
                // 度量拿不到就退回"字号 × 1.35"这个常见经验值，总比不夹强
                lineHeight = (text.FontSize > 0 ? text.FontSize : 12) * 1.35;
            }
            // 多给一点点余量：MaxHeight 正好等于 2×行高时，第二行的降部
            // （g/y/p 的下沿）有概率被切掉半个像素。
            text.MaxHeight = lineHeight * maxLines + 1;

            // 第二行放不下就省略号
            text.TextTrimming = TextTrimming.CharacterEllipsis;
        }
    }
}
