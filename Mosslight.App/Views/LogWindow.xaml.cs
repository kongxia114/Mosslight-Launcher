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

public partial class LogWindow : Window
{
    // 显示行数档位（0 = 不限）。
    private static readonly int[] LineLimits = { 2000, 5000, 20000, 0 };

    // 级别 → (主题画刷键, 是否加粗)；⚠️ Info 用 TextFaint（更亮一档）。
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

    // 各级别当前有多少条（计数按钮上的数字）。
    private readonly Dictionary<LogLevel, int> _counts = new();

    // 已经写进文档、但还没被行数上限裁掉的行数（裁的时候要按它删段落）。
    private readonly List<LogLevel> _renderedOwners = new();

    // 归属级别 → 渲染用的画刷（缓存：一行一个 Brush 太浪费）。
    private readonly Dictionary<LogLevel, Brush> _brushes = new();

    // `[启动器] ` 前缀那一段的颜色（强调色）。
    private Brush _launcherBrush = Brushes.Gray;

    // 「正在回填界面」。
    private bool _syncing = true;

    // 运行状态 → 「结束游戏进程」能不能点（见类注释第 3 条）。
    private DispatcherTimer? _runStateTimer;

    public LogWindow()
    {
        InitializeComponent();

        BuildLevelButtons();
        BuildLimitOptions();
        BuildBrushes();

        NoiseCheck.IsChecked = App.Settings.GetBool("log_noise_filter", true);

        // 先整批读历史，再订阅新的 —— **顺序不能反**：
        // 反过来的话，"订阅之后、读历史之前"来的那几行会被写两遍
        Rebuild();

        LogBus.Appended += OnAppended;
        LogBus.Cleared += OnCleared;

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

    // 建控件

    /// <summary>那排级别计数 / 过滤开关。</summary>
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

    /// <summary>行数下拉框（2000 / 5000 / 20000 / 全部）。</summary>
    private void BuildLimitOptions()
    {
        foreach (var limit in LineLimits)
        {
            LimitCombo.Items.Add(new ComboBoxItem
            {
                Content = limit == 0 ? Localizer.Tr("全部") : limit.ToString(),
                Tag = limit,
            });
        }
        LimitCombo.SelectedIndex = 0;
    }

    /// <summary>级别 → 画刷。</summary>
    private void BuildBrushes()
    {
        _brushes.Clear();
        foreach (var (level, (key, _)) in LevelColors)
        {
            _brushes[level] = TryFindResource(key) as Brush ?? Brushes.Gainsboro;
        }
        _launcherBrush = TryFindResource("AccentBrush") as Brush ?? Brushes.Gray;
    }

    /// <summary>级别 → 界面上的词。</summary>
    private static string LevelWord(LogLevel level) => level switch
    {
        LogLevel.Fatal => "致命",
        LogLevel.Error => "错误",
        LogLevel.Warn => "警告",
        LogLevel.Info => "信息",
        LogLevel.Debug => "调试",
        _ => "其他",
    };

    // 内容

    /// <summary>
    /// 新来一条；⚠️ LogBus.Appended 是在**调用方的线程**上触发的 （游戏输出那条路是后台线程），所
    /// 以这里必须切回 UI 线程。
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

    /// <summary>归属级别为空的行没有开关可点，永远显示。</summary>
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

    /// <summary>超过行数上限就从**最老的**开始丢。</summary>
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

    /// <summary>按当前过滤条件 + 当前主题**整篇重建**。</summary>
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
        // 只按级别合并的话，一段"级别相同但来源不同"的行会被染成同一种颜色
        var runLauncher = false;

        void Flush()
        {
            if (runTexts.Count == 0) return;
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

    /// <summary>清空（走 LogBus.Clear：总线是唯一的真相，多个窗口一起清）。</summary>
    private void ClearAll()
    {
        LogView.Document.Blocks.Clear();
        _renderedOwners.Clear();
        _counts.Clear();
        RefreshCounts();
        LogBus.Clear();
    }

    // 事件

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

    /// <summary>「结束游戏进程」。</summary>
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
    /// 「导出」；⚠️ 导出的是**全部**日志，不管当前藏了哪几级 —— 发问题报告时漏掉被藏起来的 DEBUG，
    /// 往往就查不出来了。
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
            // CRLF 在某些工具里会显示成 ^M
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

    /// <summary>
    /// 换了主题：把画刷缓存扔掉重画（写进 Run 的颜色不会跟着 DynamicResource 走）。
    /// </summary>
    public void RefreshTheme()
    {
        BuildBrushes();
        Rebuild();
    }

    // 收尾

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
