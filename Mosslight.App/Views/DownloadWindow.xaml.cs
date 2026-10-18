using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Mosslight.Core.Localization;
using Mosslight.Core.Services.Engine;

namespace Mosslight.App.Views;

/// <summary>
/// 下载进度窗口（移植自 1.0.0 的 `ui/dialogs/download_window.py`）。
///
/// 布局、三条"行要不要留"的规则、以及为什么是独立 Window 而不是对话框，
/// 都写在 `DownloadWindow.xaml` 顶部的注释里 —— 这里只说**数据怎么流动**：
///
///     DownloadManager.Snapshot()  ──►  DownloadMonitor.Last  ──►  这个窗口（200ms 轮询）
///
/// 服务层不把 manager 交出来（它是各方法里的局部变量），所以靠 Core 里那个
/// 广播点转一手。窗口**只读**：取消是转给 <see cref="DownloadMonitor.CancelAll"/> 的。
/// </summary>
public partial class DownloadWindow : Window
{
    /// <summary>最多列这么多行（几百个库全铺出来没意义，也卡）。</summary>
    private const int MaxRows = 12;

    /// <summary>下完的行再留这么久才撤（秒）。理由见 XAML 注释。</summary>
    private const double HideDoneSeconds = 0.8;

    /// <summary>轮询间隔（毫秒）。跟 1.0.0 的 `POLL_MS` 一致。</summary>
    private const int PollMs = 200;

    private readonly DispatcherTimer _timer;

    /// <summary>已经建出来的行（顺序 = 建出来的顺序）。</summary>
    private readonly List<Row> _rows = new();

    /// <summary>任务 → 它"刚下完"的时刻（`Stopwatch` 秒）。</summary>
    private readonly Dictionary<DownloadTask, double> _doneAt = new();

    private TextBlock? _moreLabel;

    /// <summary>取消过一次就别再让点（点了也不会再生效，留着只会让人反复点）。</summary>
    private bool _cancelRequested;

    /// <summary>定时器已经停了（跑完了）。外部再 SetStageNote 时不要再启动它。</summary>
    private bool _finished;

    /// <summary>
    /// 建一个下载窗口。
    /// </summary>
    /// <param name="title">装的是什么（`26.2` / `Sodium 1.0.1` / 整合包名）。</param>
    public DownloadWindow(string title)
    {
        InitializeComponent();

        var header = Localizer.Tr("正在安装 {name}", ("name", title));
        TitleText.Text = header;
        Title = $"{header} - {Core.AppInfo.AppName}";
        SubtitleText.Text = Localizer.Tr("准备中…");
        FooterText.Text = string.Empty;

        // ⚠️ 开窗先清掉上一批的残留：不清的话第一眼会看到上一次的 100%，
        //    像"刚点开始就已经下完了"（见 DownloadMonitor.Reset 的注释）。
        DownloadMonitor.Reset();

        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(PollMs),
        };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    /// <summary>这一秒在干什么（安装阶段那种"正在跑安装器…"，没有下载行的时候用）。</summary>
    public void SetStageNote(string note)
    {
        if (_finished || string.IsNullOrWhiteSpace(note))
        {
            return;
        }
        // ⚠️ 只在"没有真的在下载"时顶掉副标题：下载过程中那句
        //    「正在下载 N 个文件…」比任何阶段说明都有用。
        if (DownloadMonitor.Last is { Active: > 0 })
        {
            return;
        }
        SubtitleText.Text = note;
    }

