using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mosslight.Core.Localization;
using Mosslight.Core.Logging;
using Mosslight.Core.Services;

namespace Mosslight.App.Views;

/// <summary>「启动游戏」页。</summary>
public partial class LaunchPage : UserControl, IMosslightPage
{
    // 当前版本（版本页选中，或者冷启动时按 last_version 挑的）。
    private GameVersion? _version;

    // 这一局是不是正在启动。
    private bool _running;

    // 这次退出是不是用户点了「结束游戏」造成的（决定文案，不当崩溃报）。
    private bool _killRequested;

    public LaunchPage()
    {
        InitializeComponent();

        // 静态文案「启动游戏」「选择版本和档案，然后启动」「选择版本」「版本设置」
        // 都写在 XAML 里走 {loc:Tr}（同一套词典，漏翻只会退回中文）。
        PickInitialVersion();
        RefreshAccount();
        RefreshJavaStatus();     // 顺带把状态行填上（1.0.0 的 _start_java_scan）
        UpdatePanel();

        ShowScanErrors(App.Services.Games.ScanErrors);
    }

    /// <summary>这一页**不做入场动画**（返回 null = 不挂 PageAnimator）。</summary>
    public Panel? AnimationHost => null;

    // 冷启动：挑版本

    /// <summary>
    /// 冷启动时选哪个版本：配置里的 `last_version`，找不到就退回第一个；⚠️ 真引擎下这一步是
    /// **同步扫盘**（VersionScanner 在服务构造时就跑过了），所以这里不用 await。
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

    // 面板

    public void ForceRunningForScreenshot() => SetRunning(true);

    private void UpdatePanel()
    {
        VersionWarnBadge.Visibility = Visibility.Collapsed;

        if (_version is not { } version)
        {
            LaunchButton.IsEnabled = false;
            VersionSettingsButton.IsEnabled = false;
            LaunchSubtitleText.Text = Localizer.Tr("未找到本地版本");
            LaunchSubtitleText.Visibility = Visibility.Visible;
            LaunchButton.ToolTip = null;
            return;
        }

        // ⚠️ 运行中（_running）按钮必须保持可点：那一下是"结束游戏"。
        // 这里早期写的是 `!_running`，面板一刷新（切语言/换档案）红色按钮就被禁掉、
        // 文案也被 UpdateLaunchText 改回「启动游戏」—— 用户根本点不到"结束"。
        LaunchButton.IsEnabled = true;      // 走到这儿版本一定在（上面已经 return）
        VersionSettingsButton.IsEnabled = !_running;
        if (!_running)
        {
            UpdateLaunchText();
        }

        var problem = Problem(version);
        if (problem.Length > 0)
        {
            // 前缀固定两个空格，别 trim。
            VersionWarnText.Text = "\u26a0  " + problem;
            VersionWarnBadge.Visibility = Visibility.Visible;
        }
    }

    private void UpdateLaunchText()
    {
        // 图标是 XAML 里那个 Path（见 LaunchIcon），文字里不再塞 "▶"
        LaunchTitleText.Text = Localizer.Tr("启动游戏");
        LaunchIcon.Data = (Geometry)FindResource("Icon.Play");

        var name = _version?.Name ?? string.Empty;
        LaunchSubtitleText.Text = name;
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
    /// 版本信息那一行；⚠️ 2.0 没有只对本版本生效的内存设置，所以不补「（本版本）」。
    /// </summary>
    private static string MetaLine(GameVersion version)
    {
        var parts = new List<string> { JavaText(version) };

        // tr("内存 {n} MB", n=…) —— 占位符走 Localizer.Tr 的参数版，别自己拼字符串
        parts.Add(Localizer.Tr("内存 {n} MB", ("n", App.Settings.MaxMemory)));

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

    /// <summary>版本信息里的 Java 那段。</summary>
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

    /// <summary>这个版本现在起不来 / 可能有问题的原因，没有就返回空串。</summary>
    private static string Problem(GameVersion version)
    {
        if (!version.Complete)
        {
            return Localizer.Tr("这个版本启动不了：找不到客户端 jar");
        }

        if (App.Services.Java.Detected.Count == 0)
        {
            return string.Empty;
        }

        return App.Services.Java.PickFor(version) is null
            ? Localizer.Tr("这个版本没有可用的 Java，先去「设置」里指定一个")
            : string.Empty;
    }

    // 档案

    /// <summary>重贴「当前档案」；⚠️ 只读展示，不能做成输入框，否则可绕过档案名校验。</summary>
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

    // Java

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

    /// <summary>后台扫一遍 Java。</summary>
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
            UpdatePanel();
        }
        catch (Exception)
        {
            // 扫描是"锦上添花"：拿不到结果就不显示，不要弹窗也不要崩
        }
    }

    // 启动

    /// <summary>点「启动游戏」。</summary>
    private async void OnLaunchClick(object sender, RoutedEventArgs e)
    {
        // 已经跑起来了：这一次点击是「强制结束」，不再走启动流程
        if (_running)
        {
            // 记下"是我们杀的"：退出回调里据此给"已强制结束"，而不是"异常关闭"
            _killRequested = true;
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

        var account = App.Services.Accounts.Current;
        if (account is null)
        {
            SetStatus(Localizer.Tr("请先在「账户」页新建一个档案。"));
            return;
        }

        App.Settings.LastVersion = version.Id;

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
                LogBus.WriteLauncher(p.Message);
            });
            var result = await App.Services.Launch.LaunchAsync(request, progress);

