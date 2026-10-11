using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
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

/// <summary>
/// 程序入口。
///
/// 启动顺序是有讲究的：
///   1. 读配置（读坏了也要能起来 —— 背景图/动效这些东西坏了不该让人开不了启动器）
///   2. 定语言（配置里存过就用存的，没存过按系统语言定）
///   3. 应用主题（**必须在建窗口之前**：晚一步会先闪一帧默认深色）
///   4. 建主窗口
/// </summary>
public partial class App : Application
{
    /// <summary>全局配置（全程序一份）。</summary>
    public static AppSettings Settings { get; private set; } = null!;

    /// <summary>全局服务（原型阶段全是假实现）。</summary>
    public static LauncherServices Services { get; private set; } = null!;

    /// <summary>进程启动目录（配置、语言文件都相对它找）。</summary>
    public static string BaseDir { get; private set; } = string.Empty;

    /// <summary>
    /// 「离屏截图模式」（命令行给了 `--screenshot`）。
    ///
    /// ⚠️ 页面里那些"要用户点一下才会出现"的东西（对话框、展开的分组）需要一个**开关**
    ///    来判断自己该不该直接 Show：截图是在没有窗口、没有鼠标的环境里跑的，
    ///    照常 Show 会在用户桌面上闪一个窗（而且 Owner 都设不了，见
    ///    PersonalizePage.OpenAnimSettings）。有了这个标志，页面就能走"只建不显示、
    ///    交给 App 渲染"那条路。
    /// </summary>
    public static bool ScreenshotMode { get; private set; }

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

            Settings = AppSettings.Load(BaseDir);
            Localizer.Init(Path.Combine(BaseDir, "Assets", "lang"));
            Localizer.SetLanguage(Localizer.ResolveLanguage(Settings));

            // 真引擎。⚠️ 这里从 Prototype() 换成 Production() 就是"接上引擎"的全部开关：
            // 界面认的是接口，`IsPrototype` 一变 false，状态行那句
            // 「（原型数据：启动引擎还没接）」就自己消失了。
            Services = LauncherServices.Production(Settings, BaseDir);

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

            // `--page 页面 key` 让截图脚本能直接截某一页（不然只能截到首页）
            var pageKey = ArgumentValue(e.Args, "--page");
            if (pageKey is not null)
            {
                window.ShowPage(pageKey);
            }

            if (screenshotPath is not null)
            {
                // `--page personalize:anim` 打开的是**对话框**：那一段要截的目标不是主窗口
                // （对话框没 Show，只能直接渲染它的 Content）。见 MainWindow.ScreenshotTarget。
                RenderThenQuit(window.ScreenshotTarget ?? window, screenshotPath, ParseSettle(e.Args));
                return;
            }