    /// <summary>
    /// 装完了（或失败了）：停掉轮询，把结论写进副标题。
    ///
    /// ⚠️ 窗口**不自动关**：失败了要让人看得见是哪几个文件挂了。
    ///    调用方想关就自己 Close。
    /// </summary>
    public void Finish(string message)
    {
        _finished = true;
        _timer.Stop();
        // 收尾时再刷一次：最后那一行可能还卡在缓冲期里（定时器一停就再没人撤它）
        foreach (var task in _doneAt.Keys.ToList())
        {
            _doneAt[task] = -60;     // 让缓冲立即过期
        }
        Refresh();

        if (!string.IsNullOrWhiteSpace(message))
        {
            SubtitleText.Text = message;
        }
        CancelButton.IsEnabled = false;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_cancelRequested)
        {
            return;
        }
        _cancelRequested = true;
        CancelButton.IsEnabled = false;
        SubtitleText.Text = Localizer.Tr("正在取消…（会把没下完的临时文件删掉）");
        DownloadMonitor.CancelAll();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 把右边的行列表滚到底。
    ///
    /// ⚠️ 只有 `--page downloadwindow` 那条**截图**路径会用：列表默认停在最上面，
    ///    而"还有 N 个文件没列出来"这句在最底下 —— 不滚下去，截图就永远证明不了
    ///    那句话排得对不对。用户正常使用时不会走到这里。
    /// </summary>
    public void ScrollRowsToEnd()
    {
        // 等一轮布局：刚加进去的行还没有实际高度，立刻滚只会滚到 0
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            RowsScroll.UpdateLayout();
            RowsScroll.ScrollToEnd();
        }));
    }

    protected override void OnClosed(EventArgs e)
    {
        // ⚠️ 只停定时器：下载**继续**（管理器不归窗口管，界面那边会收到结果）。
        //    不停的话窗口销毁之后定时器还在往回调，会对着已销毁的控件赋值。
        _timer.Stop();
        base.OnClosed(e);
    }

    // ============================================================
    //  刷新（每 200ms 一次）
    // ============================================================

    private void Refresh()
    {
        var snapshot = DownloadMonitor.Last;
        if (snapshot is null)
        {
            SubtitleText.Text = Localizer.Tr("准备中…");
            return;
        }

        SyncRows(snapshot);
        foreach (var row in _rows)
        {
            row.Refresh();
        }
        RefreshStats(snapshot);
        if (!_cancelRequested)
        {
            RefreshSubtitle(snapshot);
        }
        RefreshFooter(snapshot);

        // ⚠️ 这里**故意不因为"这一批下完了"就停掉定时器**（以前会停）。
        //
        //    装整合包是**多批**下载：包里的文件 → **原版**（几百上千个库和资源）→ 加载器。
        //    每一批都是独立的 `DownloadManager`，跑完各自 `Finished = true`。
        //    定时器一停，后面那批就再也没人刷新窗口 —— 用户看到进度条卡在 100%、
        //    剩 0 个文件，可机器还在下 686 MB（2026-10-18 用户报的
        //    「下载游戏文件没走下载窗口」就是这个）。
        //
        //    定时器只在 `Finish()`（整件事结束）和窗口关闭时停。
        //    空转的代价很小：`Row.Refresh` 有"值没变就不动控件"的短路，
        //    `SyncRows` 只是比一遍任务表。
    }

    /// <summary>
    /// 决定哪些任务该出现在列表里，然后**增量**增删。
    ///
    /// 规则（照 1.0.0，一条都别省）：
    ///   · 已存在（<see cref="DownloadTask.Skipped"/>）→ 不列
    ///   · 完成 → 再留 <see cref="HideDoneSeconds"/> 秒，然后撤掉
    ///   · 失败 / 取消 → **一直留着**
    ///   · 下载中 / 等待中 → 留着
    /// </summary>
    private void SyncRows(DownloadSnapshot snapshot)
    {
        var now = Now();

        var eligible = new List<DownloadTask>();
        foreach (var task in snapshot.Tasks)
        {
            if (task.Skipped)
            {
                continue;
            }
            if (task.State == DownloadManager.StateDone)
            {
                if (!_doneAt.TryGetValue(task, out var at))
                {
                    _doneAt[task] = at = now;
                }
                if (now - at < HideDoneSeconds)
                {
                    eligible.Add(task);
                }
                continue;
            }
            eligible.Add(task);
        }

        var want = eligible.Take(MaxRows).ToList();

        // ① 该撤的行撤掉
        for (var i = _rows.Count - 1; i >= 0; i--)
        {
            if (!want.Contains(_rows[i].Task))
            {
                RowsHost.Children.Remove(_rows[i].Visual);
                _rows.RemoveAt(i);
            }
        }

        // ② 该补的行补上（撤掉一行之后，把后面等着的那行顶上来）。
        //    新任务在任务表里一定排在现有的后面，所以直接追加就能保持顺序。
        foreach (var task in want)
        {
            if (_rows.Any(r => ReferenceEquals(r.Task, task)))
            {
                continue;
            }
            var row = new Row(task, this);
            _rows.Add(row);
            RowsHost.Children.Add(row.Visual);
        }

        // ③「还有 N 个文件没列出来」永远贴在最后一行下面（1.0.0 是插在列表中间，见 XAML 注释）
        SyncMoreLabel(Math.Max(0, eligible.Count - _rows.Count));
    }

    private void SyncMoreLabel(int need)
    {
        if (need <= 0)
        {
            if (_moreLabel is not null)
            {
                RowsHost.Children.Remove(_moreLabel);
                _moreLabel = null;
            }
            return;
        }

        _moreLabel ??= new TextBlock
        {
            Style = (Style)FindResource("HintText"),
            Margin = new Thickness(2, 0, 0, 2),
        };
        _moreLabel.Text = Localizer.Tr("还有 {n} 个文件没列出来", ("n", need));

        // 位置：永远在最后（先摘再加，避免跟"新行追加"抢顺序）
        RowsHost.Children.Remove(_moreLabel);
        RowsHost.Children.Add(_moreLabel);
    }

    private void RefreshStats(DownloadSnapshot snapshot)
    {
        if (snapshot.Percent is { } percent)
        {
            TotalValue.Text = $"{percent * 100:F2} %";
            TotalBar.Value = (int)Math.Round(percent * 1000);
        }
        else
        {
            // 总大小还不知道：把已下的字节数亮出来，别显示一个假的 0%
            TotalValue.Text = snapshot.Bytes > 0 ? DownloadManager.HumanSize(snapshot.Bytes) : "—";
            TotalBar.Value = 0;
        }

        SpeedValue.Text = DownloadManager.HumanSpeed(snapshot.Speed);

        // 「剩余文件」= 还没收尾的任务数（已存在的算完成，所以不计入）
        LeftValue.Text = Math.Max(0, snapshot.Count - snapshot.Done).ToString();
    }

    private void RefreshSubtitle(DownloadSnapshot snapshot)
    {
        if (snapshot.Active > 0)
        {
            var text = Localizer.Tr("正在下载 {n} 个文件…", ("n", snapshot.Active));
            // 镜像在熔断冷却中时补一句：不说的话进度看着像卡住，用户会以为程序死了
            if (snapshot.MirrorOff)
            {
                text += "　" + Localizer.Tr("镜像源不通，已改用官方源继续下载…");
            }
            SubtitleText.Text = text;
            return;
        }

        if (snapshot.Finished)
        {
            SubtitleText.Text = snapshot.Failed > 0
                ? Localizer.Tr("下载结束，但有 {n} 个文件失败", ("n", snapshot.Failed))
                : Localizer.Tr("全部完成");
            return;
        }

        SubtitleText.Text = Localizer.Tr("准备中…");
    }

    private void RefreshFooter(DownloadSnapshot snapshot)
    {
        // 显示"当前在下的那个"的落点；没有在下的就显示第一个还没跑完的
        var pick = snapshot.Tasks.FirstOrDefault(t => t.Active)
                   ?? snapshot.Tasks.FirstOrDefault(t => !t.Finished);
        FooterText.Text = pick is null
            ? string.Empty
            : Localizer.Tr("保存到：{path}", ("path", pick.Dest));
        FooterText.ToolTip = FooterText.Text;
    }

    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    // ============================================================
    //  一行
    // ============================================================

    /// <summary>一行：`[库] xxx.jar` + 右侧 `已下 / 总共` + 下面那根细条。</summary>
    private sealed class Row
    {
        private readonly TextBlock _state;
        private readonly ProgressBar _bar;
        private (string Text, double? Percent)? _last;

        public Row(DownloadTask task, FrameworkElement owner)
        {
            Task = task;

            var name = new TextBlock
            {
                // `[库] xxx.jar`：跟 1.0.0 的显示一致
                Text = task.Kind.Length > 0 ? $"[{task.Kind}] {task.Label}" : task.Label,
                Style = (Style)owner.FindResource("DlRowName"),
            };
            // 名字太长被截断时，悬停能看到完整路径
            name.ToolTip = task.Dest;

            _state = new TextBlock { Style = (Style)owner.FindResource("DlRowState") };

            var top = new DockPanel();
            DockPanel.SetDock(_state, Dock.Right);
            top.Children.Add(_state);
            top.Children.Add(name);

            _bar = new ProgressBar
            {
                Style = (Style)owner.FindResource("BaseProgressBar"),
                Minimum = 0,
                Maximum = 1000,
                Margin = new Thickness(0, 6, 0, 0),
            };

            var stack = new StackPanel();
            stack.Children.Add(top);
            stack.Children.Add(_bar);

            Visual = new Border
            {
                Style = (Style)owner.FindResource("DlRow"),
                Child = stack,
            };
        }

        public DownloadTask Task { get; }

        public Border Visual { get; }

        public void Refresh()
        {
            var percent = Task.Percent;
            var text = StateText();
            // 值没变就别动控件：几百个文件每 200ms 无脑刷一遍是纯浪费
            if (_last == (text, percent))
            {
                return;
            }
            _last = (text, percent);

            _state.Text = text;
            // ⚠️ 总大小还不知道（服务器没给 Content-Length）时**不做走马灯**：
            //    主题里的进度条模板只有一条静态指示条，没有 indeterminate 动画，
            //    设了 IsIndeterminate 也看不出来。把已下字节数写在右边（见 StateText）
            //    已经足够说明"在动"。
            _bar.Value = percent is { } p ? (int)Math.Round(p * 1000) : 0;

            var resources = Application.Current.Resources;
            _state.Foreground = Failure()
                ? (Brush)resources["DangerBrush"]
                : Task.State == DownloadManager.StateDone
                    ? (Brush)resources["AccentBrush"]
                    : (Brush)resources["TextDimBrush"];
        }

        private bool Failure()
            => Task.Error.Length > 0 || Task.State == DownloadManager.StateFailed;

        /// <summary>
        /// 右侧那句话：「已下 / 总共」（`0 B / 2.6 MB`），照 1.0.0 的实验版。
        ///
        /// ⚠️ 唯一的例外是出错：那边原本也显示字节数，等于把失败藏起来了 ——
        ///    这里直接显示原因（并且变红），不然用户根本不知道为什么少了文件。
        /// </summary>
        private string StateText()
        {
            if (Failure())
            {
                return Task.Error.Length > 0 ? Task.Error : Localizer.Tr("失败");
            }
            return $"{DownloadManager.HumanSize(Task.DoneBytes)} / {DownloadManager.HumanSize(Task.Total)}";
        }
    }
}
