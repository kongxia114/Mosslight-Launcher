using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mosslight.App.Infrastructure;
using Mosslight.Core.Localization;
using Mosslight.Core.Logging;
using Mosslight.Core.Services;
using Mosslight.Core.Settings;
using Mosslight.Core.Theme;

namespace Mosslight.App;

public partial class App : Application
{
    /// <summary>全局配置（全程序一份）。</summary>
    public static AppSettings Settings { get; private set; } = null!;

    /// <summary>全局服务（原型阶段全是假实现）。</summary>
    public static LauncherServices Services { get; private set; } = null!;

    /// <summary>收藏夹（`&lt;配置目录&gt;/favorites.json`）。</summary>
    public static Mosslight.Core.Services.FavoritesStore Favorites { get; private set; } = null!;

    /// <summary>进程启动目录（配置、语言文件都相对它找）。</summary>
    public static string BaseDir { get; private set; } = string.Empty;

    /// <summary>「离屏截图模式」（命令行给了 `--screenshot`）。</summary>
    public static bool ScreenshotMode { get; private set; }

    /// <summary>这个 exe 是不是"从编译器里跑起来的开发版"。</summary>
    public static bool IsDevelopmentBuild { get; } = DetectDevelopmentBuild();

    private static bool DetectDevelopmentBuild()
    {
#if DEBUG
        return true;
#else
        return Debugger.IsAttached;
#endif
    }

    /// <summary>`--notice-body` 给的正文（只有 `--page debug:notice` 截图时会用）。</summary>
    public static string? ScreenshotNoticeBody { get; private set; }

    /// <summary>
    /// `--page` 指定的那个 key（离屏截图专用，见 `MainWindow.ScreenshotTarget`）。
    /// </summary>
    public static string? ScreenshotPageKey { get; private set; }

    /// <summary>注册表里存的语言代码认不认识（认不出来当没有，走默认解析）。</summary>
    private static string? SupportedLanguage(string? code)
        => code is not null && Localizer.LanguageNames.ContainsKey(code) ? code : null;

    /// <summary>环境变量 `MOSSLIGHT_DISABLE_DEBUG_HINT` 给了任意值 → 不再弹开发版提示。</summary>
    public static void OnMainWindowReady(MainWindow window)
    {
        if (ScreenshotMode)
        {
            return;                     // 截图模式不联网、不弹窗
        }

        var dispatcher = window.Dispatcher;
        _ = Task.Run(async () =>
        {
            await HandleStartupUpdateAsync(window, dispatcher).ConfigureAwait(false);
            await CheckUpdatesAsync(window, dispatcher).ConfigureAwait(false);
        });
    }

    private static Version CurrentVersion()
        => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
           ?? new Version(0, 0, 0, 0);

    /// <summary>把 `2.11.0.0` 这种四段版本号显示成 `v2.11.0`。</summary>
    private static string DisplayVersion(string version)
    {
        if (!Version.TryParse(version, out var parsed))
        {
            return version;
        }
        // 发布版本永远是三段（2.11.0），Revision 那一截不进界面
        return $"v{parsed.Major}.{parsed.Minor}.{Math.Max(0, parsed.Build)}";
    }

