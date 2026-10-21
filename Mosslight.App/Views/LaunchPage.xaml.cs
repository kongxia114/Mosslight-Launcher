using System.IO;
using System.Windows;
using System.Windows.Controls;
using Mosslight.Core.Localization;
using Mosslight.Core.Logging;
using Mosslight.Core.Services;

namespace Mosslight.App.Views;

/// <summary>
/// 「启动游戏」页。
///
/// 移植自 1.0.0 的 `ui/pages/home_page.py` —— 版式、文案、间距、面板里的顺序
/// 都照那份源码抄，没有"顺手重新设计"。这一页**只有右边那块启动面板**
/// （版本列表在 1.0.0 里已经搬去「版本」页了，同一份列表不在两个页面各摆一个）。
///
/// ## 跟 1.0.0 有意不同的三处
/// 1. **不弹对话框**。那边找不到档案 / 缺 Java / 启动失败都弹 QMessageBox；
///    2.0 统一落在状态行上。理由：原型的对话框会挡住截图，而且"弹窗告诉你不行"
///    比"状态行一直写着不行"更烦人。
/// 2. **运行中按钮就是「结束游戏」**：绿色「启动游戏」换成红色「结束游戏」，
///    把一个点不动的按钮写成「停止游戏」等于许一个做不到的承诺 ——
///    所以运行期间只是把按钮禁掉，文字仍然是「启动游戏」。
/// 3. **「选择版本」「版本设置」不导航**，而是在状态行里明说那两页还没搬
///    （1.0.0 里它们分别跳「版本」页和「版本设置」页）。
/// </summary>
public partial class LaunchPage : UserControl, IMosslightPage
{
    /// <summary>当前版本（版本页选中，或者冷启动时按 last_version 挑的）。</summary>
    private GameVersion? _version;

    /// <summary>这一局是不是正在启动。跑起来以后别让人换版本 —— 那改的是"下一次"的参数。</summary>
    private bool _running;

    public LaunchPage()
    {
        InitializeComponent();

        // 静态文案「启动游戏」「选择版本和档案，然后启动」「选择版本」「版本设置」
        // 都写在 XAML 里走 {loc:Tr}（同一套词典，漏翻只会退回中文）。
        PickInitialVersion();
        RefreshAccount();
        RefreshJavaStatus();     // 顺带把状态行填上（1.0.0 的 _start_java_scan）
        UpdatePanel();

        // 设计说明见 docs/design-notes.md#mosslight-app-views-launchpage-xaml-cs
        ShowScanErrors(App.Services.Games.ScanErrors);
    }

    /// <summary>
    /// 这一页**不做入场动画**（返回 null = 不挂 PageAnimator）。
    ///
    /// ⚠️ 这是照搬，不是省事：1.0.0 的入场动画（`ui/widgets/slide_in.py`）只用在
    /// **版本列表**（`collapsible_group` 展开出来的行）和**模组页的两个列表**上；
    /// `home_page.py` 就是普通 QFrame 卡片直接进布局，一帧动画都没有 ——
    /// 这页只有一块面板，让它滑进来反而比 1.0.0 多出一个动作。
    /// PageAnimator 是给列表页准备的。
    /// </summary>
    public Panel? AnimationHost => null;

    // ---------- 冷启动：挑版本 ----------

    /// <summary>
    /// 冷启动时选哪个版本：配置里的 `last_version`，找不到就退回第一个。
    ///
    /// ⚠️ 真引擎下这一步是**同步扫盘**（VersionScanner 在服务构造时就跑过了），
    ///    所以这里不用 await。扫盘本身很轻（只读几个 json），重的是 `java -version`
    ///    那一步 —— 那个已经在 <see cref="ScanJavaAsync"/> 里异步跑了。
    /// </summary>
    private void PickInitialVersion()
    {
        var versions = App.Services.Games.Installed;
        if (versions.Count == 0)
        {
            _version = null;
            return;
        }

        var wanted = App.Settings.LastVersion;
        _version = versions.FirstOrDefault(v => v.Id == wanted) ?? versions[0];
    }

