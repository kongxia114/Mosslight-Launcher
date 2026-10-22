using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mosslight.App.Infrastructure;
using Mosslight.Core.Localization;
using Mosslight.Core.Services;
using Mosslight.Core.Services.Engine;

namespace Mosslight.App.Views;

/// <summary>设计说明见 docs/design-notes.md#mosslight-app-views-modspage-xaml-cs</summary>
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
    /// 正在往搜索卡那三个下拉里铺候选。铺的过程会触发 `SelectionChanged`。
    ///
    /// ⚠️ 不拦住它的话，"页面一构造就铺表"会被当成"用户改了筛选"：轻则一进来
    ///    就自己搜一把，重则把刚预选好的那一项（版本 / 来源 / 类型）覆盖掉 ——
    ///    那种 bug 看着像"默认值没生效"，查起来要人命。
    /// </summary>
    private bool _fillingFilters;

    /// <summary>
    /// 用户有没有**亲手**选过「版本」那个下拉。
    ///
    /// ⚠️ 不能拿"当前选中值"来判断这件事：进页面时预选的就是当前档案的版本，
    ///    跟"用户自己挑的"是同一个值、分不开。所以这里单独记一笔 ——
    ///    接口回来重铺候选表时（<see cref="RefreshGameVersionsAsync"/>）只在这个标志是
    ///    false 时才替用户默认选中「当前档案」的原版号，绝不覆盖他手动挑的那个。
    /// </summary>

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
    /// 问接口要候选（版本 / 类型）的那个任务。
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

    /// <param name="warm">
    /// 预热用：只把控件树建好，**不自动搜第一屏**。下载页会在空闲时先把其余几栏预热，
    /// 用户真正切过去时再调 <see cref="EnsureFirstSearch"/>（切栏就不再等建页）。
    /// </param>
    public ModsPage(ModContentType type, bool warm = false)
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

        // 「版本」输入框的占位提示（2026-10 用户要求：框里默认空着，提示"输入版本号 (如 1.20.1)"）。
        // ⚠️ WPF 的 ComboBox 没有 Placeholder，所以是**叠了一层 TextBlock**（跟搜索框一个做法）；
        //    这里用 AddHandler 接内部 TextBox 冒泡上来的 TextChanged —— ComboBox 自己没有这个事件。
        VersionHint.Text = Localizer.Tr("输入版本号 (如 1.20.1)");
        GameVersionBox.ToolTip = VersionHint.Text;
        GameVersionBox.AddHandler(
            TextBox.TextChangedEvent,
            new TextChangedEventHandler((_, _) => UpdateVersionHint()));
        GameVersionBox.GotKeyboardFocus += (_, _) => UpdateVersionHint();
        GameVersionBox.LostKeyboardFocus += (_, _) => UpdateVersionHint();
        UpdateVersionHint();

        ResolveTargetVersion();

        // ⚠️ 必须在 ResolveTargetVersion 之后：那一步要先把"装到哪个版本上"定下来，
        //    「版本」那个下拉的默认值（= 那个版本的游戏版本）才知道该预选什么。
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
        if (!warm)
        {
            EnsureFirstSearch();
        }

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

        // `mods:expand`：打开第一个结果的详情，并**展开第一组** ——
        // 「前置资源」在折叠组内部，折叠状态下截图里看不见（跟 pill / beta 那几个子段一个思路）。
        if (string.Equals(key, "expand", StringComparison.Ordinal))
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background, new Action(() => _ = ProbeExpandFirstGroupAsync()));
            return;
        }
        // `mods:kbd`：只打诊断（焦点框 / Tab 可达性），不切页面、不改数据
        if (string.Equals(key, "kbd", StringComparison.Ordinal) && _searchTask is not null)        {
            _ = ProbeAfterSearchAsync();
            return;
        }

        // `mods:pills` / `mods:pill3`：等详情页把版本拉回来，点一颗大版本胶囊，验筛选链路
        // ⚠️ 同样要排到 `Background` 之后：不排的话这一句跑在"自动搜索还没开跑"的时候，
        //    `_searchTask` 还是 null → 详情页根本不会打开 → 一颗胶囊都没有
        //    （2026-10 实测：日志里就是「胶囊 0 颗」）。
        if (key.StartsWith("pill", StringComparison.Ordinal))
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => _ = ProbeMajorPillAsync(ParsePillIndex(key))));
            return;
        }

        // `mods:beta`：把「显示预览版/Beta」打开再截图（验开关的"开"态）
        if (string.Equals(key, "beta", StringComparison.Ordinal))
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => _ = ProbeShowPrereleaseAsync()));
            return;
        }

        // `mods:options`：只打诊断（三个下拉里到底铺了什么），不切页面、不改数据
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

    /// <summary>数据包的存档目录（跟 mods 同一个版本隔离规则）。</summary>
    private string DatapackSavesDir => ModsDirectory.SavesDirFor(
        App.Services.Games.MinecraftDir,
        _targetVersion?.MinecraftVersion ?? string.Empty,
        _targetVersion?.LoaderKey ?? string.Empty,
        _targetVersion);

    /// <summary>用户选的目录 → 真正的 datapacks 目录（挑的是存档根目录就补一段）。</summary>
    private static string AsDatapacksDir(string dir)
    {
        var trimmed = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0)
        {
            return dir;
        }
        var name = Path.GetFileName(trimmed);
        return string.Equals(name, "datapacks", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : Path.Combine(trimmed, "datapacks");
    }

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

        // 数据包：候选是"每个存档的 datapacks 目录"（游戏只读这些地方）
        if (_type.Target == ModInstallTarget.WorldDatapacks)
        {
            foreach (var world in ModsDirectory.ListWorlds(DatapackSavesDir))
            {
                options.Add((world, Path.Combine(DatapackSavesDir, world, "datapacks")));
            }
            return options;
        }

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
        return _type.Target == ModInstallTarget.WorldDatapacks
            ? new InstallTargetDialog(options, _contentDir,
                fieldLabel: Localizer.Tr("装到哪个存档"),
                hint: Localizer.Tr("选一个存档，或者自己挑一个文件夹。数据包会放进它的 datapacks/ 目录。"))
            : new InstallTargetDialog(options, _contentDir);
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
            ReportError(_type.Target == ModInstallTarget.WorldDatapacks
                ? Localizer.Tr("还没有存档 —— 先去游戏里建一个，再回来装数据包。")
                : Localizer.Tr("还没有本地版本 —— 先去「下载」页装一个，再回来装{kind}。",
                    ("kind", _type.Label)));
            return null;
        }

        var owner = Window.GetWindow(this);
        if (owner is not null && owner.IsLoaded)
        {
            dialog.Owner = owner;
        }

        if (dialog.ShowDialog() != true || dialog.ChosenDir.Length == 0)
        {
            return null;
        }

        // 数据包：用户可能挑的是存档根目录，也可能挑的就是 datapacks/
        return _type.Target == ModInstallTarget.WorldDatapacks
            ? AsDatapacksDir(dialog.ChosenDir)
            : dialog.ChosenDir;
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

    /// <summary>三个下拉当前选中的接口值 + 名称（诊断用）。</summary>
    private string DescribeFilters()
        => $"名称=「{QueryBox.Text}」 来源={SelectedValue(SourceBox)}"
            + $" 版本={GameVersionFilter}"
            + $" 类型={(CategoryBox.Visibility == Visibility.Visible ? SelectedValue(CategoryBox) : "<收起>")}"
            + $" 排序={SelectedValue(SortBox)}";

    /// <summary>
    /// `--page mods:options`：把搜索卡那三个下拉的**实际候选**打到标准输出。
    ///
    /// 跟 <see cref="ProbeListKeyboard"/> 一个理由：下拉是"点开才看得见"的东西，
    /// 离屏截图拍不到 —— 而"候选铺了几项、用的是接口回来的数据还是兜底表、
    /// 没有「类型」这一维的栏有没有把整格收起来"正是这次改动最需要证明的地方。
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
        DumpCombo("来源", SourceBox);
        DumpCombo("版本", GameVersionBox);
        DumpCombo("类型", CategoryBox);
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
    {        if (_searchTask is not null)
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
    /// `--page mods:pill`（`pill3` = 第 3 颗）：等详情页把版本拉回来，点一颗大版本胶囊。
    ///
    /// 跟 `mods:filters` 一个理由：胶囊是"点出来的"，离屏截图点不了鼠标 ——
    /// 没有这个入口，"筛完到底还剩几组"就只能靠人肉开程序点一遍。
    /// 对真人没有影响：不传这个子段时这就是个没人调的方法。
    /// </summary>
    private async Task ProbeMajorPillAsync(int index)
    {
        await WaitForDetailGroupsAsync();

        var pills = MajorVersionPills.Children.OfType<ToggleButton>().ToList();
        Console.WriteLine($"[major-pill] 通道：预览版"
            + $"{(ShowPrereleaseBox.IsChecked == true ? "开" : "关")}，"
            + $"分组 {_detailGroups.Count} → 通道内 {ChannelGroups().Count}；"
            + $"胶囊 {pills.Count} 颗：" + string.Join(" | ", pills.Select(pill => pill.Content)));
        if (index < 0 || index >= pills.Count)
        {
            return;
        }

        pills[index].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Console.WriteLine($"[major-pill] 点了「{pills[index].Content}」："
            + $"可见 {_visibleGroups.Count} / 共 {_detailGroups.Count} 组，"
            + $"铺出 {DetailGroups.Children.Count} 个分组控件");
    }

    /// <summary>`pill` / `pill3` → 点第几颗（默认第 1 颗，也就是「全部」后面那个大版本）。</summary>
    private static int ParsePillIndex(string key)
        => key.Length > 4 && int.TryParse(key[4..], out var index) ? index : 1;

    /// <summary>
    /// `--page mods:beta`：把「显示预览版/Beta」打开，验开关的"开"态。
    ///
    /// ⚠️ 置 `IsChecked` 会**真的**走 `OnChannelChanged`（跟鼠标点一样），所以截出来的
    ///    就是用户点开之后的样子，不是硬把数据改一遍。
    /// </summary>
    private async Task ProbeShowPrereleaseAsync()
    {
        await WaitForDetailGroupsAsync();
        ShowPrereleaseBox.IsChecked = true;
        Console.WriteLine($"[major-pill] 打开「显示预览版/Beta」后："
            + $"分组 {_detailGroups.Count} → 通道内 {ChannelGroups().Count}，"
            + $"可见 {_visibleGroups.Count}，铺出 {DetailGroups.Children.Count}");
    }

    /// <summary>`--page mods:expand`：展开第一组，好让「前置资源」在截图里可见。</summary>
    private async Task ProbeExpandFirstGroupAsync()
    {
        await WaitForDetailGroupsAsync();
        // 等前置项目拉回来：拉完会重铺一次分组，展开太早会被重铺冲掉（<see cref="FetchDependencyProjectsAsync"/>）
        await Task.Delay(3000);
        if (DetailGroups.Children.OfType<CollapsibleGroup>().FirstOrDefault() is { } first)
        {
            first.SetExpanded(true);
            Console.WriteLine($"[deps] 展开第一组；已缓存前置项目 {_depProjects.Count} 个");
        }
    }

    /// <summary>等详情页把版本拉回来、胶囊铺出来（最多 6 秒；够用就立刻往下走）。</summary>
    private async Task WaitForDetailGroupsAsync()
    {
        await OpenFirstResultDetailAsync();
        for (var i = 0; i < 60 && MajorVersionPills.Children.Count <= 1; i++)
        {
            await Task.Delay(100);
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

        if (_type.Target == ModInstallTarget.WorldDatapacks)
        {
            // 数据包没有固定落点：这里只摆一个占位给底部提示/对话框预选用。
            // ⚠️ 这里**不扫盘**（建页可能发生在界面线程的空闲预热里）；真正列存档是
            //    点「下载」那一刻的事（`BuildTargetOptions`）。
            _contentDir = Path.Combine(
                DatapackSavesDir, "<" + Localizer.Tr("存档") + ">", "datapacks");
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
    /// 用户本地这个版本装的是哪家加载器 —— 口径是"**我装了什么**"，不是"这次搜什么"。
    ///
    /// ⚠️ 2026-10 起搜索卡上**没有**加载器下拉了（照 PCL 精简成 名称 / 版本 / 来源 / 类型），
    ///    所以搜索本身不再按加载器筛。这个值仍然要用在两处：详情页版本分组"置顶哪一组"、
    ///    以及"下载会落到哪个目录"（版本隔离）。
    /// </summary>
    private string TargetLoader => _type.UsesLoader
        ? _targetVersion?.LoaderKey ?? ModFilters.Any
        : ModFilters.Any;

    /// <summary>
    /// 这一次搜索按哪个游戏版本筛 = 「版本」里选的那一项，或者**用户手敲的版本号**。
    ///
    /// ⚠️ 「版本」是**可输入**的下拉：手敲 `1.21.1` 时 `SelectedItem` 还停在上一项 ——
    ///    只读它等于把用户的输入吞掉。所以先看"框里的文本是不是正好就是选中那一项的文案"，
    ///    不是就按文本当版本号。
    ///
    /// ⚠️ **空文本 / 「全部」/ 「全部（也可自行输入）」都算"不按版本筛"**（返 <see cref="ModFilters.Any"/>）：
    ///    框里默认就是空的（2026-10 用户要求），不清掉这条判断的话，空框会被当成
    ///    一个叫 "" 的版本号发出去，搜索结果会莫名其妙变空。
    /// </summary>
    private string GameVersionFilter
    {
        get
        {
            var typed = GameVersionBox.Text?.Trim() ?? string.Empty;
            if (typed.Length == 0
                || string.Equals(typed, ModFilters.Any, StringComparison.OrdinalIgnoreCase)
                || string.Equals(typed, Localizer.Tr(ModFilters.Any), StringComparison.OrdinalIgnoreCase)
                || string.Equals(typed, "全部（也可自行输入）", StringComparison.OrdinalIgnoreCase))
            {
                return ModFilters.Any;
            }

            if (GameVersionBox.SelectedItem is ComboBoxItem picked
                && string.Equals(typed, picked.Content as string, StringComparison.Ordinal))
            {
                return picked.Tag as string ?? ModFilters.Any;
            }
            return typed;
        }
    }

    /// <summary>版本输入框里为空时显示占位提示，有字就藏起来（跟搜索框的 <c>UpdateQueryHint</c> 一个做法）。</summary>
    private void UpdateVersionHint()
        => VersionHint.Visibility = string.IsNullOrWhiteSpace(GameVersionBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

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

    /// <summary>
    /// 这一次搜索按哪个来源 = 用户在「来源」下拉里选的那个。
    ///
    /// **A 方案：只有「全部 / Modrinth」两档**（候选表在 <see cref="ModFilters.Sources"/>）。
    /// 「全部」的意思是"不限来源"，现在只有 Modrinth 一家，所以两者结果一样 ——
    /// 差别要等第二家来源接进来才看得出来，见 `RealModService.SearchAsync` 那句注释。
    /// </summary>
    private string SourceFilter => SelectedValue(SourceBox);

    // ---------- 筛选（搜索卡上那三个下拉） ----------

    /// <summary>
    /// 铺搜索卡那三个下拉（来源 / 版本 / 类型），并把默认值选好。
    ///
    /// 默认值：版本 = **当前这个版本要的 MC 版本**（点进这一栏九成是想给它找东西），
    /// 来源 / 类型 = 「全部」。三个下拉都是明摆着的，用户随时能改。
    ///
    /// ⚠️ 2026-10 起照 PCL 精简成「名称 / 版本 / 来源 / 类型」四个字段：
    ///    原来的「当前版本」快捷筛选和「加载器」下拉都去掉了。
    /// </summary>
    private void InitFilters()
    {
        // 卡片标题跟着类型走（左栏换一栏之后，"现在搜的是哪一类"只能从标题看出来）。
        SearchCardTitle.Text = Localizer.Tr("搜索{kind}", ("kind", _type.Label));
        // 按钮文案是固定的「搜索 / 重置条件」（照 PCL），不跟类型变。
        SearchButton.Content = Localizer.Tr("搜索");

        // 来源：**A 方案只有「全部 / Modrinth」**，默认「全部」（= 不限制来源）。
        FillCombo(SourceBox,
            ModFilters.Sources.Select(o => (Value: o.Value, Label: OptionLabel(o))),
            ModFilters.Any);

        // 版本：默认选中「全部」= 不按版本筛（2026-10 用户确认："默认是显示全部版本"）。
        // 「版本」仍然是**可输入**的：想按版本搜就直接点进去敲 1.20.1，敲完按回车或按搜索；
        // 把文字清空之后框里会显示占位提示（VersionHint）。空 / 「全部」都等于不筛（见 GameVersionFilter）。
        FillCombo(GameVersionBox,
            ModFilters.FallbackVersions.Select(v => (Value: v, Label: v)),
            ModFilters.Any);

        // 类型（= Modrinth 的分类）：数据包 / 光影包一个分类都没有（tag 表里就是空的），
        // 这两栏整格收起，让「版本」跨满整行。
        var categories = ModFilters.CategoriesFor(_type.ProjectType);
        if (categories.Count > 0)
        {
            FillCombo(CategoryBox,
                categories.Select(o => (Value: o.Value, Label: OptionLabel(o))),
                ModFilters.Any);
        }
        else
        {
            HideCategoryCell();
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
        bool includeAny = true,
        string? anyLabel = null)
    {
        _fillingFilters = true;
        try
        {
            box.Items.Clear();
            if (includeAny)
            {
                // 第一项永远是「全部」（= 不给 facets 加条件）。这个字符串要过
                // Localizer：模块级写死中文的话，切语言之后它不会跟着变。
                // `anyLabel` 给「版本」那种"还能手敲"的下拉用（照 PCL 的「全部（也可自行输入）」）。
                box.Items.Add(new ComboBoxItem
                {
                    Content = anyLabel ?? Localizer.Tr(ModFilters.Any),
                    Tag = ModFilters.Any,
                });
            }
            foreach (var (value, label) in options)
            {
                box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
            }
            SelectValue(box, prefer);
            // ⚠️ **可输入**的下拉（只有「版本」）不会把 `SelectedItem` 自动同步到 `Text`：
            //    不补这一句，框里就是空的 —— 选中的版本明明是对的，看上去却像没选。
            if (box.IsEditable)
            {
                box.Text = (box.SelectedItem as ComboBoxItem)?.Content as string ?? string.Empty;
            }
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
        if (TrySelectExisting(box, wanted))
        {
            return;
        }

        var added = new ComboBoxItem { Content = wanted, Tag = wanted };
        box.Items.Insert(0, added);
        box.SelectedItem = added;
    }

    /// <summary>
    /// 表里**有**这一项才选中它（返回选没选中）。
    ///
    /// 跟 <see cref="SelectValue"/> 只差一条：这个**不往表里塞东西** ——
    /// 给"默认值"用（见 <see cref="RefreshGameVersionsAsync"/>），不能因为一个默认值
    /// 就把接口没给的版本项塞进候选表里（那正是被删掉的那个哨兵值）。
    /// </summary>
    private static bool TrySelectExisting(ComboBox box, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }
        foreach (var item in box.Items)
        {
            if (item is ComboBoxItem option
                && string.Equals(option.Tag as string, value, StringComparison.Ordinal))
            {
                box.SelectedItem = option;
                return true;
            }
        }
        return false;
    }

    /// <summary>一个下拉当前选中的接口值（一项都没选的话是「全部」）。</summary>
    private static string SelectedValue(ComboBox box)
        => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? ModFilters.Any;

    /// <summary>
    /// 把「类型」那一格（标签 + 下拉）从布局里摘掉，让「版本」跨满整行。
    ///
    /// 用于"没有分类这一维"的栏（数据包 / 光影包）：留着两个空格子会让第二行看着像排坏了，
    /// 挪成整行才是 PCL 那种"这一栏没有这个条件"的样子。
    ///
    /// ⚠️ **不要按列号去把列宽归零**：2026-10 的表单改过两次列数（8 列 → 2 列 → 4 列），
    ///    拿旧列号索引 `ColumnDefinitions` 会直接越界、崩在页面构造期
    ///    （资源包 / 光影包 / 数据包 这三栏都走这条分支）。
    ///    这两格在页面上只出现一次，摘掉不用复原。
    /// </summary>
    private void HideCategoryCell()
    {
        CategoryLabel.Visibility = Visibility.Collapsed;
        CategoryBox.Visibility = Visibility.Collapsed;
        // 「版本」在第 1 列，跨 3 列 = 一直铺到右边（第 2、3 列本来是标签和类型下拉的位置）。
        // ⚠️ 跨的是**外层的 Grid**（GameVersionCell）：占位提示跟输入框一起走，
        //    只给 ComboBox 跨列的话，提示那层还留在第 1 列，宽度会缩一半。
        Grid.SetColumnSpan(GameVersionCell, 3);
    }

    /// <summary>当前这个版本要的 MC 版本（没装版本时「全部」）—— 「版本」下拉的预选值。</summary>
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
    /// 「重置条件」：回到**刚进这一页时**的样子。
    ///
    /// 重置 = 名称清空 → 版本清空（框里空着、显示占位提示）→ 类型 / 来源回「全部」→ 重搜热门。
    /// ⚠️ 2026-10 起「版本」的默认值就是**空**（用户要求：不默认筛选版本），所以这里也是清空 ——
    ///    别再写回 <see cref="TargetMcVersion"/>，否则"重置"会凭空给一个版本条件。
    /// </summary>
    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        _fillingFilters = true;
        try
        {
            QueryBox.Text = string.Empty;
            // 版本回「全部」（2026-10 用户确认的默认值）；不再写回当前档案的版本
            SelectValue(GameVersionBox, ModFilters.Any);
            if (ModFilters.CategoriesFor(_type.ProjectType).Count > 0)
            {
                SelectValue(CategoryBox, ModFilters.Any);
            }
            SelectValue(SourceBox, ModFilters.Any);
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
    /// 把版本 / 类型的候选换成接口给的**真实数据**（兜底表会慢慢变旧：
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
            // ⚠️ 选中的值要在**铺之前**记下来：接口回来这半秒里用户很可能已经改过了，
            //    铺完不还回去就等于把他的输入悄悄改成「第一个」。
            // ⚠️ 用 `GameVersionFilter` 而不是 `SelectedValue`：手敲的版本号也要留住
            //    （SelectedItem 停在上一次选的那项，只看它会把输入丢掉）。
            var keep = GameVersionFilter;
            FillCombo(GameVersionBox, versions.Select(v => (Value: v, Label: v)), keep);

            // 铺完不用再动：`keep` 是「全部」时 <see cref="FillCombo"/> 已经把「全部」选好并写进框里
            // （2026-10 用户确认的默认值就是"全部版本"）。
            // ⚠️ 2026-10 起**不再**自动把当前档案的原版号选上 —— 以前那段
            //    `TrySelectExisting(vanilla)` 已删，默认不再替用户选一个版本。
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

    /// <summary>设计说明见 docs/design-notes.md#mosslight-app-views-modspage-xaml-cs</summary>
    /// <summary>
    /// 「版本」那个可输入下拉的键盘处理。
    ///
    /// ⚠️ 只有下拉的 `SelectionChanged` 不够用了：手敲版本号时选中项没变、那个事件不会来。
    ///    Enter = "这个版本号我输完了"（照 PCL：敲完回车就搜）。
    /// ⚠️ 2026-10 起**不再**记"用户亲手选过"那笔账：接口回来重铺候选表时一律按
    ///    `GameVersionFilter` 原样还回去，已经不靠那个标志判断默认值了。
    /// </summary>
    private void OnVersionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RequestSearch();
        }
    }

    /// <summary>
    /// 当前档案的真实原版号（优先读它的 mosslight.ini）。
    /// 任何一步不满足都返回空串 —— 调用方据此决定不追加这一条，绝不抛。
    /// </summary>
    private static string CurrentVersionVanilla()
    {
        try
        {
            var mcDir = App.Settings?.MinecraftDir;
            var name = App.Settings?.LastVersion;
            if (string.IsNullOrWhiteSpace(mcDir) || string.IsNullOrWhiteSpace(name)) return string.Empty;
            var dir = Path.Combine(mcDir, "versions", name);
            if (!Directory.Exists(dir)) return string.Empty;
            var meta = VersionMetaCache.Read(dir);
            if (meta?.VanillaVersion is { Length: > 0 } vanilla) return vanilla;
            return VersionScanner.ScanVersions(mcDir)
                .FirstOrDefault(v => v.DirName == name || v.Id == name)?.MinecraftVersion ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

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

        var typed = QueryBox.Text.Trim();

        // ⚠️ Modrinth 的搜索只匹配**英文**标题/摘要（它索引里没有中文名），所以中文关键词
        //    直接发过去必然空（2026-10 用户报的"有些模组中文搜不出来"）。
        //    这里先过一遍内置别名表（`ModSearchAliases`）；表里没有的中文词**照原样搜**，
        //    但用告警条把原因说清楚，别让人以为启动器坏了。
        var query = typed;
        if (typed.Length > 0 && ModSearchAliases.TryTranslate(typed, out var english))
        {
            query = english;
            ShowNotice(Localizer.Tr(
                "「{zh}」按英文名 {en} 搜索（Modrinth 只索引英文）。", ("zh", typed), ("en", english)));
        }
        else if (typed.Length > 0 && ModSearchAliases.ContainsCjk(typed))
        {
            ShowNotice(Localizer.Tr(
                "Modrinth 只索引英文名，中文关键词可能搜不到 —— 换个英文名试试。"));
        }

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
            // 搜索条件全都**由用户在下拉里定**（默认值是当前这个版本，见 InitFilters）：
            // 版本 / 类型 / 来源 / 排序（排序控件是隐藏的，值一直是默认的「相关度」）。
            // ⚠️ 加载器**不在条件里**（2026-10 照 PCL 去掉那个下拉）：Modrinth 那边
            //    不传 `categories:加载器` 就是不限加载器 —— 资源包 / 光影包那一维本来也没有。
            // ⚠️ 这套条件要**记下来**：滑到底续取下一页时得原样再用一次
            //    （不能到时候重读下拉 —— 用户可能已经改了但还没按搜索）。
            _resultQuery = query;
            _resultGameVersion = GameVersionFilter;
            _resultCategory = CategoryFilter;
            _resultSort = SelectedSort;
            _resultSource = SourceFilter;

            var data = await App.Services.Mods.SearchAsync(
                query: _resultQuery,
                gameVersion: _resultGameVersion,
                category: _resultCategory,
                sort: _resultSort,
                limit: ResultPageSize,
                offset: 0,
                projectType: _type.ProjectType,
                source: _resultSource);

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
    private string _resultCategory = ModFilters.Any;
    private string _resultSort = ModFilters.DefaultSort;

    /// <summary>这一次搜索的来源（「全部」/ `modrinth`，候选表见 <see cref="ModFilters.Sources"/>）。</summary>
    private string _resultSource = ModFilters.Any;

    /// <summary>每页取多少条（= 1.0.0 的 `PAGE_SIZE`）。</summary>
    private const int ResultPageSize = 20;

    /// <summary>正在取下一页（挡住 ScrollChanged 连发导致的重复请求）。</summary>
    private bool _loadingMore;

    /// <summary>设计说明见 docs/design-notes.md#mosslight-app-views-modspage-xaml-cs</summary>
    private async void OnResultsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 设计说明见 docs/design-notes.md#mosslight-app-views-modspage-xaml-cs
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
            // ⚠️ 条件要用**这一次搜索时定的那套**（`_resultQuery` + 三个下拉），
            //    不能重新读输入框：用户可能已经改了关键词还没按搜索 ——
            //    那样取回来的会是另一组结果，跟列表里已有的混在一起。
            var data = await App.Services.Mods.SearchAsync(
                query: _resultQuery,
                gameVersion: _resultGameVersion,
                category: _resultCategory,
                sort: _resultSort,
                limit: ResultPageSize,
                offset: _resultOffset,
                projectType: _type.ProjectType,
                source: _resultSource);

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

        // 设计说明见 docs/design-notes.md#mosslight-app-views-modspage-xaml-cs
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

    /// <summary>设计说明见 docs/design-notes.md#mosslight-app-views-modspage-xaml-cs</summary>
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

    // 安装只有一条路：详情页点某一行 → `InstallVersionAsync`。

    /// <summary>
    /// 真装某一版。进度和失败原因都写到**当前看得见的那一处**（详情页底部那行 /
    /// 列表页的状态行 + 告警条），见 <see cref="ReportProgress"/>。
    ///
    /// 三条路，按 <see cref="ModContentType.Target"/> 分：
    ///   · <see cref="ModInstallTarget.ContentDir"/>（模组 / 资源包 / 光影）
    ///     → `IModService.InstallAsync`，目录由引擎按类型算
    ///   · <see cref="ModInstallTarget.ModpackInstance"/>（整合包）
    ///     → 先把 `.mrpack` 的地址交给 `IModpackService`（它自己下 + 装成独立实例）
    ///   · <see cref="ModInstallTarget.WorldDatapacks"/>（数据包）
    ///     → `IModService.InstallAsync`，落点是用户选的那个存档的 `datapacks/`
    /// </summary>
    private async Task InstallVersionAsync(JsonObject version)
    {
        // 数据包跟"装到哪个版本"无关（落点是某个存档），没装版本也能装它
        if (_targetVersion is null && _type.Target != ModInstallTarget.WorldDatapacks)
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

        // ⚠️ 先问"装到哪儿"（= PCL 那种先选目录再下载），**不要**直接下进
        //    "上次启动的那个版本" —— 那样用户完全没得选。
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
                //    数据包跟"装到哪个版本"无关，这里可能是 null（用 ?. 兜住）。
                _targetVersion?.MinecraftVersion ?? string.Empty,
                _targetVersion?.LoaderKey ?? string.Empty,
                _targetVersion, progress,
                // ⚠️ 类型走**具名参数**传在末尾（接口上那个参数是追加的、默认 "mod"）：
                //    目录由引擎按类型算，这里不再自己拼路径
                projectType: _type.ProjectType,
                // 用户在「装到哪儿？」里选的那个目录
                targetDir: targetDir);

            ReportProgress(result.Message);
            window.Finish(result.Message);
            if (result.Ok)
            {
                // 成功提示走主窗口那个 toast —— 跟设置页「调试 → 成功提示」是**同一套**
                // （用户 2026-10：把那个提示接到下载成功上）。文案对齐 PCL：
                // 「模组下载成功：钠 0.9.3 for NeoForge 26.3」
                MainWindow.Current?.ShowToast(
                    Localizer.Tr("{kind}下载成功：{name}",
                        ("kind", _type.Label),
                        ("name", Str(version, "name", Str(version, "version_number", string.Empty)))),
                    ToastKind.Success);
            }
            else
            {
                ReportError(result.Message);
                MainWindow.Current?.ShowToast(result.Message, ToastKind.Error);
            }
        }
        catch (ModrinthException ex)
        {
            ReportError(ex.Message);
            window.Finish(ex.Message);
            MainWindow.Current?.ShowToast(ex.Message, ToastKind.Error);
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

            // 置顶哪一组 = **用户在「版本」里选的那个**（默认值就是当前档案的版本，见 InitFilters）：
            // 底部那句话说"已按你筛选的 … 置顶"，这里就必须真的是"筛选的那个"。
            // ⚠️ 加载器不再由用户选（搜索卡上没有这一格了），这里用**当前档案装的那家**
            //    来决定置顶哪一组 —— 用户真正要装东西的那一组排在前面就够了。
            // ⚠️ 加载器只有 mod / 整合包有这一维（`ModContentType.ShowsLoaderFilter`）：
            //    资源包 / 光影包的版本把 `loaders` 当"能用在谁身上"（光影包常常同时标
            //    iris + optifine），按它分组会把同一批版本列成两套 —— 所以这两类传 false。
            var groups = App.Services.Mods.GroupVersions(
                versions,
                GameVersionFilter,
                _type.ShowsLoaderFilter ? TargetLoader : ModrinthApi.AnyOption,
                splitByLoader: _type.ShowsLoaderFilter);

            if (groups.Count == 0)
            {
                ShowDetailGroupMessage(Localizer.Tr("这个项目没有可下载的版本"));
                return;
            }

            // 底部那句「共 N 个版本分组」由 ApplyDetailGroups → RenderDetailGroups 写
            // （它要跟着大版本筛选走，这里再写一次就用总数盖掉筛后的数了）。
            ApplyDetailGroups(groups);
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

    private bool _firstSearchStarted;

    /// <summary>
    /// 要搜第一屏了（预热过的页面在被真正显示时调一次）。
    /// 挂在 Dispatcher 的 `Loaded` 优先级：排在布局之后，也给 `--query` 留了先手。
    /// </summary>
    public void EnsureFirstSearch()
    {
        if (_firstSearchStarted)
        {
            return;
        }
        _firstSearchStarted = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(OnFirstLoaded));
    }

    /// <summary>这次详情页的全部分组（排序已经由引擎做好：匹配的在前）。</summary>
    private IReadOnlyList<ModVersionGroup> _detailGroups = Array.Empty<ModVersionGroup>();

    /// <summary>
    /// 前置模组：`project_id` → 项目 JSON（拉不到就存 null，别反复重试）。
    ///
    /// ⚠️ Modrinth 的 `version.dependencies` 只给 **project_id**，没有名字/图标 ——
    ///    要显示「前置资源」就必须再查一次 `/project/{id}`。所以这里按项目缓存：
    ///    一个详情页通常只有 1~3 个前置，拉一次就够，铺几百个分组时不会再发请求。
    /// </summary>
    private readonly Dictionary<string, JsonObject?> _depProjects = new(StringComparer.Ordinal);

    /// <summary>这一次详情页的前置信息是否已经拉过（换项目时重置）。</summary>
    private bool _depFetchStarted;

    /// <summary>按大版本筛过之后，真正要铺的那批分组（「全部」时就是 <see cref="_detailGroups"/>）。</summary>
    private IReadOnlyList<ModVersionGroup> _visibleGroups = Array.Empty<ModVersionGroup>();

    /// <summary>
    /// 顶部胶囊选中的大版本（`1.20` / `26.3` …）；空串 = 「全部」。
    ///
    /// ⚠️ 只筛**已经拉下来的分组**，不重新问接口：点一下不该再等一次网络。
    /// </summary>
    private string _majorFilter = string.Empty;

    /// <summary>
    /// 「显示预览版/Beta」开着没有（**默认关**）。
    ///
    /// ⚠️ 关着时连 `-pre` / `-rc` / 快照一起滤掉，不只是 `version_type=beta` 那批 ——
    ///    判据见 <see cref="IsPrereleaseGroup"/>。
    /// </summary>
    private bool _showPrerelease;

    /// <summary>已经铺出来几组（滚到底会往后加）。</summary>
    private int _detailGroupsShown;

    /// <summary>每次滚到底追加几组。太小要翻很多次，太大会卡住一瞬。</summary>
    private const int DetailGroupBatch = 12;

    /// <summary>
    /// 详情页滚到**快见底**就续铺下一批分组。
    ///
    /// ⚠️ 阈值留提前量（160）：等真滚到最底才加载，用户会看到"卡在最后一行等一下"。
    /// ⚠️ 只认**竖向**变化：内容变化（增删行）也会触发 ScrollChanged，
    ///    不判一下就会在铺行过程中反复进来。
    /// </summary>
    private void OnDetailScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ScrollLoader.ShouldLoadMore(e.VerticalOffset, e.ViewportHeight, e.ExtentHeight))
        {
            AppendDetailGroups();
        }
    }

    /// <summary>换一个项目 → 清空重来（顶部的大版本筛选也回「全部」）。</summary>
    private void ApplyDetailGroups(IReadOnlyList<ModVersionGroup> groups)
    {
        _detailGroups = groups;
        _majorFilter = string.Empty;      // 进详情页默认「全部」（照 PCL）

        // 前置模组的缓存跟着项目走：换项目必须清，否则会把上一个项目的前置挂到新项目上
        _depProjects.Clear();
        _depFetchStarted = false;
        _ = FetchDependencyProjectsAsync();

        RebuildMajorPills();
        RenderDetailGroups();
    }

    /// <summary>
    /// 按当前选中的大版本，把分组**从头**铺一遍。
    ///
    /// ⚠️ 筛选必须重置分批游标（`_detailGroupsShown`）：不清的话续铺会从"第 12 组"接着来，
    ///    筛出 3 组时等于一组都不显示。
    /// </summary>
    private void RenderDetailGroups()
    {
        // 先按「显示预览版/Beta」过一遍通道，再按大版本胶囊过一遍 —— 两个条件是与的关系。
        var channel = ChannelGroups();
        _visibleGroups = _majorFilter.Length == 0
            ? channel
            : channel
                .Where(g => string.Equals(MajorOf(g.GameVersion), _majorFilter, StringComparison.Ordinal))
                .ToList();

        _detailGroupsShown = 0;
        ClearDetailGroups();
        AppendDetailGroups();

        // 底部那句「共 N 个版本分组」要跟着**筛后**的数量走，
        // 不然选完 1.20 还写着总共几百组，跟眼前看到的对不上。
        RefreshDetailHint(_visibleGroups.Count, _visibleGroups.Count(g => g.MatchesFilter));
    }

    /// <summary>
    /// 按**当前拉到的分组**重铺顶部那排大版本胶囊：`全部` + 去重倒序的大版本号。
    ///
    /// ⚠️ 候选是从分组里现算的，不是写死的表：这个项目到底支持哪些版本，只有接口知道。
    /// ⚠️ 只分得出**一个**大版本时整排收起来 —— 只有「全部 / 1.20」的筛选栏是摆设，
    ///    还白占一条高度。
    /// </summary>
    private void RebuildMajorPills()
    {
        MajorVersionPills.Children.Clear();

        var majors = ChannelGroups()
            .Select(group => MajorOf(group.GameVersion))
            .Where(major => major.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(MajorRank)
            .ToList();

        // ⚠️ 关掉预览版之后，当前选中的大版本可能整个消失了（那一档只有 beta）——
        //    这时要退回「全部」，否则屏幕上是一排没有选中项的胶囊 + 一个空列表。
        if (_majorFilter.Length > 0 && !majors.Contains(_majorFilter, StringComparer.Ordinal))
        {
            _majorFilter = string.Empty;
        }

        MajorVersionScroll.Visibility = majors.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        if (majors.Count < 2)
        {
            return;
        }

        AddMajorPill(Localizer.Tr(ModFilters.Any), string.Empty);
        foreach (var major in majors)
        {
            AddMajorPill(major, major);
        }
    }

    /// <summary>当前的「版本通道」：关掉预览版时只留正式版。</summary>
    private IReadOnlyList<ModVersionGroup> ChannelGroups()
        => _showPrerelease
            ? _detailGroups
            : _detailGroups.Where(group => !IsPrereleaseGroup(group)).ToList();

    /// <summary>快照版本号的形状：`26w14a` / `24w14a`（年份 + w + 周 + 小写字母）。</summary>
    private static readonly Regex SnapshotVersion = new(
        @"^\d{2,3}w\d{2}[a-z]$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 这一组算不算"预览通道"（beta / alpha / pre / rc / 快照）。
    ///
    /// ⚠️ 两道判据都得看：
    ///    · <see cref="ModVersionGroup.Prerelease"/> —— Modrinth 的 `version_type` 是
    ///      beta / alpha（**文件自己声明的**）；
    ///    · 游戏版本号本身 —— `1.20.5-pre1`、`1.21-rc1`、`26.4-snapshot-5`、`26w14a`。
    ///      这几类的 `version_type` 有时仍然写 release，只信第一道会漏。
    /// </summary>
    private static bool IsPrereleaseGroup(ModVersionGroup group)
    {
        if (group.Prerelease)
        {
            return true;
        }
        var game = group.GameVersion ?? string.Empty;
        return game.Contains("pre", StringComparison.OrdinalIgnoreCase)
            || game.Contains("rc", StringComparison.OrdinalIgnoreCase)
            || game.Contains("snapshot", StringComparison.OrdinalIgnoreCase)
            || SnapshotVersion.IsMatch(game);
    }

    /// <summary>
    /// 切「显示预览版/Beta」。
    ///
    /// ⚠️ 只重铺**已经拉下来的**分组，不重新问接口（切个开关不该再等一次网络）；
    /// ⚠️ 胶囊候选也要跟着重算：某个大版本如果只剩预览版，关掉开关后它就不该还挂着
    ///    （点进去一组都没有）。
    /// </summary>
    private void OnChannelChanged(object sender, RoutedEventArgs e)
    {
        _showPrerelease = ShowPrereleaseBox.IsChecked == true;
        if (_detailGroups.Count == 0)
        {
            return;     // 版本还没回来：等 ApplyDetailGroups 那一步按当前开关铺
        }
        RebuildMajorPills();
        RenderDetailGroups();
        DetailScroll.ScrollToTop();
    }

    /// <summary>铺一颗胶囊（`value` 空串 = 「全部」）。</summary>
    private void AddMajorPill(string label, string value)
    {
        var pill = new ToggleButton
        {
            Style = (Style)FindResource("VersionPill"),
            Content = label,
            Tag = value,
            IsChecked = string.Equals(value, _majorFilter, StringComparison.Ordinal),
        };
        pill.Click += OnMajorPillClick;
        MajorVersionPills.Children.Add(pill);
    }

    /// <summary>
    /// 点了某个大版本。
    ///
    /// ⚠️ 这些是 `ToggleButton`（要有"按下"的视觉状态），但语义上是**单选** ——
    ///    点一下 WPF 会自己把它翻面，所以这里得手动把其余的压回去，否则会同时亮两个。
    /// </summary>
    private void OnMajorPillClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked)
        {
            return;
        }
        _majorFilter = clicked.Tag as string ?? string.Empty;
        foreach (var pill in MajorVersionPills.Children.OfType<ToggleButton>())
        {
            pill.IsChecked = ReferenceEquals(pill, clicked);
        }

        RenderDetailGroups();
        DetailScroll.ScrollToTop();
    }

    /// <summary>
    /// 一个游戏版本号 → 它的「大版本」（去掉补丁号）：`1.20.4` / `1.20.6` → `1.20`；
    /// `26.3` 本来就是两段，原样；`1.20.1-pre1` 这种先切掉 `-pre1` 再算 → `1.20`。
    ///
    /// 认不出来返回空串（`26w14a` 那种快照）：这些版本**不参与**大版本筛选，
    /// 只出现在「全部」里 —— 硬塞进某个大版本，用户反而要在一堆旧版里找它。
    /// </summary>
    private static string MajorOf(string? gameVersion)
    {
        var core = (gameVersion ?? string.Empty).Split('-')[0];     // 1.20.1-pre1 → 1.20.1
        var parts = core.Split('.');
        if (parts.Length < 2
            || !int.TryParse(parts[0], out _)
            || !int.TryParse(parts[1], out _))
        {
            return string.Empty;
        }
        return $"{parts[0]}.{parts[1]}";
    }

    /// <summary>大版本的排序键（`26.3` → (26, 3)、`1.20` → (1, 20)）：倒序时新版本在前。</summary>
    private static (int Major, int Minor) MajorRank(string major)
    {
        var parts = major.Split('.');
        return (int.TryParse(parts[0], out var majorPart) ? majorPart : 0,
                parts.Length > 1 && int.TryParse(parts[1], out var minorPart) ? minorPart : 0);
    }

    private void ClearDetailGroups()
    {
        DetailGroups.Children.Clear();
    }

    /// <summary>
    /// 把下一批分组铺出来（第一次调用 = 铺开头那批）。
    ///
    /// ⚠️ 铺的是 <see cref="_visibleGroups"/>（筛过的那份），不是 <see cref="_detailGroups"/> ——
    ///    大版本胶囊一改，游标就归零重铺（见 <see cref="RenderDetailGroups"/>）。
    /// ⚠️ 铺完要**看一眼有没有把一屏填满**：窗口很高、或者这个项目分组很少的时候，
    ///    12 组可能还不够一屏 —— 那样滚动条根本不出现，用户永远触发不了"滑到底"，
    ///    剩下的分组就再也看不到了。所以只要"内容还没超过视口"就继续铺。
    /// </summary>
    private void AppendDetailGroups()
    {
        if (_detailGroupsShown >= _visibleGroups.Count)
        {
            return;
        }

        var end = Math.Min(_visibleGroups.Count, _detailGroupsShown + DetailGroupBatch);
        for (var i = _detailGroupsShown; i < end; i++)
        {
            DetailGroups.Children.Add(BuildDetailGroup(_visibleGroups[i]));
        }
        _detailGroupsShown = end;

        if (_detailGroupsShown < _visibleGroups.Count)
        {
            // ⚠️ 等一轮布局再量：刚加进去的控件还没有实际高度，
            //    当场量 ExtentHeight 会一直是旧值。
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_detailGroupsShown >= _visibleGroups.Count)
                {
                    return;
                }
                DetailScroll.UpdateLayout();

                // ⚠️ **视口还没量出来就别继续**：详情页刚建出来那一瞬间（或者面板还没
                //    显示的时候）`ViewportHeight` 是 0，于是"内容 > 视口"恒不成立，
                //    这里会一口气把几百组全铺出来，打开详情页就卡一下。
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

    /// <summary>一组：标题 + 匹配说明 + 版本行（点一行就下载）。</summary>
    private CollapsibleGroup BuildDetailGroup(ModVersionGroup group)
    {
        var widget = new CollapsibleGroup { Margin = new Thickness(0, 0, 0, 10) };
        widget.SetTitle(ModVersionGroups.Title(group));

        // ⚠️「匹配你选的版本」放在**右边那行副标题**上，不拼进标题：
        //    标题后面会挂一个条目数，拼在一起会插进句子中间。
        widget.SetNote(group.MatchesFilter ? Localizer.Tr("匹配你选的版本") : string.Empty);
        widget.SetItems(BuildDetailRows(group.Versions), DpiScale);

        // ⚠️ 必须接 `ItemActivated`：整行点击就是下载（行内没有单独的按钮）。
        //    漏了这句，整页的版本行会变成点了没反应的死行。
        widget.ItemActivated += OnDetailVersionDownload;

        // ---------- 前置资源（照 PCL：展开一组时先看到"要装什么"，再是版本列表） ----------
        // ⚠️ 挂在**组内部**（`SetLead`），不是组上方：Iris 那种项目有几十个分组，
        //    放在外面就是几十张一模一样的 Sodium 卡；放进组里，折叠时自然不占地方。
        widget.SetLead(BuildDependencyBlock(group));

        // 对得上的默认展开（用户通常就是奔着它来的），其余折叠成目录
        widget.SetExpanded(group.MatchesFilter);
        return widget;
    }

    /// <summary>
    /// 一组的前置资源块（没有必需依赖 / 还没拉到数据时返回 null）。
    ///
    /// ⚠️ 卡片直接复用搜索结果那张（`BuildResultCard`）：图标、简介、下载量、
    ///    行尾指向符都在，而且整行可点 —— 点了就进那个前置项目的详情页，
    ///    用户可以顺着把前置也装了。
    /// </summary>
    private FrameworkElement? BuildDependencyBlock(ModVersionGroup group)
    {
        var deps = RequiredProjects(group);
        if (deps.Count == 0)
        {
            return null;
        }

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = Localizer.Tr("前置资源"),
            Style = (Style)FindResource("SectionTitle"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        foreach (var project in deps)
        {
            stack.Children.Add(BuildResultCard(project));
        }
        stack.Children.Add(new Border { Height = 4 });
        return stack;
    }

    /// <summary>
    /// 这一组里出现过的、**已经拉到手**的前置项目（去重、保持出现顺序）。
    ///
    /// ⚠️ 只认 `dependency_type = required`（可选前置 / 内嵌 / 不兼容都不算"必须装"）：
    ///    把可选前置也列出来，等于给每个项目都挂一串"其实不用装"的东西。
    /// </summary>
    private List<JsonObject> RequiredProjects(ModVersionGroup group)
    {
        var result = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var version in group.Versions)
        {
            foreach (var id in RequiredDependencyIds(version))
            {
                if (!seen.Add(id))
                {
                    continue;
                }
                if (_depProjects.TryGetValue(id, out var project) && project is not null)
                {
                    result.Add(project);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 把详情页里所有**必需**依赖的项目查下来，填进 <see cref="_depProjects"/>。
    ///
    /// ⚠️ 一定要在"能把失败记住"的前提下重试：`/project/{id}` 404 的项目（被删了 / 私有）
    ///    也存一个 null，否则每铺一组就再打一次接口。
    /// ⚠️ 拉完要**重铺一次**：分组是分批铺的，前 12 组先出来时缓存可能还是空的 ——
    ///    不重铺的话「前置资源」要等用户滚动续铺才可能出现。
    /// </summary>
    private async Task FetchDependencyProjectsAsync()
    {
        if (_depFetchStarted)
        {
            return;
        }
        _depFetchStarted = true;

        var ids = _detailGroups
            .SelectMany(group => group.Versions)
            .SelectMany(RequiredDependencyIds)
            .Distinct(StringComparer.Ordinal)
            .Where(id => !_depProjects.ContainsKey(id))
            .Take(24)       // 极端项目能挂几十个前置：只查前 24 个，别把接口打满
            .ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var loaded = false;
        foreach (var id in ids)
        {
            try
            {
                var project = await ModrinthApi.GetProjectAsync(id);
                _depProjects[id] = project;
                loaded |= project is not null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                          or IOException or System.Text.Json.JsonException
                                          or InvalidOperationException)
            {
                _depProjects[id] = null;
            }
        }

        if (loaded && DetailPane.Visibility == Visibility.Visible)
        {
            RenderDetailGroups();
        }
    }

    /// <summary>一个版本里"必需"的依赖项目 id（`dependency_type = required`）。</summary>
    private static IEnumerable<string> RequiredDependencyIds(JsonObject version)
    {
        if (version["dependencies"] is not JsonArray deps)
        {
            yield break;
        }
        foreach (var node in deps)
        {
            if (node is not JsonObject dep)
            {
                continue;
            }
            var type = dep["dependency_type"]?.GetValue<string>();
            if (!string.Equals(type, "required", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var id = dep["project_id"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(id))
            {
                yield return id;
            }
        }
    }

    /// <summary>没有分组时的那句话（没有版本 / 拿不到数据）。</summary>
    private void ShowDetailGroupMessage(string text)
    {
        ClearDetailGroups();
        // 空态下那排胶囊也得撤掉 —— 留着「全部 / 1.20」点下去什么都不会发生
        MajorVersionPills.Children.Clear();
        MajorVersionScroll.Visibility = Visibility.Collapsed;
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

            // 类型**不翻译**（跟分组标题同一条规则，见 ModVersionGroups.Title）：
            // Modrinth 原本就叫 release / beta / alpha，翻成「正式版/测试版/预览版」对不上号。
            var meta = new List<string> { Str(version, "version_type", string.Empty) };
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

    // 设计说明见 docs/design-notes.md#mosslight-app-views-modspage-xaml-cs

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

        // ⚠️ 版本那句里的"你筛选的 …"**必须真的是「版本」里选的那个**（或用户手敲的）：
        //    以前写的是本地版本，句子却说是"你筛选的" —— 那是两回事。
        // ⚠️ 选了「全部」的时候也要写「全部」，不能留空：
        //    1.0.0 的原文案是 `version or tr("全部")`，空串会渲染成
        //    "已按你筛选的 1.20.4 +  置顶"（中间两个空格，看着像写坏了）。
        var versionPicked = GameVersionFilter;
        var versionLabel = string.Equals(versionPicked, ModFilters.Any, StringComparison.Ordinal)
            ? Localizer.Tr("全部")
            : versionPicked;

        // ⚠️ 加载器不再是"用户筛的"（搜索卡上没有这一格了），这里写的是**当前档案装的那家**。
        var loaderPicked = _type.ShowsLoaderFilter ? TargetLoader : ModFilters.Any;
        var loaderLabel = string.Equals(loaderPicked, ModFilters.Any, StringComparison.Ordinal)
            ? Localizer.Tr("全部")
            : LoaderTitleOf(loaderPicked);

        // 数据包：落点要等下载时用户挑存档才知道，所以这里**不写死路径**
        if (_type.Target == ModInstallTarget.WorldDatapacks)
        {
            DetailHint.Text = groupCount == 0
                ? Localizer.Tr("共 {n} 个版本。下载时再选放进哪个存档。", ("n", 0))
                : Localizer.Tr("共 {n} 个版本，已按 {version} 挑过。下载时再选放进哪个存档。",
                    ("n", groupCount), ("version", versionLabel));
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

        // 模组：说清楚分组数，以及哪一组被置顶了
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