    /// <summary>
    /// 启动时的"更新完成"提示，两条路；⚠️ 整个方法在后台线程跑；弹窗一律 `Dispatcher.Invoke` 回
    /// UI 线程。
    /// </summary>
    private static async Task HandleStartupUpdateAsync(
        MainWindow window, System.Windows.Threading.Dispatcher dispatcher)
    {
        try
        {
            var manager = new Core.Updates.UpdateManager(BaseDir, CurrentVersion(), WriteLauncherLog);

            // 启动先清旧缓存：残留的 zip / 解压一半的目录会串档
            if (!manager.TryClearUpdateCache(out var cleanError))
            {
                WriteLauncherLog($"清更新缓存失败（忽略）：{cleanError}");
            }

            // ⓪ 上次替换失败了吗（脚本写了标记）
            var failure = Core.Updates.UpdateManager.ReadErrorMarker(manager.ErrorMarkerPath);
            if (failure is not null)
            {
                // bat 只写 ASCII 错误码，文案在这里翻（bat 里不能有中文）
                var failureText = failure switch
                {
                    "MOVE_FAILED" => Updates.UpdateLang.Get("warning_locked"),
                    "NEW_EXE_MISSING" => Updates.UpdateLang.Get("warning_missing"),
                    _ => failure,
                };
                WriteLauncherLog($"上次自动更新失败：{failure} → {failureText}");
                dispatcher.Invoke(() =>
                {
                    var dialog = Updates.UpdateWindow.CreateWarning(failureText, () =>
                        Core.Updates.UpdateManager.DeleteErrorMarker(manager.ErrorMarkerPath));
                    dialog.Owner = window;      // 工厂方法返回的是现成窗口，不能接对象初始化器
                    dialog.ShowDialog();
                });
                return;
            }

            // ① 待展示缓存
            var pending = Core.Updates.UpdateManager.ReadPendingUpdate(manager.PendingFilePath);
            if (pending is not null)
            {
                var version = DisplayVersion(pending.Version);
                WriteLauncherLog($"检测到刚更新完：{pending.Tag}（{version}）");

                var notes = string.IsNullOrWhiteSpace(pending.Notes)
                    ? await manager.FetchNotesAsync(pending.Tag).ConfigureAwait(false)
                    : pending.Notes;

                dispatcher.Invoke(() =>
                {
                    var dialog = new Updates.UpdateWindow(version, notes, () =>
                    {
                        Core.Updates.UpdateManager.DeletePendingUpdate(manager.PendingFilePath);
                        RegistryStore.WriteString("Update", "LastRunVersion", CurrentVersion().ToString());
                        WriteLauncherLog("已删除 pending_update.json（这次更新只提示一遍）");
                    })
                    {
                        Owner = window,
                    };
                    dialog.ShowDialog();
                });
                return;
            }

            // ② LastRunVersion 比对
            var current = CurrentVersion();
            var last = RegistryStore.ReadString("Update", "LastRunVersion");
            if (last is null)
            {
                RegistryStore.WriteString("Update", "LastRunVersion", current.ToString());
                return;
            }
            if (!Core.Updates.UpdateManager.IsJustUpdated(current, last))
            {
                return;
            }

            var currentVersion = DisplayVersion(current.ToString());
            var body = await manager.FetchNotesAsync(currentVersion).ConfigureAwait(false);
            WriteLauncherLog($"版本从 {last} 变成 {current}，给用户看一次这一版的说明");

            dispatcher.Invoke(() =>
            {
                var dialog = new Updates.UpdateWindow(currentVersion, body, () =>
                    RegistryStore.WriteString("Update", "LastRunVersion", current.ToString()))
                {
                    Owner = window,
                };
                dialog.ShowDialog();
            });
        }
        catch (Exception ex)
        {
            WriteLauncherLog($"更新后的提示失败：{ex.Message}");
        }
    }

    /// <summary>后台查一次更新。</summary>
    private static async Task CheckUpdatesAsync(
        MainWindow window, System.Windows.Threading.Dispatcher dispatcher)
    {
#if DEBUG
        // Debug 构建跳过启动时的自动检查，避免每次 F5 都请求 GitHub。
        // ⚠️ 只拦这一处；设置页的手动检查和 pending_update 提示仍然执行。
        await Task.Yield();
        WriteLauncherLog("Debug 构建：跳过启动时的自动检查（设置页的手动「检查更新」仍然可用）");
#else
        try
        {
            if (Core.Updates.UpdateManager.IsDisabledByEnvironment())
            {
                WriteLauncherLog("自动更新已被环境变量禁用");
                return;
            }
            if (!Core.Updates.UpdateManager.IsSafeInstallDir(BaseDir, out var reason))
            {
                WriteLauncherLog($"自动更新跳过：{reason}");
                return;
            }

            var manager = new Core.Updates.UpdateManager(BaseDir, CurrentVersion(), WriteLauncherLog);
            var result = await manager.CheckAsync().ConfigureAwait(false);
            WriteLauncherLog(result.Message);
            if (!result.Available || result.Info is not { } info)
            {
                return;
            }

            dispatcher.Invoke(() =>
            {
                var dialog = new Updates.UpdateWindow(manager, info) { Owner = window };
                dialog.ShowDialog();
            });
        }
        catch (Exception ex)
        {
            // 查不到更新是个"没事发生"，不是错误：日志里留一行就够
            WriteLauncherLog($"检查更新失败（已忽略）：{ex.Message}");
        }
#endif
    }