    /// <summary>
    /// 重新扫一遍版本目录（版本页装完新版本、或者用户在设置里改了游戏目录之后调）。
    /// </summary>
    public async Task ReloadVersionsAsync()
    {
        try
        {
            await App.Services.Games.RefreshAsync();
            PickInitialVersion();
            UpdatePanel();
            ShowScanErrors(App.Services.Games.ScanErrors);
        }
        catch (Exception)
        {
            // 扫不动就保持原样 —— 不该因此崩页面
        }
    }

    // ---------- 面板 ----------

    /// <summary>
    /// 重贴面板。对应 1.0.0 的 `_update_panel()` ——
    /// 版本变了、Java 扫完了、设置改过了都要调它。
    /// </summary>
    /// <summary>
    /// 截图用：把页面直接摆成"游戏运行中"（红色结束按钮）。
    /// 由 MainWindow 在页面挂到 `PageHost` 之后调用 —— 构造期调会被面板刷新覆盖。
    /// </summary>
    public void ForceRunningForScreenshot() => SetRunning(true);

    private void UpdatePanel()
    {
        VersionWarnBadge.Visibility = Visibility.Collapsed;

        if (_version is not { } version)
        {
            // 1.0.0：没有版本时名字显示「未找到本地版本」，启动和版本设置都点不动
            LaunchButton.IsEnabled = false;
            VersionSettingsButton.IsEnabled = false;
            LaunchSubtitleText.Text = Localizer.Tr("未找到本地版本");
            LaunchSubtitleText.Visibility = Visibility.Visible;
            LaunchButton.ToolTip = null;
            return;
        }

        // ⚠️ 运行中（_running）按钮必须保持可点：那一下是"结束游戏"。
        //   这里早期写的是 `!_running`，面板一刷新（切语言/换档案）红色按钮就被禁掉、
        //   文案也被 UpdateLaunchText 改回「启动游戏」—— 用户根本点不到"结束"。
        LaunchButton.IsEnabled = true;      // 走到这儿版本一定在（上面已经 return）
        VersionSettingsButton.IsEnabled = !_running;
        if (!_running)
        {
            UpdateLaunchText();
        }

        var problem = Problem(version);
        if (problem.Length > 0)
        {
            // 1.0.0 的前缀是两个空格：`"⚠  " + problem`
            VersionWarnText.Text = "\u26a0  " + problem;
            VersionWarnBadge.Visibility = Visibility.Visible;
        }
    }

