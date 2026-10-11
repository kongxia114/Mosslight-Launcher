using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Mosslight.Core.Localization;
using Mosslight.Core.Logging;

namespace Mosslight.App.Views;

/// <summary>
/// 独立的游戏日志窗口。移植自 1.0.0 的 <c>ui/dialogs/log_window.py</c>。
///
/// ## 它跟 1.0.0 那份的关系
/// 布局、交互、"点计数就把那一级藏起来"这些**来历清楚**的设计照抄了；
/// 内容来源换成了 2.0 的 <see cref="LogBus"/> —— 那边是"首页持有窗口、
/// `append_log()` 直接往里写"，这边是"日志是进程级的、窗口只是订阅者"。
/// 差别在效果上的好处：窗口**后开也有历史**，而且下载页 / 版本页发生的事也在里面。
///
/// ## 三处**有意**跟 1.0.0 不一样的地方
///   1. **正文用 RichTextBox 而不是 TextBox**。1.0.0 是 QPlainTextEdit +
///      `QTextCharFormat` 一段段上色（QSS 管不到单独几行）。WPF 里对等的做法是
///      <see cref="FlowDocument"/> + 每行一个 <see cref="Run"/> 带 Foreground。
///   2. **多了一个「过滤噪音」开关**。1.0.0 正式版压根没有噪音过滤
///      （那份 `LOG_NOISE_PATTERNS` 只在实验版里，见 <see cref="LogLine.NoisePatterns"/>），
///      所以这里做成了可关的 —— 宁可让用户自己关，也不要替他决定滤掉什么。
///   3. **「结束游戏进程」在没有游戏在跑时是禁用的**。1.0.0 那个按钮永远能点，
///      点了只是发一个信号（那边首页收到再判断）。这边直接看 `IsRunning`，
///      点不动的按钮比"点了没反应"诚实。
///
/// ## 性能上做过的三件事（都是实测踩出来的）
///   · 同一级别的**连续行合并成一次写入**（<see cref="Rebuild"/>）：日志是一串一串来的，
///     这样几万行也就几十毫秒；点一下过滤开关就整篇重建，省得维护增量状态。
///   · **流式追加**（<see cref="Append"/>）而不是"每来一行重建一次"：重建要
///     清空整个 FlowDocument，游戏跑起来一秒几十行，那样会闪。
///   · 超过行数上限时**从头删段落**（<see cref="Trim"/>）：FlowDocument 删中间的块很慢，
///     删最前面那段是 O(1) 的（它就是个链表）。
///
/// ## 主题
/// 每行的级别颜色是写进 <see cref="Run.Foreground"/> 的，DynamicResource 管不到 ——
/// 所以窗口订阅了 <see cref="ThemeApplier"/> 那个换主题事件，换主题时把缓存扔掉重画
/// （1.0.0 的 `refresh_theme()` 同样的道理）。
/// </summary>
public partial class LogWindow : Window
{
    /// <summary>显示行数的档位（0 = 不限）—— 照 1.0.0 的 <c>LINE_LIMITS</c>。</summary>
    private static readonly int[] LineLimits = { 2000, 5000, 20000, 0 };

    /// <summary>
    /// 级别 → (主题画刷键, 要不要加粗)。照 1.0.0 的 <c>_LEVEL_COLOR</c>：
    /// 错误红、警告黄、信息灰、调试更淡；FATAL 额外加粗。
    /// <see cref="LogLevel.None"/>（认不出级别的行）用正文色。
    ///
    /// ⚠️ 只有 `Info` 这一条**有意**跟 1.0.0 不同：那边是 <c>text_dim</c>，
    ///    C# 这边用 <see cref="TextFaint"/>（更亮一档）。理由是实机截图上看，
    ///    INFO 通常占了窗口里八成以上的行，用 dim 的话整屏都糊在底色里 ——
    ///    QSS 那边 dim 是给"整块控件"的亮度，一行一行分开看时太暗了。
    ///    其余四个级别跟 1.0.0 一一对应。
    /// </summary>
    private static readonly Dictionary<LogLevel, (string Brush, bool Bold)> LevelColors = new()
    {
        [LogLevel.Fatal] = ("DangerBrush", true),
        [LogLevel.Error] = ("DangerBrush", false),
        [LogLevel.Warn] = ("WarnBrush", false),
        [LogLevel.Info] = ("TextFaintBrush", false),
        [LogLevel.Debug] = ("TextDisabledBrush", false),
        [LogLevel.None] = ("TextBrush", false),
    };

