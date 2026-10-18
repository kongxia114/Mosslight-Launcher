using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mosslight.Core.Localization;
using Mosslight.Core.Services;
using Mosslight.Core.Services.Engine;

namespace Mosslight.App.Views;

/// <summary>
/// 「版本设置」页。移植自 1.0.0 的 `ui/pages/version_settings_page.py`（844 行）。
///
/// ## 三个核心约定（都照 1.0.0）
///
/// 1. **改动即时保存、没有保存按钮**。页面是常驻的（切页而不是弹窗），再挂一对
///    保存/取消反而要多记一份"打开时的快照"，还容易漏掉"切走时忘了保存"。
///    数值走 400ms 防抖（1.0.0 的 <c>SAVE_DELAY_MS</c>）：拖滑块会连着发几十次
///    <c>ValueChanged</c>，每次都写盘的话 `versions.json` 会被写几十遍。
///
/// 2. **三态，不是开关**。每一项要么"跟随启动器默认"（`versions.json` 里**没有这个键**），
///    要么"这个版本自己定"（文件里有）。**不是**把默认值抄一份过来 —— 抄过来的话，
///    以后改了启动器默认，所有动过一次的版本都不会跟着变。所以"跟随默认" = 删掉那个键，
///    界面上表现为"输入框留空 / 勾上跟随全局设置"。
///
/// 3. **只写这个版本设过的项**。保存时把整个 <see cref="PerVersionSettings"/> 交出去，
///    <see cref="IVersionSettingsService.Update"/> 是**整体替换**（跟 1.0.0 的
///    <c>VersionSettings.update()</c> 一样），所以没设的项必须是 <c>null</c>。///
/// ## ⚠️ 四个**空壳**：版本隔离 / 游戏窗口标题 / 内存管理 / 启动前执行命令
///
/// 结论先说：**1.0.0 里这四个是"禁用的只读展示"，不是"能改但不落盘"**。
/// 证据是 1.0.0 的 <c>_shell_combo()</c> / <c>_shell_edit()</c> 两个工厂函数：
/// 它们建完控件就 <c>setEnabled(False)</c>，页面上再没有别的地方给它们赋过值。
/// 截图里它们看着像普通下拉框/输入框，只是**禁用态在静态图里看不出来**
/// （Qt 的禁用 QComboBox 照样画一个下拉箭头）。所以这里照实做成 `IsEnabled="False"`
/// + 旁边一句"还没接上"，跟 1.0.0 一模一样。
///
/// 逐个查过 2.0 的 <c>Mosslight.Core</c>，四个都没有对应能力，所以**一个都不能做成可编辑**：
///
/// | 项 | 1.0.0 | Core 里有没有 | 为什么不做成可编辑 |
/// |---|---|---|---|
/// | 版本隔离 | 空壳（显示扫描器的判断结果） | 只有 `GameVersion.Isolated` —— 只读，由 `VersionScanner.DetectGameDir()` 按版本文件夹里有没有 `saves/`、`options.txt` 这些特征**判出来**的 | 要"改"它得真去挪存档和 mod（PCL 也是这个语义）。2.0 没有搬文件的代码，做成开关就是"改了不生效" |
/// | 游戏窗口标题 | 空壳 | 没有。`LaunchPlanBuilder` 里根本没有生成窗口标题参数的地方 | 1.0.0 的说明原文就写了"先把值写进「游戏参数」里（`--title` 还没验证过是否所有版本都吃）"—— 它自己也没接 |
/// | 内存管理（GC 预设） | 空壳 | 没有。`SystemMemory` 的注释明说"这里只做显示和建议，**不碰启动参数**"；`LaunchPlanBuilder` 只拼 `-Xms` / `-Xmx` | GC 参数在不同 Java 大版本之间差异很大，加错了 JVM 直接拒绝启动。要做得先把预设逐版本验一遍 |
/// | 启动前执行命令 | 空壳 | 没有。`RealLaunchService` 里没有任何"起进程之前跑一条命令"的钩子 | 这是要真起进程、还要处理失败/超时/工作目录的活；1.0.0 也没做 |
///
/// PCL 里那两个「禁用 Java Launch Wrapper / 禁用 LWJGL Unsafe Agent」是 PCL 自己往
/// 命令行塞补丁的开关，我们没塞补丁，**故意不画** —— 画了就是谁也看不懂的死开关
/// （1.0.0 的取舍，照搬）。
///
/// ## 记住"要设置哪个版本"
/// 这一页是**切页**不是弹窗，所以得自己记住目标版本（<see cref="Configure"/>），
/// 不能靠 <c>AppSettings.LastVersion</c> —— 用户可能只是在
/// 版本列表里点了一下看看。
/// </summary>
public partial class VersionSettingsPage : UserControl, IMosslightPage
{
    /// <summary>1.0.0 的 <c>SAVE_DELAY_MS</c>：拖滑块时不要每一格都写一次 `versions.json`。</summary>
    private const int SaveDelayMs = 400;

    /// <summary>1.0.0 给「最小」那个 QSpinBox 的上限（<c>setRange(MEMORY_MIN, 32768)</c>）。</summary>
    private const int MinMemoryMax = 32768;

    private const int TabSettingsIndex = 0;
    private const int TabModsIndex = 1;

    /// <summary>
    /// 刷新界面时挡住"值变了 → 回写文件"的循环。
    ///
    /// ⚠️ **初值必须是 true**，不能是 false：<c>Slider.ValueChanged</c> 在
    ///    `InitializeComponent()` **解析 XAML 的过程中就会触发**（Maximum / Value
    ///    一被赋值就触发），那时候后面那些 `x:Name` 控件**还没建出来**，
    ///    处理器里一碰就是 NullReferenceException，而且是在窗口构造期抛。
    ///    设置页踩过这个坑（见 SettingsPage._loading 的注释），这一页同一个坑。
    /// </summary>
    private bool _loading = true;

    /// <summary>正在重建 Java 下拉 —— 挡住 <c>SelectedIndex</c> 那一下被当成"用户选的"。</summary>
    private bool _fillingJava;