    /// <summary>设计说明见 docs/design-notes.md#mosslight-app-views-launchpage-xaml-cs</summary>
    private void UpdateLaunchText()
    {
        // 图标用几何符号而不是 emoji：emoji 在缺字体的机器上会变成豆腐块
        LaunchTitleText.Text = $"\u25b6    {Localizer.Tr("启动游戏")}";

        var name = _version?.Name ?? string.Empty;
        LaunchSubtitleText.Text = name;
        // 没有版本时不占位 —— 不然按钮里会留一条空白（1.0.0 的 set_text）
        LaunchSubtitleText.Visibility = name.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 原来面板上那三行都收进 ToolTip：鼠标停一下就能看到，
        // 不用在面板上常年占三行（用户："下面的一个还有啥用呢？"）
        if (_version is null)
        {
            LaunchButton.ToolTip = null;
            return;
        }

        var lines = new List<string> { name };
        var meta = MetaLine(_version);
        if (meta.Length > 0)
        {
            lines.Add(meta);
        }
        var path = string.IsNullOrEmpty(_version.Path) ? _version.Id : _version.Path;
        if (!string.IsNullOrEmpty(path))
        {
            lines.Add(path);
        }
        LaunchButton.ToolTip = string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 版本信息那一行。1.0.0 的 `_meta_line()`：几段用 `"    ·    "`（四个空格 +
    /// 间隔号 + 四个空格）拼起来，不是逗号也不是竖线。
    ///
    /// ⚠️ 1.0.0 还会补一段「（本版本）」：这个版本自己覆盖过内存设置时才加 ——
    ///    2.0 还没有"只对这个版本生效"的那一层设置（全走 AppSettings.MaxMemory），
    ///    所以仍然不编。「版本隔离」现在**有了**（扫描器认得出来），接上。
    /// </summary>
    private static string MetaLine(GameVersion version)
    {
        var parts = new List<string> { JavaText(version) };

        // tr("内存 {n} MB", n=…) —— 占位符走 Localizer.Tr 的参数版，别自己拼字符串
        parts.Add(Localizer.Tr("内存 {n} MB", ("n", App.Settings.MaxMemory)));

        // 加载器：1.0.0 用 version["loader_label"]（"Fabric 0.15.7" 这种带版本号的），
        // 原版不显示；2.0 的 GameVersion.Loader 只有加载器名，所以原版之外直接写名字。
        if (!string.IsNullOrEmpty(version.Loader) && version.Loader != "Vanilla")
        {
            parts.Add(version.Loader);
        }

        // 版本隔离（存档、mods 都在版本目录里，而不是 .minecraft 下）
        if (version.Isolated)
        {
            parts.Add(Localizer.Tr("版本隔离"));
        }

        return string.Join("    ·    ", parts);
    }

    /// <summary>
    /// 版本信息里的 Java 那段。
    ///
    /// 1.0.0 读的是版本 JSON 里的 `javaVersion.majorVersion`，读不到才退回
    /// 「Java 8（默认）」——那句注释写得很清楚："不是读不到，是那个年代还没有
    /// javaVersion 这个概念"。真引擎的 `RequiredMajor` 就是照这个实现的，
    /// 所以这里显示"这个版本**要求**几"、"挑中的是几"。
    /// </summary>
    private static string JavaText(GameVersion version)
    {
        var java = App.Services.Java.PickFor(version);
        if (java is null)
        {
            return Localizer.Tr("找不到 Java");
        }

        var required = App.Services.Java.RequiredMajor(version);
        // 挑中的正好是要求那个大版本 → 只写一条；否则把要求也写出来
        // （"没有 Java 21，用 Java 17 顶替"这种情况用户必须看得见）
        if (java.Major == required)
        {
            return java.IsDefault
                ? Localizer.Tr("Java {n}（默认）", ("n", java.Version))
                : $"Java {java.Version}";
        }

        return java.IsDefault
            ? Localizer.Tr("Java {n}（默认，要求 {need}）", ("n", java.Version), ("need", required))
            : Localizer.Tr("Java {n}（要求 {need}）", ("n", java.Version), ("need", required));
    }

    /// <summary>
    /// 这个版本现在起不来 / 可能有问题的原因，没有就返回空串。
    ///
    /// 1.0.0 这里查 `version["complete"]`（缺客户端 jar →「这个版本启动不了」）。
    /// 真引擎的扫描器已经把 `Complete` 算出来了 ——
    /// 判据是"自己目录里有 jar，**或者**能顺着 inheritsFrom 继承"，
    /// 所以加载器版本（jar 在父版本里）不会被误判成坏的。
    /// </summary>
    private static string Problem(GameVersion version)
    {
        if (!version.Complete)
        {
            return Localizer.Tr("这个版本启动不了：找不到客户端 jar");
        }

        // Java 还没扫出来的时候不报错：1.0.0 也是等扫描结果回来才重贴面板的
        if (App.Services.Java.Detected.Count == 0)
        {
            return string.Empty;
        }

        return App.Services.Java.PickFor(version) is null
            ? Localizer.Tr("这个版本没有可用的 Java，先去「设置」里指定一个")
            : string.Empty;
    }

    // ---------- 档案 ----------

    /// <summary>
    /// 重贴「当前档案」那一行。
    ///
    /// ⚠️ 这一行是**只读展示**，不是输入框：1.0.0 专门改过这一点 ——
    /// 以前主窗口把账户名回填到一个 QLineEdit，而启动时读的又是输入框里的文本，
    /// 用户可以随便改成非法名字、绕过新建档案时的正则校验。现在名字一律来自
    /// AccountManager（见 2.0 的 LaunchRequest.Account）。
    ///
    /// 「账户」页搬过来以后，建完档案调这个方法，面板和侧边栏卡片会一起更新。
    /// </summary>
    public void RefreshAccount()
    {
        // 账号只在左侧栏底部那张卡片上显示（同一件事不留两个门）；
        // 这里保留方法是因为"建完档案要刷新侧边栏"这件事还得有人干。
        var account = App.Services.Accounts.Current;
        _ = account;

        // 侧边栏底部那张卡片是主窗口画的，它自己不知道档案变了 —— 回头喊一声。
        // 页面刚建好时还没挂进视觉树，Window.GetWindow 会是 null，所以必须判空。
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.RefreshAccountChip();
        }
    }