    private readonly Dictionary<LogLevel, ToggleButton> _levelButtons = new();

    /// <summary>各级别当前有多少条（计数按钮上的数字）。</summary>
    private readonly Dictionary<LogLevel, int> _counts = new();

    /// <summary>已经写进文档、但还没被行数上限裁掉的行数（裁的时候要按它删段落）。</summary>
    private readonly List<LogLevel> _renderedOwners = new();

    /// <summary>归属级别 → 渲染用的画刷（缓存：一行一个 Brush 太浪费）。</summary>
    private readonly Dictionary<LogLevel, Brush> _brushes = new();

    /// <summary>
    /// `[启动器] ` 前缀那一段的颜色（强调色）。
    ///
    /// 为什么要单独一个色：默认那档文案（`[启动器] 检测到 10 个可用的 Java`）跟
    /// 游戏输出的 INFO 行混在一起时，扫一眼分不出"哪几行是启动器说的"——
    /// 而"部署到哪一步了"恰恰是用户最常找的东西。
    /// </summary>
    private Brush _launcherBrush = Brushes.Gray;

    /// <summary>
    /// 「正在回填界面」。
    /// ⚠️ 必须有：构造期给 ComboBox / CheckBox 赋值会触发它们的事件，
    /// 不挡一下就会在控件还没建完时就去读 <c>LogView.Document</c>，直接空引用。
    /// </summary>
    private bool _syncing = true;

    /// <summary>运行状态 → 「结束游戏进程」能不能点（见类注释第 3 条）。</summary>
    private DispatcherTimer? _runStateTimer;

    public LogWindow()
    {
        InitializeComponent();

        BuildLevelButtons();
        BuildLimitOptions();
        BuildBrushes();

        // 噪音过滤的默认值来自配置（键名 `log_noise_filter`，默认开）——
        // 1.0.0 没有这个开关，这是新增的（见类注释第 2 条）
        NoiseCheck.IsChecked = App.Settings.GetBool("log_noise_filter", true);

        // 先整批读历史，再订阅新的 —— **顺序不能反**：
        // 反过来的话，"订阅之后、读历史之前"来的那几行会被写两遍
        Rebuild();

        LogBus.Appended += OnAppended;
        LogBus.Cleared += OnCleared;

        // 游戏退出 / 启动时按钮要跟着变（1.0.0 是首页 `_set_running` 顺手调的）
        App.Services.Launch.GameExited += OnGameExited;

        _runStateTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(700),
        };
        _runStateTimer.Tick += (_, _) => RefreshStopButton();
        _runStateTimer.Start();
        RefreshStopButton();

