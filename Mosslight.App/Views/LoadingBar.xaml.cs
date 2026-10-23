using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Mosslight.App.Views;

public partial class LoadingBar : UserControl
{
    /// <summary>至少显示这么久再允许隐藏。</summary>
    public const int MinVisibleMs = 180;

    // 收起动画时长。
    private const int HideAnimMs = 140;

    // chunk 跑一个来回的时长（一个方向 = 一半）。
    private const int SweepMs = 1700;

    // chunk 占槽宽的比例。
    private const double ChunkRatio = 0.3;

    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _hideDoneTimer;
    private readonly DoubleAnimation _sweep;

    // 本次显示是什么时候开始的（算"最少显示 180ms"用）。
    private DateTime _shownAtUtc;

    // "该不该显示"（**不是** Visibility）。
    private bool _shown;

    // 正在收起动画里（这期间再来一次 ShowState 要能把动画掰回来）。
    private bool _hiding;

    private int _generation;

    public LoadingBar()
    {
        InitializeComponent();

        // 覆盖整个控件：来回跑的那块是横着动的，只在发生尺寸变化时重算
        _sweep = new DoubleAnimation
        {
            Duration = new Duration(TimeSpan.FromMilliseconds(SweepMs)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            // 三次缓动：起步和收尾慢一点，中间快 —— 平缓，不像匀速那样"撞墙"
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        TrackHost.SizeChanged += (_, _) => RestartSweep();

        _hideTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(MinVisibleMs),
        };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            BeginHide(_generation);
        };

        // "收起动画演完了"的信号（**不是**动画的 Completed，理由见 BeginHide）
        _hideDoneTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(HideAnimMs),
        };
        _hideDoneTimer.Tick += (_, _) =>
        {
            _hideDoneTimer.Stop();
            AfterHide(_generation);
        };
    }

    /// <summary>显示出来（已经在显示就只换文字，动画不重启 —— 重启会让块跳回起点）。</summary>
    public void ShowState(string? text = null)
    {
        if (!string.IsNullOrEmpty(text))
        {
            Label.Text = text;
        }

        _generation++;
        _hideTimer.Stop();
        // 上一次收起的"演完了"信号还没到就又被叫起来了：作废它
        // （generation 已经变了，它醒了也只会把 _hiding 放掉）
        _hideDoneTimer.Stop();
        _hiding = false;
        // ⚠️ 起点只在"真的从隐藏变成显示"时重置。已经是显示态时再来一次
        // （比如搜索 A 没回来又搜了 B）不该把已经显示的时间清零 ——
        // 清零的话两次连着的短请求会一直推迟收起。
        if (!_shown)
        {
            _shownAtUtc = DateTime.UtcNow;
        }
        _shown = true;

        Visibility = Visibility.Visible;
        MaxHeight = double.PositiveInfinity;

        // 收起动画可能正把高度往下带，先按住（上面已经把 MaxHeight 放开了）
        BeginAnimation(MaxHeightProperty, null);

        StartSweep();
    }

    public void HideWithDelay(string? text = null)
    {
        if (!string.IsNullOrEmpty(text))
        {
            Label.Text = text;
        }
        if (!_shown || _hiding)
        {
            return;
        }

        var elapsed = (DateTime.UtcNow - _shownAtUtc).TotalMilliseconds;
        if (elapsed < MinVisibleMs)
        {
            // 记下当前是第几次显示：定时器醒来时如果又显示过了，这次收起作废
            _hideTimer.Interval = TimeSpan.FromMilliseconds(MinVisibleMs - elapsed + 10);
            _hideTimer.Start();
            return;
        }
        BeginHide(_generation);
    }

    /// <summary>立刻收起（不等最少显示时间）。</summary>
    public void HideNow()
    {
        _hideTimer.Stop();
        BeginHide(_generation);
    }

    /// <summary>
    /// 正在显示加载态（调用方和测试用，省得去猜 Visibility）；⚠️ 看的是 _shown 而不是 Visibility：
    /// 父页面没显示时 IsVisible 恒为 false，那样这个属性会撒谎。
    /// </summary>
    public bool IsLoading => _shown && !_hiding;

    // 内部

    private void StartSweep()
    {
        Chunk.Visibility = Visibility.Visible;
        RestartSweep();
    }

    /// <summary>
    /// 按当前槽宽重算 chunk 宽度、动画起点终点，然后从头跑；⚠️ 尺寸一变（窗口拉伸、DPI 变化、第一
    /// 次布局）就必须重来：动画的 To 是**算出来的绝对像素**，不重来的话块会跑出槽外或者半路停住。
    /// </summary>
    private void RestartSweep()
    {
        var track = TrackHost.ActualWidth;
        if (track <= 0)
        {
            return;     // 还没布局出来，等 SizeChanged
        }

        var width = Math.Max(12.0, track * ChunkRatio);
        Chunk.Width = width;

        ChunkShift.BeginAnimation(TranslateTransform.XProperty, null);
        _sweep.From = 0;
        _sweep.To = Math.Max(0.0, track - width);
        ChunkShift.BeginAnimation(TranslateTransform.XProperty, _sweep);
    }

    private void BeginHide(int generation)
    {
        // 定时器是延迟触发的：醒来发现"这期间又显示过一次"就作废
        if (generation != _generation || !_shown || _hiding)
        {
            return;
        }
        _hiding = true;

        ChunkShift.BeginAnimation(TranslateTransform.XProperty, null);
        Chunk.Visibility = Visibility.Collapsed;

        // ⚠️ From 要按**当前**高度取：展开动画可能还没走完，写死上限值会"跳"一下。
        // （构造后从没布局过时 ActualHeight 是 0，那时没有"动"可言，直接收。）
        var from = ActualHeight > 0 ? ActualHeight : 0;
        if (from > 0)
        {
            BeginAnimation(MaxHeightProperty, new DoubleAnimation
            {
                From = from,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(HideAnimMs)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.Stop,
            });
        }

        _hideDoneTimer.Stop();
        _hideDoneTimer.Interval = TimeSpan.FromMilliseconds(HideAnimMs);
        _hideDoneTimer.Start();
    }

    /// <summary>真的收起来（由 _hideDoneTimer 在收起动画演完后调）。</summary>
    private void AfterHide(int generation)
    {
        // ⚠️ 代际变化时不能收起加载条，但必须清掉 `_hiding`，否则后续每次 HideWithDelay 都会被吞掉。
        if (generation != _generation)
        {
            _hiding = false;
            return;
        }

        _hiding = false;
        _shown = false;
        BeginAnimation(MaxHeightProperty, null);
        MaxHeight = 0;
        Visibility = Visibility.Collapsed;
    }

    /// <summary>「正在加载…」默认文案（源文案，调用方过 Localizer.Tr）。</summary>
    public const string DefaultText = "正在加载…";
}