    // ---------- Java ----------

    /// <summary>设计说明见 docs/design-notes.md#mosslight-app-views-launchpage-xaml-cs</summary>
    private void RefreshJavaStatus()
    {
        var detected = App.Services.Java.Detected;
        if (detected.Count > 0)
        {
            SetIdleStatus(string.Empty);
            return;
        }

        // 还没扫过（真实现里 `java -version` 每个都要起一次进程，有点慢）。
        // 先把话说出去，免得界面看着像卡住了，扫完再改口。
        SetIdleStatus(Localizer.Tr("正在查找 Java…"));
        _ = ScanJavaAsync();
    }

    /// <summary>后台扫一遍 Java。失败就算了 —— 状态行留在「正在查找 Java…」，不该因此崩页面。</summary>
    private async Task ScanJavaAsync()
    {
        try
        {
            var javas = await App.Services.Java.ScanAsync();
            if (javas.Count > 0)
            {
                // 有 Java 就闭嘴（见 RefreshJavaStatus 的注释）
                SetIdleStatus(string.Empty);
            }
            else
            {
                SetIdleStatus(Localizer.Tr("没找到可用的 Java —— 去「设置 → Java」里指定一个"));
            }
            // 扫完了才知道某个版本有没有 Java 可用，面板要跟着变（1.0.0 的 _on_java_scan_done）
            UpdatePanel();
        }
        catch (Exception)
        {
            // 扫描是"锦上添花"：拿不到结果就不显示，不要弹窗也不要崩
        }
    }

    // ---------- 启动 ----------