        _syncing = false;
    }

    // ============================================================
    //  建控件
    // ============================================================

    /// <summary>
    /// 那排级别计数 / 过滤开关。
    /// ⚠️ 用**代码**建而不是 XAML 里写死五个：颜色、文案、顺序全都来自
    ///    <see cref="LogLine.Filterable"/> 和 <see cref="LevelColors"/>，
    ///    写死在 XAML 里加一级就得改五处（1.0.0 那边是循环建的）。
    /// </summary>
    private void BuildLevelButtons()
    {
        foreach (var level in LogLine.Filterable)
        {
            var button = new ToggleButton
            {
                Style = (Style)FindResource("LogLevelToggle"),
                IsChecked = true,
                // 初始文案在 RefreshCounts 里统一刷（那时才有计数）
                Content = Localizer.Tr(LevelWord(level)),
                ToolTip = Localizer.Tr("点一下隐藏这一级，再点一下显示"),
                Margin = new Thickness(0, 0, 6, 0),
                Tag = level,
            };
            // 颜色按级别给（不是按"选中/未选中"）—— 弹起时压暗那条规则在模板的
            // IsChecked=False 触发器里，这里只负责"这一级本身是什么颜色"
            button.SetResourceReference(ForegroundProperty, LevelColors[level].Brush);
            if (LevelColors[level].Bold) button.FontWeight = FontWeights.Bold;

            button.Checked += OnFilterChanged;
            button.Unchecked += OnFilterChanged;
            _levelButtons[level] = button;
            LevelBar.Children.Add(button);
        }
    }

    /// <summary>行数下拉框（照 1.0.0 的 <c>LINE_LIMITS</c>：2000 / 5000 / 20000 / 全部）。</summary>
    private void BuildLimitOptions()
    {
        foreach (var limit in LineLimits)
        {
            LimitCombo.Items.Add(new ComboBoxItem
            {
                // 0 = 不限，文案是「全部」（1.0.0 同款：除了"全部"都是数字）
                Content = limit == 0 ? Localizer.Tr("全部") : limit.ToString(),
                Tag = limit,
            });
        }
        LimitCombo.SelectedIndex = 0;
    }

    /// <summary>
    /// 级别 → 画刷。用 <c>TryFindResource</c> 取当前主题的值。
    /// ⚠️ 是**快照**不是 DynamicResource：写进 Run 的 Foreground 之后主题再换它不会动，
    ///    所以换主题时要 <see cref="RefreshTheme"/> 把缓存扔掉重画（1.0.0 同款做法）。
    /// </summary>
    private void BuildBrushes()
    {
        _brushes.Clear();
        foreach (var (level, (key, _)) in LevelColors)
        {
            _brushes[level] = TryFindResource(key) as Brush ?? Brushes.Gainsboro;
        }
        _launcherBrush = TryFindResource("AccentBrush") as Brush ?? Brushes.Gray;
    }

    /// <summary>级别 → 界面上的词。⚠️ 字面量直接写在 <c>Tr()</c> 里（提取工具只认这种写法）。</summary>
    private static string LevelWord(LogLevel level) => level switch
    {
        LogLevel.Fatal => "致命",
        LogLevel.Error => "错误",
        LogLevel.Warn => "警告",
        LogLevel.Info => "信息",
        LogLevel.Debug => "调试",
        _ => "其他",
    };

    // ============================================================
    //  内容
    // ============================================================

    /// <summary>
    /// 新来一条。
    ///
    /// ⚠️ <see cref="LogBus.Appended"/> 是在**调用方的线程**上触发的
    ///    （游戏输出那条路是后台线程），所以这里必须切回 UI 线程。
    ///    不切的话下一步碰 <c>FlowDocument</c> 会直接抛
    ///    "调用线程无法访问此对象，因为另一个线程拥有该对象"。
    /// </summary>
    private void OnAppended(LogEntry entry)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnAppended(entry));
            return;
        }

        // 噪音：**不进计数、也不显示**（过滤掉的东西不该让计数虚高）
        if (IsNoise(entry)) return;

        _counts[entry.Owner] = Count(entry.Owner) + 1;
        RefreshCounts();

        if (!IsLevelVisible(entry.Owner)) return;

        Append(entry);
        Trim();
        ScrollToEndIfNeeded();
    }

    /// <summary>这一行要不要按"噪音"丢掉。</summary>
    private bool IsNoise(LogEntry entry)
        => NoiseCheck.IsChecked == true && LogLine.IsNoise(entry.Text);

    private int Count(LogLevel level) => _counts.TryGetValue(level, out var n) ? n : 0;

    /// <summary>
    /// 归属级别为空的行没有开关可点，永远显示（1.0.0 的 <c>_is_visible</c>）。
    /// ⚠️ 名字**不能**叫 <c>IsVisible</c>：<see cref="UIElement"/> 上本来就有这个名字，
    ///    重名会静默隐藏基类成员（编译器只给一条 warning），
    ///    而 WPF 内部也会调它 —— 那种 bug 很难查。
    /// </summary>
    private bool IsLevelVisible(LogLevel owner)
        => owner == LogLevel.None
           || !_levelButtons.TryGetValue(owner, out var button)
           || button.IsChecked == true;

    /// <summary>把一段文本按级别写进文档（连续的同一级别合并成一次写入）。</summary>
    private void WriteRun(string text, LogLevel owner, bool launcher)
    {
        var paragraph = new Paragraph
        {
            // 段落默认带 12px 上下的间距，日志是一行挨一行的，必须清掉
            Margin = new Thickness(0),
            LineHeight = double.NaN,
        };

        var brush = _brushes.TryGetValue(owner, out var found) ? found : Brushes.Gainsboro;
        var bold = LevelColors.TryGetValue(owner, out var style) && style.Bold;
        var weight = bold ? FontWeights.Bold : FontWeights.Normal;

        // `[启动器] ` 前缀单独上强调色（见 _launcherBrush 的注释）
        if (launcher && text.StartsWith(LogLine.LauncherPrefix, StringComparison.Ordinal))
        {
            paragraph.Inlines.Add(new Run(LogLine.LauncherPrefix)
            {
                Foreground = _launcherBrush,
                FontWeight = weight,
            });
            paragraph.Inlines.Add(new Run(text[LogLine.LauncherPrefix.Length..])
            {
                Foreground = brush,
                FontWeight = weight,
            });
        }
        else
        {
            paragraph.Inlines.Add(new Run(text) { Foreground = brush, FontWeight = weight });
        }

        LogView.Document.Blocks.Add(paragraph);
        _renderedOwners.Add(owner);
    }

    /// <summary>追加一条（流式，不重建整个文档）。</summary>
    private void Append(LogEntry entry) => WriteRun(entry.Text, entry.Owner, entry.IsLauncher);

    /// <summary>
    /// 超过行数上限就从**最老的**开始丢。
    /// ⚠️ 删段落用的是 <c>Blocks.Remove(Blocks.FirstBlock)</c> —— 那相当于删链表头，
    ///    不要用"取第 N 个再删"，FlowDocument 按索引取是 O(n) 的。
    /// </summary>
    private void Trim()
    {
        var limit = CurrentLimit();
        if (limit <= 0) return;

        while (_renderedOwners.Count > limit && LogView.Document.Blocks.FirstBlock is { } first)
        {
            LogView.Document.Blocks.Remove(first);
            _renderedOwners.RemoveAt(0);
        }
    }

    private int CurrentLimit()
        => LimitCombo.SelectedItem is ComboBoxItem { Tag: int value } ? value : LineLimits[0];

    /// <summary>自动滚到底（关掉「自动滚动」时不动 —— 用户正在往上翻）。</summary>
    private void ScrollToEndIfNeeded()
    {
        if (AutoScrollCheck.IsChecked != true) return;
        LogView.ScrollToEnd();
    }

    /// <summary>
    /// 按当前过滤条件 + 当前主题**整篇重建**。
    ///
    /// 为什么允许整篇重建：点过滤开关是低频操作（人手点的），
    /// 而重建几万行只要几十毫秒；维护"哪几段该显示"的增量状态反而更容易出错
    /// （1.0.0 的注释也是这个理由）。
    /// ⚠️ 重建前**记下滚动位置**：关掉「自动滚动」时用户正翻在某处，
    ///    重建完跳回顶上等于把他翻丢了。
    /// </summary>
    private void Rebuild()
    {
        var scroll = FindScrollViewer(LogView);
        var keep = scroll?.VerticalOffset ?? 0;

        BuildBrushes();

        LogView.Document.Blocks.Clear();
        _renderedOwners.Clear();
        _counts.Clear();

        // 噪音行连计数都不算，所以重建时也要按同一个规则过一遍
        var entries = LogBus.Entries.Where(e => !IsNoise(e)).ToList();
        foreach (var entry in entries)
        {
            _counts[entry.Owner] = Count(entry.Owner) + 1;
        }

        var limit = CurrentLimit();
        var start = limit > 0 && entries.Count > limit ? entries.Count - limit : 0;
        var runOwner = LogLevel.None;
        var runTexts = new List<string>();
        // ⚠️ 合并的键要**连"是不是启动器消息"一起比**：两者颜色不同，
        //    只按级别合并的话，一段"级别相同但来源不同"的行会被染成同一种颜色
        var runLauncher = false;

        void Flush()
        {
            if (runTexts.Count == 0) return;
            // 合并成一次写入：几百上千行时比逐行 Add 快好几倍（1.0.0 同款做法）
            WriteRun(string.Join("\n", runTexts), runOwner, runLauncher);
            runTexts.Clear();
        }

        for (var i = start; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (!IsLevelVisible(entry.Owner)) continue;
            if (entry.Owner != runOwner || entry.IsLauncher != runLauncher)
            {
                Flush();
                runOwner = entry.Owner;
                runLauncher = entry.IsLauncher;
            }
            runTexts.Add(entry.Text);
        }
        Flush();

        RefreshCounts();

        if (AutoScrollCheck.IsChecked == true)
        {
            LogView.ScrollToEnd();
        }
        else if (scroll is not null)
        {
            scroll.ScrollToVerticalOffset(keep);
        }
    }

    /// <summary>把各级别计数刷到按钮文字上（`0 错误` 这种，量词用中文当 key）。</summary>
    private void RefreshCounts()
    {
        foreach (var (level, button) in _levelButtons)
        {
            button.Content = $"{Count(level)} {Localizer.Tr(LevelWord(level))}";
        }
    }

    /// <summary>清空（走 <see cref="LogBus.Clear"/>：总线是唯一的真相，多个窗口一起清）。</summary>
    private void ClearAll()
    {
        LogView.Document.Blocks.Clear();
        _renderedOwners.Clear();
        _counts.Clear();
        RefreshCounts();
        LogBus.Clear();
    }

    // ============================================================
    //  事件
    // ============================================================

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        Rebuild();
    }

    private void OnLimitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        Rebuild();
    }

    private void OnClear(object sender, RoutedEventArgs e) => ClearAll();

    /// <summary>
    /// 「结束游戏进程」。
    /// ⚠️ 先问一句再杀：游戏里没存盘的东西会丢，点错一下代价太大
    ///    （1.0.0 那边是首页弹 QMessageBox 问的，这里就地问）。
    /// </summary>
    private void OnStopGame(object sender, RoutedEventArgs e)
    {
        if (!App.Services.Launch.IsRunning) return;

        var answer = MessageBox.Show(
            this,
            Localizer.Tr("游戏还在运行，确定要结束它吗？没保存的进度会丢。"),
            Localizer.Tr("结束游戏进程"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        LogBus.WriteLauncher(Localizer.Tr("正在停止游戏…"));
        App.Services.Launch.Stop();
        RefreshStopButton();
    }

    /// <summary>
    /// 「导出」。
    ///
    /// ⚠️ 导出的是**全部**日志，不管当前藏了哪几级 ——
    ///    发问题报告时漏掉被藏起来的 DEBUG，往往就查不出来了（1.0.0 的原话）。
    ///    文件名也照那边：`Mosslight-log.txt`。
    /// </summary>
    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = Localizer.Tr("导出日志"),
            FileName = "Mosslight-log.txt",
            Filter = Localizer.Tr("文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*"),
            DefaultExt = ".txt",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            // ⚠️ newline 一律写 `\n`：日志是拿去贴到 issue / 论坛的，
            //    CRLF 在某些工具里会显示成 ^M（1.0.0 也是 newline="\n"）
            var body = LogBus.Export();
            File.WriteAllText(dialog.FileName, body);
            LogBus.WriteLauncher(Localizer.Tr("日志已导出：{path}", ("path", dialog.FileName)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, Localizer.Tr("导出失败"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnGameExited(object? sender, int code)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshStopButton);
            return;
        }
        RefreshStopButton();
    }

    /// <summary>没有游戏在跑时，「结束游戏进程」点不动（见类注释第 3 条）。</summary>
    private void RefreshStopButton()
        => StopButton.IsEnabled = App.Services is not null && App.Services.Launch.IsRunning;

    /// <summary>换了主题：把画刷缓存扔掉重画（写进 Run 的颜色不会跟着 DynamicResource 走）。</summary>
    public void RefreshTheme()
    {
        BuildBrushes();
        Rebuild();
    }

    // ============================================================
    //  收尾
    // ============================================================

    /// <summary>
    /// 关窗：**退订**所有事件。
    ///
    /// ⚠️ 不退订的话 <see cref="LogBus"/> 会一直攥着这个窗口 ——
    ///    它是静态的，订阅者活到进程结束，表现就是"关掉的窗口还在后台吃内存、
    ///    而且每次开一个新窗口就多一份"。
    ///
    /// ⚠️ 每个成员都要判空/判可用：**构造期抛异常时 WPF 照样会走 OnClosed**
    ///    （Application.DoShutdown 那条路），那一刻字段还是 null、
    ///    <c>App.Services</c> 可能也没建起来 —— 不判的话"开窗口失败"
    ///    会被第二个 NullReferenceException 盖掉，真正的原因就查不出来了（踩过）。
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        LogBus.Appended -= OnAppended;
        LogBus.Cleared -= OnCleared;
        if (App.Services is not null)
        {
            App.Services.Launch.GameExited -= OnGameExited;
        }
        _runStateTimer?.Stop();
        base.OnClosed(e);
    }

    /// <summary>清空事件（别的窗口点了清空，这边也要清）。</summary>
    private void OnCleared()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnCleared);
            return;
        }
        LogView.Document.Blocks.Clear();
        _renderedOwners.Clear();
        _counts.Clear();
        RefreshCounts();
    }

    /// <summary>取 RichTextBox 内部的 ScrollViewer（自动滚动要用它）。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }
}