    /// <summary>正在扫描 Java —— 「重新扫描」点第二下直接忽略（1.0.0 的 <c>isRunning()</c> 判断）。</summary>
    private bool _scanning;

    /// <summary>
    /// 这一页已经离开可视树了（切走 / 窗口关了）。
    /// 后台扫描回来时**不要再碰控件** —— 对着一棵脱离可视树的控件树改 Items 在 WPF 里会抛。
    /// </summary>
    private bool _detached;

    /// <summary>Java 下拉每一项对应的 `java_path`（空串 = 跟随全局设置），下标跟 Items 一一对应。</summary>
    private readonly List<string> _javaPaths = new();

    private GameVersion? _version;

    /// <summary>`--page versionsettings:&lt;子段&gt;` 要求滚到哪张卡片（自动化截图用，见 <see cref="ShowSection"/>）。</summary>
    private string? _pendingSection;

    /// <summary>滚动试过几次。展开「高级选项」会改变布局，一趟不一定滚得准，允许重试。</summary>
    private int _scrollTries;

    private readonly DispatcherTimer _saveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(SaveDelayMs),
    };

    public VersionSettingsPage()
    {
        InitializeComponent();

        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Flush();
        };
        // 切走时把还没落盘的那次改动补上，不然"敲完 400ms 内切页"这次改动就丢了
        Unloaded += (_, _) =>
        {
            _detached = true;
            _saveTimer.Stop();
            Flush();
        };
        // 滚动到指定子段：等布局排完再滚（离屏截图不 Show 窗口，Loaded 不一定来，
        // 所以挂在 LayoutUpdated 上而不是 Loaded —— 跟设置页同一套）
        LayoutUpdated += OnLayoutUpdated;

        // NumberBox 的「值变了」是普通事件，不是 XAML 能挂的 RoutedEvent —— 在码里接。
        // ⚠️ 排在 InitializeComponent **之后**：构造期的赋值已经被 _loading 挡住了。
        MinBox.ValueChanged += OnMinChanged;

        BuildStaticItems();
        HookPlaceholders();

        // 没有明确目标就先按"上次启动的版本"来（点进来看一眼也是合理的）
        // ⚠️ Configure → LoadFromSettings 自己会管 _loading（进去置 true、finally 置 false），
        //    所以构造完 _loading 已经是 false 了，这里**不要**再补一次 LoadFromSettings ——
        //    那会把 Java 下拉和内存条白重建一遍。
        var versions = App.Services.Games.Installed;
        var wanted = App.Settings.LastVersion;
        Configure(versions.FirstOrDefault(v => v.Id == wanted) ?? versions.FirstOrDefault());

        // 1.0.0 在 __init__ 末尾就调 set_tab(TAB_SETTINGS)：两个 ToggleButton 的
        // 选中态（强调色 + 下划线）是 SetTab 贴上去的，不调的话开局两个 tab 都是灰的。
        SetTab(TabSettingsIndex);
    }

    /// <summary>这一页不做入场动画（1.0.0 的版本设置页就是普通卡片，没有滑入）。</summary>
    public Panel? AnimationHost => null;

    // ============================================================
    // 搭界面里那些"建一次就够"的东西
    // ============================================================

    /// <summary>
    /// 三个**禁用**下拉的候选项。
    ///
    /// ⚠️ 文案逐个写出来，**不要**写成 <c>foreach (var key in new[] {"开启", "关闭"})</c>：
    ///    那样字面量是经由变量进 <see cref="Localizer.Tr"/> 的，1.0.0 的文案提取工具
    ///    认不出来（会报"没走文案系统"）。1.0.0 的源码注释里专门记了这条。
    /// </summary>
    private void BuildStaticItems()
    {
        // 版本隔离：显示**当前实际判断结果**（0 = 开启，1 = 关闭），不让改
        IsolateCombo.Items.Add(Localizer.Tr("开启"));
        IsolateCombo.Items.Add(Localizer.Tr("关闭"));
        IsolateCombo.SelectedIndex = 1;

        // 启动游戏前进行内存优化（空壳）
        OptimizeCombo.Items.Add(Localizer.Tr("跟随全局设置"));
        OptimizeCombo.Items.Add(Localizer.Tr("开启"));
        OptimizeCombo.Items.Add(Localizer.Tr("关闭"));
        OptimizeCombo.SelectedIndex = 0;

        // 内存管理（空壳）
        GcCombo.Items.Add(Localizer.Tr("不指定（可自定义）"));
        GcCombo.Items.Add(Localizer.Tr("标准 G1GC"));
        GcCombo.Items.Add(Localizer.Tr("调优 G1GC"));
        GcCombo.SelectedIndex = 0;
    }

    /// <summary>
    /// 接上"输入框空着就显示占位文案"。
    ///
    /// WPF 的 TextBox 没有 Qt 的 <c>setPlaceholderText</c>，本工程的做法是
    /// 同格子里垫一个 TextBlock（样式 <c>FieldPlaceholder</c>），在这里按 Text 切换可见性。
    /// 选它而不是 Adorner：离屏截图走纯 Measure/Arrange，Adorner 层不保证被建出来。
    /// </summary>
    private void HookPlaceholders()
    {
        HookPlaceholder(WindowTitleBox, WindowTitlePlaceholder);
        HookPlaceholder(InfoBox, InfoPlaceholder);
        HookPlaceholder(ServerBox, ServerPlaceholder);
        HookPlaceholder(JvmBox, JvmPlaceholder);
        HookPlaceholder(GameBox, GamePlaceholder);
        HookPlaceholder(PreLaunchBox, PreLaunchPlaceholder);
    }

    private static void HookPlaceholder(TextBox box, TextBlock placeholder)
    {
        void Sync() => placeholder.Visibility =
            box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Sync();
        Sync();
    }

    // ============================================================
    // 目标版本 / 读
    // ============================================================

    /// <summary>设置"这一页在改哪个版本"。切页之前由主窗口/版本页/启动页调。</summary>
    public void Configure(GameVersion? version)
    {
        _version = version;
        LoadFromSettings();
    }

    /// <summary>把文件里的覆盖项贴到控件上（进来时、换版本时都调）。</summary>
    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            if (_version is null)
            {
                NameText.Text = Localizer.Tr("还没有选中版本");
                PathText.Text = string.Empty;
                RefreshSaveHint();
                return;
            }

            var service = App.Services.VersionSettings;
            var custom = service.Get(_version.Id);
            var effective = service.Resolve(_version.Id, service.LauncherDefaults());

            NameText.Text = _version.Name;
            PathText.Text = _version.Path;

            // 版本隔离：只显示扫描器判出来的结果（0 = 开启 / 1 = 关闭）
            IsolateCombo.SelectedIndex = _version.Isolated ? 0 : 1;

            // 自定义信息：留空 = 用品牌默认值（Branding.LaunchBrand），所以这里如实显示空
            InfoBox.Text = custom.CustomInfo ?? string.Empty;

            // 内存：文件里有没有这两个键 = 跟不跟随全局
            var isCustom = custom.MinMemory is not null || custom.MaxMemory is not null;
            MemCustom.IsChecked = isCustom;
            MemFollow.IsChecked = !isCustom;
            MinBox.SetValueSilently(
                Math.Clamp(effective.MinMemory, SystemMemory.MemoryMin, MinMemoryMax));
            // ⚠️ 顺序要紧：先抬 Maximum 再给 Value，反过来的话 Value 会先被老上限夹一次
            MaxSlider.Maximum = MaxUpperBound();
            MaxSlider.Value = Math.Clamp(effective.MaxMemory,
                SystemMemory.MemoryMin, (int)MaxSlider.Maximum);
            MaxValue.Text = $"{CurrentMaxMb()} MB";

            // 服务器 / 高级选项
            ServerBox.Text = custom.ServerAddress ?? string.Empty;
            JvmBox.Text = custom.ExtraJvmArgs ?? string.Empty;
            GameBox.Text = custom.ExtraGameArgs ?? string.Empty;

            // ⚠️ Java 下拉必须在**读完覆盖项之后**重建：它是按存下来的 java_path 选索引的。
            //    重建早了这个索引还是上一次的（默认"跟随全局设置"），
            //    界面显示错的还算小事，一存盘就把用户指定的 Java 冲掉了。
            FillJavaCombo();
        }
        finally
        {
            _loading = false;
        }

        RefreshLabels();
        EnsureJavaScanned();
    }

    /// <summary>最大内存滑块的上限：跟物理内存挂钩（1.0.0 的 <c>_make_memory_card</c>）。</summary>
    private int MaxUpperBound()
    {
        // ⚠️ 只查一次：Query() 是系统调用，查两次可能在两次之间被别的进程改掉，
        //    那样"有没有内存"和"有多少内存"会对不上。
        var info = SystemMemory.Query();
        var upper = info.Ok
            ? Math.Min(SystemMemory.MemoryMax, Math.Max(4096, info.TotalMb))
            : SystemMemory.MemoryMax;
        // 对齐到 512 的档（1.0.0 是 `(upper // MEMORY_STEP) * MEMORY_STEP`）
        return upper / SystemMemory.MemoryStep * SystemMemory.MemoryStep;
    }

    private int CurrentMaxMb() => SystemMemory.SnapMemory((int)MaxSlider.Value);

    private int CurrentMinMb() => SystemMemory.SnapMemory(MinBox.Value);

    // ============================================================
    // 动态文字（1.0.0 的 retranslate() 那几行）
    // ============================================================

    private void RefreshLabels()
    {
        ScopeHint.Text = Localizer.Tr("这些设置只对该游戏版本生效，不影响其他版本。");

        // ---- 启动选项 ----
        IsolateHint.Text = Localizer.Tr(
            "还没接上。现在按版本文件夹里有没有 saves/mods 自动判断，这里显示的就是判断结果。");
        TitleHint.Text = Localizer.Tr(
            "还没接上。这个版本想单独用一个窗口标题的话，先把值写进「游戏参数」里"
            + "（--title 还没验证过是否所有版本都吃）。");
        InfoHint.Text = Localizer.Tr(
            "游戏主界面那行字里「斜杠后面」那段（启动参数里的 ${version_type}）。"
            + "留空就自动填「启动器名/加载器（N 个模组）」，填 release 就不显示。");

        // 自定义信息的占位是"这个版本自带的 type"（1.0.0 的 _load）
        InfoPlaceholder.Text = !string.IsNullOrEmpty(_version?.Type)
            ? _version!.Type
            : Localizer.Tr("版本自带");

        // ---- 内存 ----
        MemoryModeHint.Text = Localizer.Tr(
            "「自动配置」还没做 —— 它会按装了哪些 Mod 和剩余内存动态算，先在下面自己拖。"
            + "没选「自定义」时用的是启动器全局设置里的内存。");
        SyncMemoryEnabled();
        RefreshMemoryBar();

        // ---- 服务器 ----
        var account = App.Services.Accounts.Current;
        AccountText.Text = account is null
            ? Localizer.Tr("还没有档案")
            : Localizer.Tr("离线登录 · {name}", ("name", account.Name));
        LoginHint.Text = Localizer.Tr(
            "现在只有离线登录（正版登录还没做）。离线档案按名字生成 UUID，"
            + "和朋友联机时名字要对得上。");
        ServerHint.Text = Localizer.Tr(
            "英文冒号分隔 IP 和端口，例如 233.233.233.233:12345。"
            + "1.20 起走 QuickPlay，更老的版本走 --server/--port。");

        // ---- 高级选项 ----
        ShellHint.Text = Localizer.Tr(
            "「内存管理」（自动挑 GC 参数）和「启动前执行命令」还是空壳 —— "
            + "画出来占个位，功能后面补。");
        AdvancedArgsHint.Text = Localizer.Tr(
            "「Java 虚拟机参数」原样追加到 java 命令行；填错了 JVM 会拒绝启动。"
            + "「游戏参数」追加在版本自带的参数后面，同名的选项以这里为准。");
        RefreshAdvancedToggle();

        // ---- Mod 管理 ----
        ModNoticeLine1.Text = Localizer.Tr(
            "你需要先安装 Forge、Fabric 等 Mod 加载器才能使用 Mod，请在下载页面安装这些版本。");
        ModNoticeLine2.Text = Localizer.Tr(
            "如果你已经安装了 Mod 加载器，那么你很可能选错了版本，点下面的「版本选择」切换一个。");

        RefreshJavaHint();
        RefreshMods();
        RefreshSaveHint();
    }

    /// <summary>
    /// Java 那一行下面写清楚**最终会用哪个**（1.0.0 的 <c>_refresh_java_hint</c>）。
    /// </summary>
    private void RefreshJavaHint()
    {
        if (_version is null)
        {
            JavaHint.Text = Localizer.Tr("还没有选中版本");
            return;
        }

        var service = App.Services.VersionSettings;
        var effective = service.Resolve(_version.Id, service.LauncherDefaults());
        var path = effective.JavaPath;
        var major = _version.JavaMajor;

        if (!string.IsNullOrEmpty(path))
        {
            JavaHint.Text = Localizer.Tr("将会使用（本版本指定）：{path}", ("path", path));
        }
        else if (major is > 0)
        {
            JavaHint.Text = Localizer.Tr(
                "跟随全局设置；这个版本要求 Java {n}，启动时按它挑。", ("n", major.Value));
        }
        else
        {
            JavaHint.Text = Localizer.Tr("跟随全局设置；版本没写 Java 要求，按默认挑。");
        }
    }

    /// <summary>
    /// 画的是**最终会生效**的值（选了自定义就是这里的，否则是全局的）。
    /// 1.0.0 的 <c>_refresh_memory_bar</c>。
    /// </summary>
    private void RefreshMemoryBar()
    {
        var info = SystemMemory.Query();
        var heap = CurrentMaxMb();
        var overhead = SystemMemory.JvmOverheadMb(heap);
        MemBar.SetValues(info.TotalMb, info.UsedMb, heap, overhead);

        if (info.Ok)
        {
            MemoryLegend.Text = Localizer.Tr(
                "物理内存 {total} GB · 系统已用 {used} GB · 游戏最多占用 {heap} GB"
                + " · JVM 开销约 {overhead} GB",
                ("total", Gb(info.TotalMb)), ("used", Gb(info.UsedMb)),
                ("heap", Gb(heap)), ("overhead", Gb(overhead)));
        }
        else
        {
            MemoryLegend.Text = Localizer.Tr("读不出系统内存，这条只按你填的值画。");
        }
    }

    /// <summary>MB → "12.4"（配合文案里的 GB 用）。</summary>
    private static string Gb(int megabytes)
        => (Math.Max(0, megabytes) / 1024.0).ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>没选「自定义」时内存控件灰掉（**但仍然显示**最终生效的值，1.0.0 的 <c>_sync_enabled</c>）。</summary>
    private void SyncMemoryEnabled()
    {
        var custom = MemCustom.IsChecked == true;
        MinBox.IsEnabled = custom;
        MaxSlider.IsEnabled = custom;
    }

    // ============================================================
    // Mod 管理（2026-10 接上；1.0.0 到这页只有一句"Mod 列表还没做"）
    // ============================================================

    /// <summary>这一版的 mods 目录（没加载器 / 没选中版本时是空串）。</summary>
    private string _modsDir = string.Empty;

    /// <summary>磁盘上的一个 mod + 从 jar 里读出来的展示信息。</summary>
    private sealed record ModEntry(ModsDirectory.LocalMod File, ModMeta Meta);

    /// <summary>当前列表（磁盘顺序已经被名字排过）。</summary>
    private List<ModEntry> _modEntries = new();

    /// <summary>扫描代次：连着点两次「刷新」时，只有最后那趟的结果能落进界面。</summary>
    private int _modScan;

    /// <summary>
    /// 切到「Mod 管理」时刷新这一块。
    ///
    /// 没加载器的版本还是显示 1.0.0 那张提示卡（PCL 的文案）；装了加载器的版本
    /// 换成真的列表：行是 <see cref="ModMetadata"/> 从 jar 里读的，开关和删除走
    /// <see cref="ModsDirectory"/> —— 跟启动器"认为 mods 在哪"用的是同一个判断
    /// （<see cref="ModsDirectory.ResolveModsDir"/>，吃扫描器的 <c>GameDir</c>），
    /// 所以列表里的东西就是游戏真会读的东西。
    /// </summary>
    private void RefreshMods()
    {
        var version = _version;
        var hasLoader = version is not null
                        && (version.Kind == VersionKind.Loader
                            || !string.IsNullOrEmpty(version.LoaderKey));

        ModNoticeCard.Visibility = hasLoader ? Visibility.Collapsed : Visibility.Visible;
        ModsCard.Visibility = hasLoader ? Visibility.Visible : Visibility.Collapsed;

        // 换版本了：上一趟还在飞的扫描不能把旧版本的结果贴到新版本上
        _modScan++;
        _modEntries = new List<ModEntry>();
        ModRows.Children.Clear();

        if (!hasLoader)
        {
            _modsDir = string.Empty;
            return;
        }

        _modsDir = ModsDirectory.ResolveModsDir(App.Settings.MinecraftDir, version);
        ModsPathText.Text = Localizer.Tr("mods 目录：{path}", ("path", _modsDir));
        _ = ReloadModsAsync();
    }

    /// <summary>重扫一遍 mods 目录（异步：jar 的元数据要一个个开 zip 读）。</summary>
    private async Task ReloadModsAsync()
    {
        if (_modsDir.Length == 0)
        {
            return;
        }

        var dir = _modsDir;
        var scan = ++_modScan;
        ModsLoading.Visibility = Visibility.Visible;
        ModsEmpty.Visibility = Visibility.Collapsed;
        ModCountText.Text = string.Empty;

        List<ModEntry> entries;
        try
        {
            entries = await Task.Run(() => ScanMods(dir));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (scan == _modScan && !_detached)
            {
                ModsLoading.Visibility = Visibility.Collapsed;
                ModCountText.Text = Localizer.Tr("读不了 mods 目录：{path}", ("path", dir));
            }
            return;
        }

        if (scan != _modScan || _detached)
        {
            return;
        }

        _modEntries = entries;
        ModsLoading.Visibility = Visibility.Collapsed;
        RenderMods();
    }

    private static List<ModEntry> ScanMods(string dir)
    {
        var entries = new List<ModEntry>();
        foreach (var file in ModsDirectory.ListMods(dir))
        {
            entries.Add(new ModEntry(file, ModMetadata.Read(file.Path)));
        }
        entries.Sort((a, b) => string.Compare(a.Meta.Name, b.Meta.Name, StringComparison.OrdinalIgnoreCase));
        return entries;
    }

    /// <summary>把 <see cref="_modEntries"/> 画成行（切开关 / 删一个之后只重画这一块）。</summary>
    private void RenderMods()
    {
        ModRows.Children.Clear();

        var disabled = 0;
        foreach (var entry in _modEntries)
        {
            if (!entry.File.Enabled)
            {
                disabled++;
            }
            ModRows.Children.Add(BuildModRow(entry));
        }

        ModCountText.Text = Localizer.Tr("已安装 {n} 个 Mod", ("n", _modEntries.Count))
                            + (disabled > 0 ? Localizer.Tr("（{n} 个已禁用）", ("n", disabled)) : string.Empty);
        ModsEmpty.Visibility = _modEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 一行：图标 · 名字/元信息 · 大小 · 开关 · 删除。
    ///
    /// ⚠️ 行是**码里建**的（不是 DataTemplate + 绑定）：这一页的惯例就是动态块手搭，
    ///    而且禁用态要整行压暗（<see cref="UIElement.Opacity"/>），绑定的写法反而绕。
    /// </summary>
    private FrameworkElement BuildModRow(ModEntry entry)
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 7, 0, 7),
            Opacity = entry.File.Enabled ? 1.0 : 0.62,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 图标
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 大小
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 开关
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 删除

        // 图标在最前面（2026-10 用户要求：图标在前、开关挪到后面）
        var icon = BuildModIcon(entry.Meta);
        icon.Margin = new Thickness(0, 0, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock
        {
            Text = entry.Meta.Name,
            Style = (Style)FindResource("VersionRowName"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var meta = ModMetaLine(entry);
        if (meta.Length > 0)
        {
            texts.Children.Add(new TextBlock
            {
                Text = meta,
                Style = (Style)FindResource("VersionRowMeta"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var size = new TextBlock
        {
            Text = FormatSize(entry.File.Size),
            Style = (Style)FindResource("HintText"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 4, 0),
        };
        Grid.SetColumn(size, 2);
        grid.Children.Add(size);

        var toggle = new CheckBox
        {
            Style = (Style)FindResource("SwitchCheckBox"),
            IsChecked = entry.File.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),
            ToolTip = Localizer.Tr("启用"),
        };
        toggle.Click += (_, _) => ToggleMod(entry, toggle);
        Grid.SetColumn(toggle, 3);
        grid.Children.Add(toggle);

        var remove = new Button
        {
            Content = Localizer.Tr("删除"),
            Style = (Style)FindResource("DangerButton"),
            Padding = new Thickness(12, 4, 12, 4),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        remove.Click += (_, _) => DeleteMod(entry);
        Grid.SetColumn(remove, 4);
        grid.Children.Add(remove);

        return grid;
    }

    /// <summary>
    /// 行首那个 32×32 的图标：jar 里有就画，没有就用名字首字母占位。
    ///
    /// ⚠️ **不画假图标**：没有就是首字母方块 —— 给每个 mod 都配一张通用图，
    ///    看起来像读到了，实际什么都没读到。
    /// </summary>
    private FrameworkElement BuildModIcon(ModMeta meta)
    {
        const double Side = 32;
        var bitmap = DecodeIcon(meta.Icon);
        if (bitmap is not null)
        {
            return new Border
            {
                Width = Side,
                Height = Side,
                CornerRadius = new CornerRadius(6),
                Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill },
                SnapsToDevicePixels = true,
            };
        }

        var initial = meta.Name.Length > 0
            ? char.ToUpperInvariant(meta.Name[0]).ToString()
            : "?";
        return new Border
        {
            Width = Side,
            Height = Side,
            CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource("BgElevatedBrush"),
            SnapsToDevicePixels = true,
            Child = new TextBlock
            {
                Text = initial,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
            },
        };
    }

    /// <summary>图标字节 → 位图（解不出来返回 null，行里退回首字母）。</summary>
    private static BitmapSource? DecodeIcon(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
        {
            return null;
        }
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 32;      // 列表里就 32 宽，别把 512×512 原图全解出来
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();                    // 跨线程用：冻住之后 UI 线程才敢拿
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException
                                       or IOException
                                       or ArgumentException
                                       or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>行里第二行小字：`版本 · 加载器 · 文件名 · 作者 …`（有什么写什么）。</summary>
    private static string ModMetaLine(ModEntry entry)
    {
        var parts = new List<string>();
        if (entry.Meta.Version.Length > 0)
        {
            parts.Add(entry.Meta.Version);
        }
        if (entry.Meta.Loader.Length > 0)
        {
            parts.Add(entry.Meta.Loader);
        }
        parts.Add(entry.File.FileName);
        if (entry.Meta.Authors.Length > 0)
        {
            parts.Add(Localizer.Tr("作者 {names}", ("names", entry.Meta.Authors)));
        }
        return string.Join(" · ", parts);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return string.Empty;
        }
        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }
        var kb = bytes / 1024.0;
        return kb < 1024
            ? kb.ToString("F0", CultureInfo.InvariantCulture) + " KB"
            : (kb / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>启用 / 禁用：只改后缀（<c>.disabled</c>），跟 PCL / HMCL 一个约定。</summary>
    private void ToggleMod(ModEntry entry, CheckBox toggle)
    {
        var want = toggle.IsChecked == true;
        var name = entry.Meta.Name;
        var path = ModsDirectory.SetEnabled(entry.File.Path, want);

        if (path is null)
        {
            // 回弹：文件被游戏独占着 / 权限不对 —— 不能让开关停在"好像改好了"的位置
            toggle.IsChecked = !want;
            MainWindow.Current?.ShowToast(
                Localizer.Tr("改不了 {name}：文件可能被游戏占着。", ("name", name)));
            return;
        }

        var updated = new ModEntry(
            entry.File with
            {
                Path = path,
                FileName = Path.GetFileName(path),
                Enabled = want,
            },
            entry.Meta);

        var index = _modEntries.IndexOf(entry);
        if (index >= 0)
        {
            _modEntries[index] = updated;
        }
        RenderMods();
        MainWindow.Current?.ShowToast(want
            ? Localizer.Tr("已启用 {name}", ("name", name))
            : Localizer.Tr("已禁用 {name}", ("name", name)));
    }

    /// <summary>删除：**先问一句**（这个是永久操作，1.0.0 的档案删除也是先问）。</summary>
    private void DeleteMod(ModEntry entry)
    {
        var name = entry.Meta.Name;
        var answer = MessageBox.Show(
            Window.GetWindow(this),
            Localizer.Tr("要删掉 {name} 吗？删了就回不来了。", ("name", name)),
            Localizer.Tr("删除 Mod"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        if (!ModsDirectory.Delete(entry.File.Path))
        {
            MainWindow.Current?.ShowToast(
                Localizer.Tr("删不掉 {name}：文件可能被游戏占着。", ("name", name)));
            return;
        }

        _modEntries.Remove(entry);
        RenderMods();
        MainWindow.Current?.ShowToast(Localizer.Tr("已删除 {name}", ("name", name)));
    }

    private void OnModsRefreshClick(object sender, RoutedEventArgs e)
    {
        _ = ReloadModsAsync();
    }

    /// <summary>打开 mods 目录。目录不存在就先建出来（点"打开文件夹"不该报错）。</summary>
    private void OnOpenModsFolderClick(object sender, RoutedEventArgs e)
    {
        if (_modsDir.Length == 0)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(_modsDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _modsDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                       or InvalidOperationException
                                       or IOException
                                       or UnauthorizedAccessException)
        {
            MainWindow.Current?.ShowToast(
                Localizer.Tr("打不开文件夹：{err}", ("err", ex.Message)));
        }
    }

    /// <summary>
    /// 底部那行"改动存到哪"。
    ///
    /// ⚠️ 这是 2.0 补的一句（1.0.0 没有）：1.0.0 的分区说明里没有"存哪"，
    ///    但这一页现在**没有保存按钮**，不说清楚会让人以为改动没落盘。
    ///    路径是 <c>config.json 同目录的 versions.json</c> —— 便携模式下也跟着走。
    /// </summary>
    private void RefreshSaveHint()
    {
        if (_version is null)
        {
            SaveHint.Text = string.Empty;
            return;
        }
        SaveHint.Text = Localizer.Tr(
            "改动即时保存到 {path}（只写这个版本自己设过的项；留空 = 跟随全局设置）。",
            ("path", VersionSettingsPath()));
    }

    private static string VersionSettingsPath()
    {
        var dir = Path.GetDirectoryName(App.Settings.FilePath);
        return string.IsNullOrEmpty(dir) ? "versions.json" : Path.Combine(dir, "versions.json");
    }

    // ============================================================
    // 保存
    // ============================================================

    /// <summary>排一次防抖保存（拖滑块 / 敲字都会连着调很多次）。</summary>
    private void ScheduleSave()
    {
        if (_loading || _version is null)
        {
            return;
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>
    /// 把界面上的值写回 `versions.json`。
    ///
    /// 空值不写（<c>VersionSettings.Normalize</c> 会丢掉），
    /// 所以"清空输入框" = 恢复成跟随默认。**没设的项一律给 null** ——
    /// <c>Update</c> 是整体替换，漏一个就等于把这个键删了。
    /// </summary>
    private void Flush()
    {
        if (_loading || _version is null)
        {
            return;
        }

        var java = SelectedJavaPath();
        var useCustomMemory = MemCustom.IsChecked == true;

        // ⚠️ Icon 要**带过去**：Update 是整体替换，这个页面不管图标
        //    （1.0.0 的图标在版本列表那边挑），不带走的话自动保存会把它抹掉。
        var overrides = new PerVersionSettings
        {
            JavaPath = java.Length > 0 ? java : null,
            MinMemory = useCustomMemory ? CurrentMinMb() : null,
            MaxMemory = useCustomMemory ? CurrentMaxMb() : null,
            ExtraJvmArgs = Trim(JvmBox.Text),
            ExtraGameArgs = Trim(GameBox.Text),
            CustomInfo = Trim(InfoBox.Text),
            ServerAddress = Trim(ServerBox.Text),
            Icon = App.Services.VersionSettings.Get(_version.Id).Icon,
        };

        // ⚠️ min 不能大于 max：两边可能一个来自覆盖、一个来自默认
        //    （默认 max 2048 + 版本自定义 min 4096），不夹一下 JVM 会直接拒绝启动
        if (overrides is { MinMemory: { } lo, MaxMemory: { } hi } && lo > hi)
        {
            overrides = overrides with { MinMemory = hi, MaxMemory = lo };
        }

        if (!App.Services.VersionSettings.Update(_version.Id, overrides))
        {
            SaveHint.Text = Localizer.Tr("保存失败 —— 看看配置目录能不能写。{path}",
                ("path", VersionSettingsPath()));
            return;
        }

        RefreshSaveHint();
    }

    private static string? Trim(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private string SelectedJavaPath()
    {
        var index = JavaCombo.SelectedIndex;
        return index >= 0 && index < _javaPaths.Count ? _javaPaths[index] : string.Empty;
    }

    // ============================================================
    // 控件联动
    // ============================================================

    private void OnEditableTextChanged(object sender, TextChangedEventArgs e)
    {
        // 占位文案的显示与否由 HookPlaceholder 挂的另一个处理器管，这里只管保存
        ScheduleSave();
    }

    private void OnMemoryModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        SyncMemoryEnabled();
        ScheduleSave();
    }

    private void OnMinChanged(object? sender, int value)
    {
        if (_loading)
        {
            return;
        }
        // 1.0.0 的 _on_min_changed：最小超过最大时把最大一起抬上去
        var snapped = SystemMemory.SnapMemory(value);
        if (snapped > MaxSlider.Value)
        {
            MaxSlider.Value = snapped;
            MaxValue.Text = $"{CurrentMaxMb()} MB";
        }
        RefreshMemoryBar();
        ScheduleSave();
    }

    private void OnMaxChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }
        // 1.0.0 的 _on_max_changed：最大低于最小时把最小一起压下来
        var snapped = CurrentMaxMb();
        if (snapped < MinBox.Value)
        {
            MinBox.SetValueSilently(snapped);
        }
        MaxValue.Text = $"{snapped} MB";
        RefreshMemoryBar();
        ScheduleSave();
    }

    private void OnJavaChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _fillingJava)
        {
            return;
        }
        RefreshJavaHint();
        ScheduleSave();
    }

    private void OnAdvancedToggle(object sender, RoutedEventArgs e)
        => SetAdvanced(AdvancedBody.Visibility != Visibility.Visible);

    private void SetAdvanced(bool open)
    {
        AdvancedBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        AdvancedToggle.IsChecked = open;
        RefreshAdvancedToggle();
    }

    private void RefreshAdvancedToggle()
    {
        // 箭头 + 文字是拼出来的，所以不走 {loc:Tr}（1.0.0 的 _refresh_advanced_toggle）
        var arrow = AdvancedBody.Visibility == Visibility.Visible ? "\u25be" : "\u25b8";
        AdvancedToggle.Content = $"{arrow}   {Localizer.Tr("高级选项")}";
    }

    // ============================================================
    // Tab
    // ============================================================

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } && int.TryParse(tag, out var index))
        {
            SetTab(index);
        }
    }

    /// <summary>切 tab。1.0.0 是 QStackedWidget，这里用两块面板的可见性表达。</summary>
    public void SetTab(int index)
    {
        var mods = index == TabModsIndex;
        TabSettings.IsChecked = index == TabSettingsIndex;
        TabMods.IsChecked = mods;
        SettingsPane.Visibility = mods ? Visibility.Collapsed : Visibility.Visible;
        ModsPane.Visibility = mods ? Visibility.Visible : Visibility.Collapsed;
    }

    // ============================================================
    // 导航
    // ============================================================

    /// <summary>
    /// 「← 返回」：回到**进来的那一页**（1.0.0 的 <c>back_requested</c>，
    /// 由主窗口记着是从哪儿进来的）。主窗口没记过就退回版本列表。
    /// </summary>
    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.ShowPage(main.PreviousPageKey ?? "versions");
        }
    }

    /// <summary>「管理档案」→ 账户页（1.0.0 的 <c>accounts_requested</c>）。</summary>
    private void OnManageAccountClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.ShowPage("accounts");
        }
    }

    /// <summary>「版本选择」→ 版本页（1.0.0 的 <c>version_pick_requested</c>）。</summary>
    private void OnPickVersionClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.ShowPage("versions");
        }
    }

    /// <summary>
    /// 「转到下载页面」。
    ///
    /// ⚠️ 跟 1.0.0 的**唯一一处有意偏离**：1.0.0 按下去只冒一句
    ///    「下载和安装功能还没做（计划在 v0.4）」，因为那一版确实没做。
    ///    2.0 的下载页早就做好了 —— 再照抄那句话就是**说假话**，
    ///    而且按钮自己写的就是"转到下载页面"。所以这里真的切过去。
    ///    只有这一页没挂在主窗口上（切不了页）时才退回 1.0.0 那句兜底提示。
    /// </summary>
    private void OnGoDownloadClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.ShowPage("download");
            return;
        }
        ModTipText.Text = Localizer.Tr("这一页没有挂在主窗口上，切不了页面。");
        ModTipText.Visibility = Visibility.Visible;
    }

    // ============================================================
    // Java 扫描
    // ============================================================

    /// <summary>
    /// 重建 Java 下拉。逻辑照 1.0.0 的 <c>_fill_java_combo</c>：
    ///   · 第一项永远是「跟随全局设置」（对应覆盖项里没有 `java_path`）
    ///   · 然后每个能用的 Java 一项，显示 `Java {主版本} · {路径}`
    ///   · 存下来的那个**没扫到也列出来**（卸载了/挪走了），
    ///     不然一进这个页面就会被悄悄改成"跟随全局设置"
    /// </summary>
    private void FillJavaCombo()
    {
        _fillingJava = true;
        try
        {
            var saved = _version is null
                ? string.Empty
                : App.Services.VersionSettings.Get(_version.Id).JavaPath ?? string.Empty;
            var usable = App.Services.Java.Detected.Where(j => j.Usable).ToList();

            _javaPaths.Clear();
            JavaCombo.Items.Clear();

            _javaPaths.Add(string.Empty);
            JavaCombo.Items.Add(Localizer.Tr("跟随全局设置"));

            foreach (var java in usable)
            {
                _javaPaths.Add(java.Path);
                JavaCombo.Items.Add($"Java {java.Major} · {java.Path}");
            }

            if (saved.Length > 0 && !_javaPaths.Any(p => PathEquals(p, saved)))
            {
                _javaPaths.Add(saved);
                JavaCombo.Items.Add(Localizer.Tr("手动指定：{path}", ("path", saved)));
            }

            var index = 0;
            for (var i = 1; i < _javaPaths.Count; i++)
            {
                if (PathEquals(_javaPaths[i], saved))
                {
                    index = i;
                    break;
                }
            }
            JavaCombo.SelectedIndex = index;
        }
        finally
        {
            _fillingJava = false;
        }
    }

    private static bool PathEquals(string? a, string? b)
        => string.Equals(
            (a ?? string.Empty).Replace('/', '\\').TrimEnd('\\'),
            (b ?? string.Empty).Replace('/', '\\').TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 首次进这一页时如果还没有扫描结果，后台扫一遍（走缓存，通常几十毫秒）。
    ///
    /// ⚠️ 跟 1.0.0 的差别（跟设置页同一个取舍）：1.0.0 每次进这一页都强制
    ///    **忽略缓存**重扫（`QuickJavaScanTask(..., use_cache=False)`），代价是十几秒。
    ///    2.0 每次切页都会新建一个页面，照抄的话每点一次就起十几个 java 进程 ——
    ///    所以只在这一页**还没有数据**时自动扫，真要重扫由用户点「重新扫描」。
    /// </summary>
    private void EnsureJavaScanned()
    {
        if (_version is null || App.Services.Java.Detected.Count > 0 || _scanning)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await App.Services.Java.ScanAsync();
            }
            catch (Exception)
            {
                // 扫描是"锦上添花"，失败就保持原样
            }
            Dispatcher.Invoke(() =>
            {
                if (!_detached)
                {
                    FillJavaCombo();
                    RefreshJavaHint();
                }
            });
        });
    }

    private void OnRescanJavaClick(object sender, RoutedEventArgs e)
    {
        if (_scanning)
        {
            return;
        }
        _scanning = true;

        // 界面先改口，别让人以为没反应（真正的扫描要起十几个 java 进程，好几秒）
        RescanJavaButton.IsEnabled = false;
        RescanJavaButton.Content = Localizer.Tr("扫描中…");

        _ = Task.Run(async () =>
        {
            try
            {
                await App.Services.Java.ScanAsync();
            }
            catch (Exception)
            {
                // 同上：扫不到就保持原来的列表
            }
            Dispatcher.Invoke(() =>
            {
                _scanning = false;
                if (_detached)
                {
                    return;
                }
                RescanJavaButton.IsEnabled = true;
                RescanJavaButton.Content = Localizer.Tr("重新扫描");
                FillJavaCombo();
                RefreshJavaHint();
            });
        });
    }

    // ============================================================
    // 自动化截图的子段入口
    // ============================================================

    /// <summary>
    /// `--page versionsettings:&lt;子段&gt;`。
    ///
    /// 为什么需要它：这一页四张卡（启动选项 / 内存分配 / 服务器 / 高级选项）
    /// 加起来比 820 高的窗口高一截，离屏截图**只会截到最上面那一屏**；
    /// 而「高级选项」默认还是**收起**的（1.0.0 就是 `setVisible(False)`），
    /// 「Mod 管理」是另一个 tab —— 不给入口的话这三块永远没人验过。
    ///
    /// 传：
    ///   `launch` / `memory` / `server` / `advanced`   滚到某张卡（advanced 还会展开）
    ///   `mods`        切到「Mod 管理」tab（当前版本没加载器 → 提示卡）
    ///   `modsloader`  换成**带加载器**的版本再切过去（第一个加载器版本，列表可能是空的）
    ///   `modsloader:&lt;版本 id&gt;`  同上，但**指定**哪一个版本 —— 想截"真的有 mod 的
    ///                 那个版本"（比如 `1.20.4-Fabric_0.19.5`）时用这个
    /// </summary>
    public void ShowSection(string section)
    {
        var scrollTarget = section;
        switch (section)
        {
            case "mods":
                SetTab(TabModsIndex);
                scrollTarget = null;
                break;
            case string s when s.StartsWith("modsloader:", StringComparison.Ordinal):
                // 指定版本再切过去：截图想验的是"列表里有东西、图标读出来了"，
                // 而列出来的几个加载器版本可能只有一个真的装了 mod
                var wanted = s["modsloader:".Length..];
                Configure(App.Services.Games.Installed.FirstOrDefault(v => v.Id == wanted)
                          ?? App.Services.Games.Installed.FirstOrDefault(v => v.Kind == VersionKind.Loader));
                SetTab(TabModsIndex);
                scrollTarget = null;
                break;
            case "modsloader":
                // 挑一个带加载器的版本，好截到 _refresh_mods 的另一条分支
                Configure(App.Services.Games.Installed
                    .FirstOrDefault(v => v.Kind == VersionKind.Loader));
                SetTab(TabModsIndex);
                scrollTarget = null;
                break;
            case "advanced":
                SetAdvanced(true);
                break;
        }

        _pendingSection = scrollTarget;
        _scrollTries = 0;
    }

    /// <summary>
    /// 布局排完之后再滚。
    ///
    /// ⚠️ 不能挂在 `Loaded` 上：离屏截图**不 Show 窗口**，`Loaded` 不一定会来；
    ///    而 `LayoutUpdated` 在 App 手动 Measure/Arrange 那一趟之后一定会来
    ///    （跟设置页同一套理由）。
    /// </summary>
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_pendingSection is null)
        {
            return;
        }

        FrameworkElement? target = _pendingSection switch
        {
            "launch" => LaunchCard,
            "memory" => MemoryCard,
            "server" => ServerCard,
            "advanced" => AdvancedCard,
            _ => null,
        };

        // 展开「高级选项」会改变布局，一趟可能量不准 —— 允许重试几趟
        if (target is null || ++_scrollTries > 3)
        {
            _pendingSection = null;
            return;
        }

        try
        {
            // 直接把偏移量算出来滚过去，比 BringIntoView 可控
            // （后者会尽量"少滚一点"，卡片只露出一半，截图反而看不全）
            var offset = target.TransformToAncestor(SettingsPane)
                .Transform(new Point(0, 0)).Y;
            SettingsPane.ScrollToVerticalOffset(Math.Max(0, offset - 12));
            _pendingSection = null;
        }
        catch (InvalidOperationException)
        {
            // 目标还没进可视树（页面刚被换掉）—— 等下一趟 LayoutUpdated
        }
    }
}