    /// <summary>
    /// 点「启动游戏」。
    ///
    /// 走的是 `ILaunchService.LaunchAsync`，阶段消息（`LaunchProgress.Message`）
    /// 和最后的失败信息**都显示在状态行上**，一个弹窗都不弹。
    /// 原型实现会明确返回失败 —— 这里照实显示，不假装成功。
    /// </summary>
    private async void OnLaunchClick(object sender, RoutedEventArgs e)
    {
        // 已经跑起来了：这一次点击是「强制结束」，不再走启动流程
        if (_running)
        {
            SetStatus(Localizer.Tr("正在结束游戏…"));
            // Stop() 内部是 process.Kill(entireProcessTree: true)：
            // 连 Java 拉起来的子进程一起收掉；进程已经没了也不会抛
            App.Services.Launch.Stop();
            if (!App.Services.Launch.IsRunning)
            {
                SetRunning(false);
            }
            return;
        }

        var version = _version;
        if (version is null)
        {
            return;
        }

        // 1.0.0 在这儿弹「还没有档案」；2.0 把这句话放进状态行
        var account = App.Services.Accounts.Current;
        if (account is null)
        {
            SetStatus(Localizer.Tr("请先在「账户」页新建一个档案。"));
            return;
        }

        // 记住这次的版本，下次打开直接选中它（1.0.0 的 config.set("last_version", …)）
        App.Settings.LastVersion = version.Id;

        // 分辨率：1.0.0 默认 854×480（游戏自己的默认值），配置里改过就用配置的。
        // 内存上下限都传 —— 只传 -Xmx 的话 JVM 会拿 -Xms 的默认值（物理内存的 1/64）起堆，
        // 大内存机器上开局就申请一大块，反而更慢。
        var request = new LaunchRequest(
            Version: version,
            Account: account,
            Java: App.Services.Java.PickFor(version),
            MaxMemoryMb: App.Settings.MaxMemory,
            MinecraftDir: MinecraftDir(),
            MinMemoryMb: Math.Min(App.Settings.MinMemory, App.Settings.MaxMemory),
            Width: App.Settings.GetInt("window_width", 854),
            Height: App.Settings.GetInt("window_height", 480),
            ExtraJvmArgs: App.Settings.GetString("jvm_args") ?? string.Empty,
            ExtraGameArgs: App.Settings.GetString("game_args") ?? string.Empty,
            // 版本目录用扫描器记下的真实路径 —— 目录名和 json 里的 id 不一定一致
            VersionDir: string.IsNullOrEmpty(version.Path) ? null : version.Path);

        SetRunning(true);
        SetStatus(Localizer.Tr("准备启动 {name}", ("name", version.Name)));

        try
        {
            // Progress<T> 是在 UI 线程上建的，回调会自动回到 UI 线程，不用自己 Dispatcher
            var progress = new Progress<LaunchProgress>(p =>
            {
                SetStatus(p.Message);
                // 1.0.0 是"每一条带 [启动器] 前缀的消息都同时进日志窗口 + 状态行"
                // （见那边 home_page.append_log）。所以这里也记一份 ——
                // 状态行只看得到最后一句，日志窗口里才留得住整条时间线。
                LogBus.WriteLauncher(p.Message);
            });
            var result = await App.Services.Launch.LaunchAsync(request, progress);

            // 命令行记一条：出问题时这是唯一能看出"参数拼错没有"的东西
            // （1.0.0 也是把 describe() 打进日志窗口的）
            if (!string.IsNullOrEmpty(result.CommandLine))
            {
                App.WriteLauncherLog($"启动 {version.Id}：{result.CommandLine}");
                LogBus.WriteLauncher(Localizer.Tr("启动参数：{cmd}", ("cmd", result.CommandLine)));
            }

            // 缺库 / 警告这类**不拦启动但必须让人看见**的话，1.0.0 是一条条 append 进日志窗口的
            foreach (var warning in result.Warnings ?? [])
            {
                LogBus.WriteLauncher("\u26a0 " + warning);
            }
            var missing = result.MissingLibraries ?? [];
            if (missing.Count > 0)
            {
                LogBus.WriteLauncher(Localizer.Tr(
                    "⚠ 本地缺 {n} 个库，游戏可能起不来：{first}",
                    ("n", missing.Count), ("first", missing[0])));
            }

            if (result.Ok)
            {
                SetStatus(result.Message);
                LogBus.WriteLauncher(result.Message);
                // 进程起来以后按钮保持禁用，等 GameExited 再恢复
                // （真引擎的 IsRunning 才是权威，见 SetRunning 的注释）
                WatchGameExit();

                // ---- 那个"死开关"接活的地方 ----
                //
                // 设置页的「启动游戏后自动打开日志窗口」写的是 `show_log_window`
                // （1.0.0 core/config.py 的 DEFAULT_CONFIG 键名，默认 true）。
                // 2.0 之前这个开关**存了没人读** —— 用户勾上、写了盘、然后什么都不发生。
                //
                // ⚠️ 位置放在 WatchGameExit() **之后**：窗口一开就订阅了 LogBus，
                //    这几行之后游戏输出才开始进来；反过来的话开窗那一刻
                //    会先漏掉进程启动的头几行（"游戏已启动"这类最该看到的）。
                if (App.Settings.GetBool("show_log_window", true))
                {
                    var window = App.OpenLogWindow();
                    LogBus.WriteLauncher(Localizer.Tr("已打开日志窗口（可在设置里关掉）"));
                    _ = window;
                }
            }
            else
            {
                SetStatus(result.Message);
                LogBus.WriteLauncher(result.Message);
            }
        }
        finally
        {
            if (!App.Services.Launch.IsRunning)
            {
                SetRunning(false);
            }
        }
    }