    /// <summary>`--toast-kind` 的值 → 提示语气（认不出来的当成功）。</summary>
    private static Infrastructure.ToastKind ParseToastKind(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "info" => Infrastructure.ToastKind.Info,
            "warning" or "warn" => Infrastructure.ToastKind.Warning,
            "error" => Infrastructure.ToastKind.Error,
            _ => Infrastructure.ToastKind.Success,
        };

    private static bool DebugHintDisabled()
        => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MOSSLIGHT_DISABLE_DEBUG_HINT"));

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 兜底：界面线程上没接住的异常，别让窗口直接消失、什么都不留。
        // （XAML 里 StaticResource 写错、样式表语法坏了这类问题都是在这里现形的。）
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog("DispatcherUnhandledException", args.Exception);
            MessageBox.Show(args.Exception.Message, "Mosslight Launcher 出错",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                WriteCrashLog("UnhandledException", ex);
            }
        };

        // 中文界面按 UTF-8 走，免得控制台/日志里中文变问号
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture;
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        try
        {
            BaseDir = AppContext.BaseDirectory;

            // 旧版本把配置放在 %APPDATA%\Mosslight：搬一次（目标已有配置就不动）。
            // ⚠️ 必须排在 Load 之前：搬完了这一趟读到的就是旧设置。
            LegacyDataMigration.ImportFromAppDataOnce(BaseDir);

            Settings = AppSettings.Load(BaseDir);

            // 游戏配置**只认游戏目录里的 config.ini**（不读 %APPDATA%）：没有就建一份默认的，
            // 写不了就弹窗退出 —— 连配置都存不下来，后面装游戏只会更乱。
            var gameDir = string.IsNullOrWhiteSpace(Settings.MinecraftDir) ? BaseDir : Settings.MinecraftDir;
            if (!Core.Storage.GameConfigFile.Ensure(gameDir, out var gameIni, out var gameIniError))
            {
                MessageBox.Show(
                    $"写不了游戏配置：{gameIni}{Environment.NewLine}{gameIniError}",
                    "Mosslight Launcher 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
                Current.Shutdown(1);
                return;
            }
            WriteLog("launcher.log", $"游戏配置：{gameIni}");
            // ⚠️ 这个目录**通常不存在**（发布产物只有一个 exe，语言数据编在
            // Mosslight.Core 里）。传进来只是把"用户覆盖"那一层的入口告诉
            // Localizer：目录里真有 <代码>.json 就优先用它，没有就走内嵌那份。
            // 所以这里**不要**加 Directory.Exists 判断、更不要因为缺目录就报错。
            Localizer.Init(Path.Combine(BaseDir, "Assets", "lang"));
            // 语言属于"会话级基础设置"：注册表里有就用它（坏值当没有），否则走配置 / 系统语言
            Localizer.SetLanguage(
                SupportedLanguage(RegistryStore.ReadString(RegistryStore.SessionKey, "Language"))
                ?? Localizer.ResolveLanguage(Settings));

            // `--lang <代码>`：**强制**用一种语言渲染，不看配置里存了什么。
            // ⚠️ 不加这个开关的话，"验中文截图"这类断言是**不确定**的：
            // 配置里存过 `language: en_US` 就永远是英文，门禁会莫名其妙地红。
            // 这是诊断 / 自动化专用口子，界面里没有它。
            var forcedLang = ArgumentValue(e.Args, "--lang");
            if (!string.IsNullOrWhiteSpace(forcedLang))
            {
                Localizer.SetLanguage(forcedLang);
            }

            // `--lang-check`：把 i18n 的加载结果打到 stdout 然后退出（不建窗口）。
            // 用途见 LanguageCheckReport 的注释。
            if (HasFlag(e.Args, "--lang-check"))
            {
                LanguageCheckReport(e.Args);
                return;
            }

            // 真引擎。⚠️ 这里从 Prototype() 换成 Production() 就是"接上引擎"的全部开关：
            // 界面认的是接口，`IsPrototype` 一变 false，状态行那句
            // 「（原型数据：启动引擎还没接）」就自己消失了。
            Services = LauncherServices.Production(Settings, BaseDir);

            // 「放自己皮肤」的目录（`skins/`）：这里**必须建**。
            PlayerAvatars.EnsureSkinDir(AppSettings.ResolveConfigDir(BaseDir));

            // 收藏夹：只是读写配置目录里那份 favorites.json，坏了当空（见 FavoritesStore）
            Favorites = new Mosslight.Core.Services.FavoritesStore(
                AppSettings.ResolveConfigDir(BaseDir));

            ThemeApplier.Apply(Settings);

            // 自动化截图模式：**不 Show 窗口**，直接离屏渲染完就退出
            // （理由见 SaveWindowPng 的注释：不闪窗、尺寸确定）
            var screenshotPath = ScreenshotRequestedPath(e.Args);
            ScreenshotMode = screenshotPath is not null;
            var window = new MainWindow();
            MainWindow = window;

            // `--query 关键词`：进模组页时顺手搜一把（理由见 ModsPage.RunQuery）。
            // ⚠️ 要在 `--page` **之前**设：ShowPage 建页面的那一刻就把它用掉了。
            window.SetInitialQuery(ArgumentValue(e.Args, "--query"));

            // `--mc <版本号>`：安装选项页认这一版（理由见 DownloadPage.SetInitialMcVersion）。
            // 同样要在 `--page` 之前设。
            window.SetInitialMcVersion(ArgumentValue(e.Args, "--mc"));

            // `--page 页面 key` 让截图脚本能直接截某一页（不然只能截到首页）
            var pageKey = ArgumentValue(e.Args, "--page");
            if (pageKey is not null)
            {
                ScreenshotPageKey = pageKey;
                ScreenshotNoticeBody = ArgumentValue(e.Args, "--notice-body");
                // `debug:notice` 不是"页面"，是开发版提示窗：不切页，只把它设成截图目标
                if (!string.Equals(pageKey, "debug:notice", StringComparison.Ordinal))
                {
                    window.ShowPage(pageKey);
                }
            }

            // `--pack 文件`：跟"把整合包拖到窗口上"走**同一条路**（见 MainWindow.OpenLocalPack）。
            // 拖放是鼠标动作，离屏截图点不了 —— 不给入口的话那张卡就只有人肉开程序才看得见。
            // ⚠️ 必须排在 `--page` **之后**：它自己会把页面切到「下载 → 整合包」，
            // 排在前面的话会被后面的 `--page home` 一把掀掉（实测过：截出来还是首页）。
            var packPath = ArgumentValue(e.Args, "--pack");
            if (packPath is not null)
            {
                window.OpenLocalPack(packPath);
            }

            // `--toast 文字`：弹一个右上角的提示框再截图。
            // 提示框是"点收藏才出现"的瞬时反馈，截图脚本点不了按钮 —— 没有这个入口
            // 就只能人肉开程序点一下，那种截图没法进回归对比。
            // ⚠️ 排在 `--page` **之后**：它只往主窗口的浮层里加，但页面切换不该把它顶掉。
            var toastText = ArgumentValue(e.Args, "--toast");
            if (toastText is not null)
            {
                // `--toast-kind info|success|warning|error`：截哪种语气的提示（默认成功）
                window.ShowToast(toastText, ParseToastKind(ArgumentValue(e.Args, "--toast-kind")));
            }

            // `--switch-lang <代码>`：**启动之后再切一次语言**，用来验"运行时切语言"
            // 这条路径（`--lang` 只在建窗口之前生效，验不到重建逻辑）。
            // 走的是和「个性化 → 语言」完全一样的方法 `MainWindow.ApplyLanguage`：
            // 换词典 → 重贴侧边栏/档案区 → 重建当前页。诊断 / 自动化专用，界面上没有。
            var switchLang = ArgumentValue(e.Args, "--switch-lang");
            if (!string.IsNullOrWhiteSpace(switchLang))
            {
                window.ApplyLanguage(switchLang);
            }

            if (screenshotPath is not null)
            {
                // `--page personalize:anim` 打开的是**对话框**：那一段要截的目标不是主窗口
                // （对话框没 Show，只能直接渲染它的 Content）。见 MainWindow.ScreenshotTarget。
                RenderThenQuit(window.ScreenshotTarget ?? window, screenshotPath, ParseSettle(e.Args));
                return;
            }

            window.Show();

            // 更新检查不在这儿做：要等主窗口真正显示出来（MainWindow.Loaded →
            // OnMainWindowReady），否则网络慢的时候会拖着首屏不出来。

            // 开发版提示：从编译器里跑起来的这个 exe 会先弹一次（见 IsDevelopmentBuild）。
            // ⚠️ 环境变量 MOSSLIGHT_DISABLE_DEBUG_HINT 设了任意值就跳过 —— 反复调试时
            // 每次都要点一下那个窗很烦。
            if (IsDevelopmentBuild && !DebugHintDisabled())
            {
                // 提示窗开着的时候，底下的启动器压一层半透明红（关掉就恢复）
                window.SetDebugNoticeVeil(true);
                try
                {
                    new Views.DebugBuildNoticeDialog { Owner = window }.ShowDialog();
                }
                finally
                {
                    window.SetDebugNoticeVeil(false);
                }
            }
        }
        catch (Exception ex)
        {
            WriteCrashLog("OnStartup", ex);
            MessageBox.Show(ex.ToString(), "Mosslight Launcher 启动失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Current.Shutdown(1);
        }
    }

    /// <summary>崩溃日志写到 exe 旁边的 `Logs\crash.log`。</summary>
    public static void WriteCrashLog(string where, Exception ex)
    {
        WriteLog("crash.log", $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {where}{Environment.NewLine}{ex}");
    }

    /// <summary>
    /// 启动器自己的运行日志（`Logs\launcher.log`）；⚠️ 同时喂一份给日志窗口，别只写文件，否则窗口
    /// 里没有内容。
    /// </summary>
    public static void WriteLog(string fileName, string message)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, fileName),
                message + Environment.NewLine,
                System.Text.Encoding.UTF8);
        }
        catch (Exception logEx) when (logEx is IOException or UnauthorizedAccessException)
        {
            // 连日志都写不进去（只读目录）就只能算了，别在这儿再抛一次
        }

        // 只把"用户流水"送进窗口，崩溃堆栈不进 —— 那是发给开发者的，
        // 而且 `WriteCrashLog` 会在窗口还没建起来的时候就被调用（启动失败那条路）
        if (fileName == "launcher.log" || fileName == "game.log")
        {
            LogBus.Write(message);
        }
    }

    /// <summary>往 `launcher.log` 记一条（自动补时间戳）。</summary>
    public static void WriteLauncherLog(string message)
        => WriteLog("launcher.log", $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}");

    // 独立日志窗口

    /// <summary>
    /// 当前那个日志窗口（null = 没开着）；⚠️ 全进程**只留一个**：日志窗口是可以被反复打开的（设置
    /// 页那个开关每次启动都会喊一次），每开一个就多一份 FlowDocument 和一份 LogBus 订阅 —— 开五次
    /// 就是五份内存。
    /// </summary>
    private static Views.LogWindow? _logWindow;

    /// <summary>打开（或提到前面）日志窗口。</summary>
    public static Views.LogWindow OpenLogWindow()
    {
        if (_logWindow is { IsLoaded: true })
        {
            _logWindow.Activate();
            return _logWindow;
        }

        var window = new Views.LogWindow();
        _logWindow = window;

        if (ScreenshotMode)
        {
            return window;
        }
        window.Show();
        window.Activate();
        return window;
    }

    /// <summary>截图模式要渲染的那个日志窗口（没建过就是 null）。</summary>
    internal static Views.LogWindow? LogWindowForScreenshot
        => _logWindow is { IsLoaded: false } ? _logWindow : null;

    private static Views.DownloadWindow? _downloadWindow;

    /// <summary>
    /// 打开下载进度窗（装原版 / 装加载器 / 下模组 / 装整合包都走这里）；⚠️ 同一时刻只留一个：上一
    /// 批的还开着就先关掉（不然装第二个东西时会叠出两个窗口，而且两个都在轮询同一份快照，显示的东
    /// 西一模一样）。
    /// </summary>
    public static Views.DownloadWindow OpenDownloadWindow(string title)
    {
        if (_downloadWindow is { IsLoaded: true } alive)
        {
            alive.Close();
        }

        var window = new Views.DownloadWindow(title);
        _downloadWindow = window;

        if (ScreenshotMode)
        {
            return window;
        }
        window.Show();
        window.Activate();
        return window;
    }

    /// <summary>截图模式要渲染的那个下载窗口（没建过就是 null）。</summary>
    internal static Views.DownloadWindow? DownloadWindowForScreenshot
        => _downloadWindow is { IsLoaded: false } ? _downloadWindow : null;

    /// <summary>`--screenshot &lt;路径&gt;` 的路径。</summary>
    private static string? ScreenshotRequestedPath(string[] args) => ArgumentValue(args, "--screenshot");

    /// <summary>`--settle &lt;毫秒&gt;`：截图前等多久（不给就用 DefaultSettleMs）。</summary>
    private static int ParseSettle(string[] args)
        => int.TryParse(ArgumentValue(args, "--settle"), out var ms) && ms >= 0
            ? ms
            : DefaultSettleMs;

    /// <summary>
    /// `--lang-check`：把语言数据的加载结果打到 stdout 然后退出（**不建窗口**）。
    /// </summary>
    private static void LanguageCheckReport(string[] args)
    {
        var probe = ArgumentValue(args, "--lang-key") ?? "启动游戏";
        var wanted = ArgumentValue(args, "--lang");
        var codes = string.IsNullOrWhiteSpace(wanted)
            ? Localizer.AvailableLanguages()
            : new[] { wanted };

        var diskDir = Path.Combine(AppContext.BaseDirectory, "Assets", "lang");
        var hasOverlay = Directory.Exists(diskDir);
        Console.WriteLine($"[lang] exe 目录 = {AppContext.BaseDirectory}");
        Console.WriteLine($"[lang] 覆盖层目录 = {diskDir}"
                          + (hasOverlay
                              ? "（**存在** —— 用户覆盖这一层在生效，来源可以是 Disk）"
                              : "（不存在 —— 单文件发布的正常形态，来源必须是 Embedded）"));
        Console.WriteLine($"[lang] 程序集里编进去的语言 = {string.Join(", ", Localizer.EmbeddedLanguages())}");
        Console.WriteLine($"[lang] 可用语言（内嵌 ∪ 磁盘）= {string.Join(", ", Localizer.AvailableLanguages())}");

        var failed = 0;
        foreach (var code in codes)
        {
            var source = Localizer.SetLanguageWithSource(code);
            var keys = AllKeysOf(code, out var keysOk);
            var translated = keys.Count(k => !string.Equals(Localizer.Tr(k), k, StringComparison.Ordinal));
            var probeText = Localizer.Tr(probe);

            // ⚠️ 来源判定看覆盖层目录：不存在时来源必须是 Embedded，存在时才允许 Disk；zh_CN 是兜底语言，不要求译文与源文不同。
            var isFallback = code == Localizer.FallbackLanguage;
            var sourceOk = hasOverlay
                ? source is Localizer.LanguageSource.Disk or Localizer.LanguageSource.Embedded
                : source == Localizer.LanguageSource.Embedded;
            var ok = keysOk && sourceOk && keys.Count > 500 && (isFallback || translated > 500);
            if (!ok) failed++;
            var note = isFallback ? "（兜底语言，值本来就等于 key）" : "";
            Console.WriteLine($"[lang] {(ok ? "✓" : "✗")} {code}：来源 = {source}，文案 {keys.Count} 条，" +
                              $"其中 {translated} 条跟中文源文不同{note}；\"{probe}\" → \"{probeText}\"");
        }

        // 内嵌那份必须"LanguageNames 里登记的每一种都在"：加语言时只丢了个 json、
        // 或者资源名规则变了，都会在这里露出来
        var embedded = Localizer.EmbeddedLanguages();
        var names = Localizer.LanguageNames.Keys.ToList();
        var embeddedOk = names.All(embedded.Contains) && embedded.Count == names.Count;
        if (!embeddedOk) failed++;
        Console.WriteLine($"[lang] {(embeddedOk ? "✓" : "✗")} 内嵌 {embedded.Count} 种，" +
                          $"LanguageNames 登记 {names.Count} 种");

        Console.WriteLine($"[lang] 结论：{(failed == 0 ? "全部通过" : $"{failed} 项不通过")}");
        Current.Shutdown(failed == 0 ? 0 : 1);
    }

    /// <summary>
    /// 把某个语言的**全部键**取出来 —— 键本身就是中文源文案，所以拿它当 Localizer.Tr 的入参就能逐
    /// 条验"翻没翻"；⚠️ 这里**故意按资源名直接读程序集**，不走 Localizer：要验的正是 "资源名对不
    /// 对"，跟着实现读就永远一致、等于没验。
    /// </summary>
    private static List<string> AllKeysOf(string code, out bool ok)
    {
        using var stream = typeof(Localizer).Assembly
            .GetManifestResourceStream($"Mosslight.Core.Localization.lang.{code}.json");
        if (stream is null)
        {
            ok = false;
            return new List<string>();
        }
        using var doc = System.Text.Json.JsonDocument.Parse(stream);
        ok = true;
        return doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
    }

    /// <summary>取 `--名字值` 这种参数的值（没有就返回 null）。</summary>
    private static string? ArgumentValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    /// <summary>有没有 `--名字` 这个**开关**（不带值，所以不能走 ArgumentValue）。</summary>
    private static bool HasFlag(string[] args, string name)
        => Array.Exists(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private const int DefaultSettleMs = 1200;

    /// <summary>
    /// 离屏渲染用多大：主窗口固定 1280×820，其它窗口（比如动效设置对话框）按它**自己**在 XAML 里
    /// 定的尺寸。
    /// </summary>
    private static (int Width, int Height) RenderSize(Window window)
    {
        if (window is MainWindow)
        {
            return (1280, 820);
        }
        var width = double.IsNaN(window.Width) ? 1280 : (int)window.Width;
        var height = double.IsNaN(window.Height) ? MeasureContentHeight(window, width) : (int)window.Height;
        return (width, height);
    }

    private static int MeasureContentHeight(Window window, int width)
    {
        if (window.Content is not FrameworkElement content)
        {
            return 820;
        }
        content.Measure(new Size(width, double.PositiveInfinity));
        var height = (int)Math.Ceiling(content.DesiredSize.Height);
        return height > 0 ? height : 820;
    }

    private static void RenderThenQuit(Window window, string path, int settleMs = DefaultSettleMs)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(0, settleMs)),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                var (width, height) = RenderSize(window);
                SaveWindowPng(window, path, width, height);
                Console.WriteLine($"[screenshot] {path}");
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                WriteCrashLog("screenshot", ex);
                Environment.ExitCode = 3;
            }
            Current.Shutdown();
        };
        timer.Start();
    }

    /// <summary>
    /// 把窗口内容渲染成 PNG；⚠️ **离屏渲染，不 Show 窗口** —— 两个原因：1. 截图是自动化流程，不该
    /// 在用户桌面上闪一个窗口；2. 尺寸要**确定**。
    /// </summary>
    public static void SaveWindowPng(Window window, string path, int width = 1280, int height = 820)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);

        // 先垫一层窗口底色（页面自己是半透明的，没这层就会透出黑色）
        var background = new DrawingVisual();
        using (var context = background.RenderOpen())
        {
            context.DrawRectangle(window.Background ?? Brushes.Transparent, null, new Rect(0, 0, width, height));
        }
        bitmap.Render(background);
        // RenderTargetBitmap.Render 是**叠加**不是覆盖，所以上面那层不会把底色刷掉
        bitmap.Render(content);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