            window.Show();
        }
        catch (Exception ex)
        {
            WriteCrashLog("OnStartup", ex);
            MessageBox.Show(ex.ToString(), "Mosslight Launcher 启动失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Current.Shutdown(1);
        }
    }

    /// <summary>
    /// 崩溃日志写到 exe 旁边的 `Logs\crash.log`。
    /// ⚠️ 故意**不用** %TEMP%：出问题时用户要找得到、发得出来，
    ///    而启动器的目录本身就在用户手上（1.0.0 的日志窗口也是这个思路）。
    /// </summary>
    public static void WriteCrashLog(string where, Exception ex)
    {
        WriteLog("crash.log", $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {where}{Environment.NewLine}{ex}");
    }

    /// <summary>
    /// 启动器自己的运行日志（`Logs\launcher.log`）。
    ///
    /// 启动时拼出来的那条命令行就往这儿写 —— 它是"游戏没起来 / 起来以后行为不对"
    /// 时唯一能看出"参数拼错没有"的东西（1.0.0 也是把 `describe()` 打进日志窗口）。
    /// 崩溃日志和它分开：一个是要发给开发者的堆栈，一个是用户自己也能看的流水。
    ///
    /// ⚠️ 这里**同时喂一份给日志窗口**（<see cref="LogBus"/>）：1.0.0 的
    ///    `Logs\` 文件和日志窗口看的是同一份内容，2.0 要是只写文件，
    ///    用户按设置页那个开关打开窗口就会看到一片空白。
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

    // ============================================================
    //  独立日志窗口
    // ============================================================

    /// <summary>
    /// 当前那个日志窗口（null = 没开着）。
    ///
    /// ⚠️ 全进程**只留一个**：日志窗口是可以被反复打开的（设置页那个开关每次启动都会
    ///    喊一次），每开一个就多一份 FlowDocument 和一份 <c>LogBus</c> 订阅 ——
    ///    开五次就是五份内存。所以这里做单例，再开就把已有的提到前面
    ///    （1.0.0 的 `open_log_window()` 也是 `show() + raise_()`，同一个思路）。
    /// </summary>
    private static Views.LogWindow? _logWindow;

    /// <summary>
    /// 打开（或提到前面）日志窗口。启动游戏那条路和以后可能的"菜单 → 日志"都走它。
    ///
    /// ⚠️ 离屏截图模式（`--screenshot`）下**照常建、不 Show** —— 跟
    ///    <c>PersonalizePage.OpenAnimSettings</c> 同一个道理：截图模式跑在没有窗口、
    ///    没有鼠标的环境里，Show 会在用户桌面上闪一个窗，而且 Owner 都没法设。
    ///    建的实例由 <c>MainWindow.ScreenshotTarget</c> 拿去做离屏渲染。
    /// </summary>
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

        // Owner 只在主窗口**已经显示过**之后才能设，否则会抛 InvalidOperationException
        if (Current?.MainWindow is { IsLoaded: true } main)
        {
            window.Owner = main;
        }
        window.Show();
        window.Activate();
        return window;
    }

    /// <summary>截图模式要渲染的那个日志窗口（没建过就是 null）。</summary>
    internal static Views.LogWindow? LogWindowForScreenshot
        => _logWindow is { IsLoaded: false } ? _logWindow : null;

    /// <summary>`--screenshot &lt;路径&gt;` 的路径。</summary>
    private static string? ScreenshotRequestedPath(string[] args) => ArgumentValue(args, "--screenshot");

    /// <summary>`--settle &lt;毫秒&gt;`：截图前等多久（不给就用 <see cref="DefaultSettleMs"/>）。</summary>
    private static int ParseSettle(string[] args)
        => int.TryParse(ArgumentValue(args, "--settle"), out var ms) && ms >= 0
            ? ms
            : DefaultSettleMs;

    /// <summary>取 `--名字 值` 这种参数的值（没有就返回 null）。</summary>
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

    /// <summary>
    /// 等界面画稳定了再截图然后退出。
    ///
    /// ⚠️ 两个坑，都是实测踩出来的：
    ///   1. **不能用 `Thread.Sleep`** —— 那会把渲染也一起停掉，截出来是白板。
    ///      要用 `DispatcherTimer`（消息泵照常转，动画和布局继续走）。
    ///   2. **要等入场动效铺完**。列表页的卡片是错峰滑入的（默认 420ms + 95ms×n），
    ///      刚 Show 完就截会截到"标题半透明、卡片还没出现"的中途状态。
    ///      所以默认等 <see cref="DefaultSettleMs"/>，也可以用 `--settle &lt;毫秒&gt;` 调。
    ///      （当前「启动」「个性化」两页本身没有入场动画，但版本/模组列表马上就会有，
    ///      这个等待是为它们准备的，不是白等。）
    /// </summary>
    private const int DefaultSettleMs = 1200;

    /// <summary>
    /// 离屏渲染用多大：主窗口固定 1280×820（跟 1.0.0 的截图对齐，好逐块比），
    /// 其它窗口（比如动效设置对话框）按它**自己**在 XAML 里定的尺寸。
    /// ⚠️ 不给对话框写死一个数：那样以后改窗口大小时截图会静默地跟着错位。
    /// </summary>
    private static (int Width, int Height) RenderSize(Window window)
    {
        if (window is MainWindow)
        {
            return (1280, 820);
        }
        var width = double.IsNaN(window.Width) ? 1280 : (int)window.Width;
        var height = double.IsNaN(window.Height) ? 820 : (int)window.Height;
        return (width, height);
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
    /// 把窗口内容渲染成 PNG。
    ///
    /// ⚠️ **离屏渲染，不 Show 窗口** —— 两个原因：
    ///   1. 截图是自动化流程（写文档、跟 1.0.0 的图对比），不该在用户桌面上闪一个窗口；
    ///   2. 尺寸要**确定**。`Window.Width/Height` 含边框和标题栏，而截图要的是**客户端区**
    ///      1280×820（1.0.0 的截图就是这个尺寸，不然没法逐块比）。所以这里手动
    ///      Measure/Arrange 到指定尺寸，再渲染 —— 跟窗口实际多大无关。
    ///      （之前那版按 `ActualWidth/ActualHeight` 开画布、画的却是 Content，
    ///       结果 PNG 右边和下边各多出一条窗口底色的空白带。）
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