    /// <summary>
    /// 游戏退出以后把按钮放开。
    ///
    /// ⚠️ 这里**不是**自己轮询进程，而是等服务的事件：真实现在进程退出时
    ///    会触发 <see cref="ILaunchService.GameExited"/>。直接轮询的话，
    ///    页面被销毁（切页/关窗）之后回调会打到已经不存在的控件上。
    /// </summary>
    private void WatchGameExit()
    {
        // ---- 游戏输出：一边落盘、一边喂给日志窗口 ----
        //
        // ⚠️ `MinecraftLogReader` 必须**每次启动新建一个**：它是有状态的
        //    （一条 log4j 事件在控制台里是 3 行，得攒齐了才能还原），
        //    跨进程复用会把上一次没收完的半条事件拼到这一次的输出上。
        var reader = new MinecraftLogReader();

        void OnOutput(object? sender, string line)
        {
            foreach (var text in reader.Feed(line))
            {
                // ⚠️ 落盘和日志窗口**都走 App.WriteLog** —— 那里已经把 game.log
                //    镜像进 LogBus 了。这里再自己 LogBus.Write 一次的话，
                //    窗口里每一行游戏输出都会出现两遍（踩过）。
                App.WriteLog("game.log", text);
            }
        }

        void OnExited(object? sender, int code)
        {
            // 进程断了但还有半条事件没攒齐 —— 原样吐出来，不吞内容
            foreach (var text in reader.Flush())
            {
                App.WriteLog("game.log", text);
            }

            App.Services.Launch.GameExited -= OnExited;
            App.Services.Launch.GameOutput -= OnOutput;
            LogBus.WriteLauncher(Localizer.Tr("游戏已退出（代码 {code}）", ("code", code)));
            // 事件来自后台线程，回 UI 线程再碰控件
            Dispatcher.Invoke(() =>
            {
                SetRunning(false);
                SetStatus(Localizer.Tr("游戏已退出（代码 {code}）", ("code", code)));
            });
        }

        // 游戏的原始输出落到 `Logs\game.log`，同时也进日志窗口。
        // ⚠️ **不进状态行** —— 1.0.0 的注释说得很清楚：那儿只放启动器自己的消息，
        //    游戏原始输出会变成一行跳动的英文，反而什么信息都读不出来。
        App.Services.Launch.GameOutput += OnOutput;
        App.Services.Launch.GameExited += OnExited;
    }

    /// <summary>
    /// 运行状态：跑起来时按钮换成红色的「结束游戏」（点 = 强杀进程树），
    /// 退出后再换回绿色的「启动游戏」并恢复可点。
    /// </summary>
    private void SetRunning(bool running)
    {
        _running = running;
        if (running)
        {
            LaunchButton.Style = (Style)FindResource("StopButton");
            LaunchButton.IsEnabled = true;          // 运行中要能点：那一下是"结束"
            LaunchButton.ToolTip = Localizer.Tr("点击强制结束游戏进程");
            LaunchTitleText.Text = Localizer.Tr("结束游戏");
            LaunchSubtitleText.Text = Localizer.Tr("点击强制结束游戏进程");
            LaunchSubtitleText.Visibility = Visibility.Visible;
        }
        else
        {
            LaunchButton.Style = (Style)FindResource("LaunchButton");
            LaunchButton.IsEnabled = _version is not null;
            LaunchButton.ToolTip = null;
            UpdateLaunchText();
        }

        // 跑起来以后别让人换版本 / 改设置：那些改的是"下一次"的参数，容易看糊涂
        ChooseVersionButton.IsEnabled = !running;
        VersionSettingsButton.IsEnabled = !running && _version is not null;
    }