            // 命令行记一条：出问题时这是唯一能看出"参数拼错没有"的东西
            if (!string.IsNullOrEmpty(result.CommandLine))
            {
                App.WriteLauncherLog($"启动 {version.Id}：{result.CommandLine}");
                LogBus.WriteLauncher(Localizer.Tr("启动参数：{cmd}", ("cmd", result.CommandLine)));
            }

            // 缺库 / 警告不拦启动，但必须逐条进日志窗口。
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

                // ⚠️ 打开日志窗口必须放在 WatchGameExit() 之后，否则会漏掉进程启动时的头几行输出。
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
    /// 游戏退出以后把按钮放开；⚠️ 这里**不是**自己轮询进程，而是等服务的事件：真实现在进程退出时
    /// 会触发 ILaunchService.GameExited。
    /// </summary>
    private void WatchGameExit()
    {
        // ⚠️ 每次启动都要新建 MinecraftLogReader；它是有状态的，跨进程复用会拼接上次未完成的事件。
        var reader = new MinecraftLogReader();

        void OnOutput(object? sender, string line)
        {
            foreach (var text in reader.Feed(line))
            {
                // ⚠️ 落盘和日志窗口**都走 App.WriteLog** —— 那里已经把 game.log
                // 镜像进 LogBus 了。这里再自己 LogBus.Write 一次的话，
                // 窗口里每一行游戏输出都会出现两遍（踩过）。
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
            // 退出码不直接给玩家看：0 是正常，非 0 可能是崩溃，也可能是我们自己点了
            // "结束游戏"（强杀拿到的就是 -1 这类）。用三种人话区分开。
            var killedByUs = _killRequested;
            _killRequested = false;
            var message = code == 0
                ? Localizer.Tr("游戏已正常退出")
                : killedByUs
                    ? Localizer.Tr("游戏已强制结束")
                    : Localizer.Tr("游戏异常关闭");
            LogBus.WriteLauncher(message);
            // 事件来自后台线程，回 UI 线程再碰控件
            Dispatcher.Invoke(() =>
            {
                SetRunning(false);
                // 只更新状态行，不弹任何错误框：异常关闭的详情在 Logs\game.log 里，
                // 不由启动器"吓"玩家一跳。
                SetStatus(message);
            });
        }

        // 游戏的原始输出落到 `Logs\game.log`，同时也进日志窗口。
        // ⚠️ 游戏原始输出不进状态行：那儿只放启动器自己的消息，英文输出会淹没信息。
        App.Services.Launch.GameOutput += OnOutput;
        App.Services.Launch.GameExited += OnExited;
    }

    /// <summary>
    /// 运行状态：跑起来时按钮换成红色的「结束游戏」（点 = 强杀进程树），退出后再换回绿色的「启动
    /// 游戏」并恢复可点。
    /// </summary>
    private void SetRunning(bool running)
    {
        _running = running;
        if (running)
        {
            LaunchButton.Style = (Style)FindResource("StopButton");
            LaunchButton.IsEnabled = true;          // 运行中要能点：那一下是"结束"
            LaunchButton.ToolTip = Localizer.Tr("点击强制结束游戏进程");
            // 播放 → 电源（lucide 的 power：圈 + 竖线，比手画的方块更像"结束"）
            LaunchIcon.Data = (Geometry)FindResource("Icon.Power");
            LaunchTitleText.Text = Localizer.Tr("结束游戏");
            LaunchSubtitleText.Text = Localizer.Tr("点击强制结束游戏进程");
            LaunchSubtitleText.Visibility = Visibility.Visible;
        }
        else
        {
            LaunchButton.Style = (Style)FindResource("LaunchButton");
            LaunchButton.IsEnabled = _version is not null;
            LaunchButton.ToolTip = null;
            LaunchIcon.Data = (Geometry)FindResource("Icon.Play");
            UpdateLaunchText();
        }

        // 跑起来以后别让人换版本 / 改设置：那些改的是"下一次"的参数，容易看糊涂
        ChooseVersionButton.IsEnabled = !running;
        VersionSettingsButton.IsEnabled = !running && _version is not null;
    }

    /// <summary>这次启动用的 .minecraft 目录。</summary>
    private static string MinecraftDir()
    {
        var configured = App.Settings.MinecraftDir;
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ".minecraft")
            : configured;
    }

    // 两个次要按钮

    /// <summary>「选择版本」。</summary>
    private void OnChooseVersionClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.ShowPage("versions");
            return;
        }
        SetStatus(Localizer.Tr("「版本」页还没搬过来 —— 版本列表还看不了。"));
    }

    /// <summary>「版本设置」。</summary>
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

    // 扫描告警条

    /// <summary>
    /// 显示"N 个版本读不了"的告警条；⚠️ 现在没有任何地方调用它：`IGameService.RefreshAsync` 只返
    /// 回版本列表，没有"哪些读不了"。
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

    // 状态行

    private void SetStatus(string text)
    {
        StatusLabel.Text = text;
        StatusLabel.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>空闲时的状态。</summary>
    private void SetIdleStatus(string text)
        => SetStatus(App.Services.Launch.IsPrototype ? text + PrototypeNote : text);

    private static string PrototypeNote => Localizer.Tr("（原型数据：启动引擎还没接）");
}