    /// <summary>
    /// 这次启动用的 .minecraft 目录。
    /// 配置里空着 = 用默认的 `%APPDATA%\.minecraft`（1.0.0 的
    /// `config.get_minecraft_dir()` 同样会退回默认路径，而不是把一个空字符串交出去）。
    /// </summary>
    private static string MinecraftDir()
    {
        var configured = App.Settings.MinecraftDir;
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ".minecraft")
            : configured;
    }

    // ---------- 两个次要按钮 ----------

    /// <summary>
    /// 「选择版本」。1.0.0 里它请主窗口切到「版本」页（本页没有列表）。
    /// 2.0 的版本页已经做好了，所以这里**真的切过去**。
    /// </summary>
    private void OnChooseVersionClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.ShowPage("versions");
            return;
        }
        SetStatus(Localizer.Tr("「版本」页还没搬过来 —— 版本列表还看不了。"));
    }

    /// <summary>
    /// 「版本设置」。1.0.0 里它跳「版本设置」页（Java / 内存 / JVM 参数 / 游戏参数）。
    /// 2.0 那一页已经做好了，所以这里**真的切过去**，并把当前选中的版本告诉它。
    /// </summary>
    private void OnVersionSettingsClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        main.ShowPage("versionsettings");
        if (_version is not null && main.PageHost.Content is VersionSettingsPage settings)
        {
            settings.Configure(_version);
        }
    }

    // ---------- 扫描告警条 ----------

    /// <summary>
    /// 显示"N 个版本读不了"的告警条（1.0.0 的 `_update_banner()`）。
    ///
    /// ⚠️ 现在没有任何地方调用它：`IGameService.RefreshAsync` 只返回版本列表，
    ///    没有"哪些读不了"。留这个方法是为了接口在 —— 扫描器哪天把错误吐出来，
    ///    接上就能长出 1.0.0 那条告警。
    /// </summary>
    public void ShowScanErrors(IReadOnlyList<string> errors)
    {
        if (errors.Count == 0)
        {
            ScanWarningBanner.Visibility = Visibility.Collapsed;
            BodyRow.Margin = new Thickness(0, 18, 0, 0);
            return;
        }

        var extra = errors.Count > 1
            ? Localizer.Tr("（另有 {n} 条）", ("n", errors.Count - 1))
            : string.Empty;
        ScanWarningText.Text = Localizer.Tr(
            "⚠  {n} 个版本读不了：{first}{extra}",
            ("n", errors.Count), ("first", errors[0]), ("extra", extra));
        ScanWarningBanner.Visibility = Visibility.Visible;
        // 告警条展开以后，副标题到面板的那段间距由它头顶的 18 接手，
        // 它自己下面只剩 Qt 的 spacing(6) —— 不跟着改的话会多空 12px
        BodyRow.Margin = new Thickness(0, 6, 0, 0);
    }

    // ---------- 状态行 ----------

    /// <summary>
    /// 状态行。1.0.0 的注释：这儿只放**启动器自己**的消息，游戏原始输出不进来 ——
    /// 不然会变成一行跳动的英文。
    /// </summary>
    /// <summary>状态行：没字就把那一行收起来（不留一条空白的外边距）。</summary>
    private void SetStatus(string text)
    {
        StatusLabel.Text = text;
        StatusLabel.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 空闲时的状态。后面挂一句原型说明 ——
    /// ⚠️ 一个"看着能启动游戏、其实什么都不发生"的按钮比一个明说没接上的按钮糟糕得多，
    ///    所以这句话必须在界面上。接上真引擎后 `IsPrototype` 变 false，它会自己消失。
    /// </summary>
    private void SetIdleStatus(string text)
        => SetStatus(App.Services.Launch.IsPrototype ? text + PrototypeNote : text);

    /// <summary>原型说明。放在状态行里，不动版式、不抢注意力。</summary>
    private static string PrototypeNote => Localizer.Tr("（原型数据：启动引擎还没接）");
}
