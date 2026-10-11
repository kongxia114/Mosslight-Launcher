using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Mosslight.App.Views;

/// <summary>
/// 可复用的加载状态条（「正在获取版本列表…」+ 来回跑的不确定进度条）。
///
/// 移植自 1.0.0 的 `ui/widgets/loading_bar.py`，连两个"踩过坑才有的"细节一起搬：
///
/// ## 一、最少显示 180ms（<see cref="MinVisibleMs"/>）
/// 网络快的时候（命中缓存 / 本地网络好）请求可能 40ms 就回来了。一来一回就立刻
/// 收起的话，加载条会**闪一下**再消失，比不显示还难看。所以 <see cref="HideWithDelay"/>
/// 会保证至少显示这么久再收。
///
/// ## 二、显示状态**自己记**，不看 <c>Visibility</c>
/// 父页面还没被切过来（或者窗口还没 Show）时，子控件的 `IsVisible` 永远是 false。
/// 1.0.0 那边就因为这个撞过 bug：`hide_now()` 以为"本来就没显示"直接 return，
/// 显示标记留着，等用户真翻到这一页看到的是**一条永远转下去的加载条**。
/// 所以这里用 <see cref="_shown"/> 记，<see cref="IsLoading"/> 也看它。
///
/// ## 用法
/// <code>
/// Loading.ShowState(Localizer.Tr("正在获取版本列表…"));
/// try { ... await 网络 ... }
/// finally { Loading.HideWithDelay(); }   // 失败也要收：错误信息有告警条负责说
/// </code>
/// ⚠️ 一定要放 finally：漏一条分支就会留一条永远转下去的条。
/// </summary>
public partial class LoadingBar : UserControl
{
    /// <summary>至少显示这么久再允许隐藏（毫秒，= 1.0.0 的 MIN_VISIBLE_MS）。</summary>
    public const int MinVisibleMs = 180;

    /// <summary>展开动画时长。</summary>
    private const int ShowAnimMs = 140;

    /// <summary>收起动画时长（= 1.0.0 的 HIDE_ANIM_MS）。</summary>
    private const int HideAnimMs = 140;

    /// <summary>chunk 跑一个来回的时长（一个方向 = 一半）。</summary>
    private const int SweepMs = 1700;

    /// <summary>chunk 占槽宽的比例。</summary>
    private const double ChunkRatio = 0.3;

    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _hideDoneTimer;
    private readonly DoubleAnimation _sweep;

    /// <summary>本次显示是什么时候开始的（算"最少显示 180ms"用）。</summary>
    private DateTime _shownAtUtc;

    /// <summary>
    /// "该不该显示"（**不是** Visibility）。理由见类注释第二条。
    /// </summary>
    private bool _shown;

    /// <summary>正在收起动画里（这期间再来一次 ShowState 要能把动画掰回来）。</summary>
    private bool _hiding;

    /// <summary>
    /// 第几次显示。<see cref="_hideTimer"/> 是延迟触发的，触发时得知道
    /// "这 180ms 里用户是不是又发起了一次加载" —— 对不上就丢弃这次收起。
    /// </summary>
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

    /// <summary>
    /// 显示出来（已经在显示就只换文字，动画不重启 —— 重启会让块跳回起点）。
    ///
    /// <paramref name="text"/> 不给就沿用上一次的文字。
    /// </summary>
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
        //    （比如搜索 A 没回来又搜了 B）不该把已经显示的时间清零 ——
        //    清零的话两次连着的短请求会一直推迟收起。
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

    /// <summary>
    /// 收起来。显示时间不足 <see cref="MinVisibleMs"/> 的话延迟收（防"闪一下"）。
    ///
    /// <paramref name="text"/> 是"收起来的时候顺手把最后一句状态留下"用的
    /// （比如「加载失败」），1.0.0 的 hide_now 也有这个参数。
    /// </summary>
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

    /// <summary>立刻收起（不等最少显示时间）。页面被切走之类"不用再演了"的场合用。</summary>
    public void HideNow()
    {
        _hideTimer.Stop();
        BeginHide(_generation);
    }

    /// <summary>
    /// 正在显示加载态（调用方和测试用，省得去猜 Visibility）。
    ///
    /// ⚠️ 看的是 <see cref="_shown"/> 而不是 <c>Visibility</c>：父页面没显示时
    /// <c>IsVisible</c> 恒为 false，那样这个属性会撒谎。
    /// </summary>
    public bool IsLoading => _shown && !_hiding;

    // ============================================================
    //  内部
    // ============================================================

    private void StartSweep()
    {
        Chunk.Visibility = Visibility.Visible;
        RestartSweep();
    }

    /// <summary>
    /// 按当前槽宽重算 chunk 宽度、动画起点终点，然后从头跑。
    ///
    /// ⚠️ 尺寸一变（窗口拉伸、DPI 变化、第一次布局）就必须重来：
    ///    动画的 To 是**算出来的绝对像素**，不重来的话块会跑出槽外或者半路停住。
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

    /// <summary>
    /// 收起：高度动到 0（<see cref="HideAnimMs"/>），时长一到就 Collapsed。
    ///
    /// ⚠️ **状态切换挂在定时器（<see cref="_hideDoneTimer"/>）上，不挂动画的 Completed**
    ///    —— 这是实测踩出来的：最初用 `animation.Completed` 收尾，结果它在这个控件上
    ///    **根本没触发**（加载条做完搜索就永远挂在页面上，截图里看得清清楚楚）。
    ///    高度动画只负责"好看"，"该收起来了"这件事必须有一个一定会发生的信号；
    ///    定时器的间隔就是动画时长，视觉上完全对得上。
    /// </summary>
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
        //    （构造后从没布局过时 ActualHeight 是 0，那时没有"动"可言，直接收。）
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

    /// <summary>
    /// 真的收起来（由 <see cref="_hideDoneTimer"/> 在收起动画演完后调）。
    /// </summary>
    private void AfterHide(int generation)
    {
        // "演完了"这段时间里用户可能又发起了一次加载（generation 变了）——
        // ⚠️ 那种情况下**不能**把条收掉，但也必须把 _hiding 放掉：
        //    不放的话 HideWithDelay 开头那个 `if (_hiding) return` 会把
        //    之后的每一次收起都吞掉 —— 加载条就永远转下去了
        //    （正是类注释里"永远转下去的加载条"那个 bug 的另一种进来方式）。
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

    /// <summary>
    /// 「正在加载…」—— 1.0.0 的默认文案（**源文案，调用方自己过 Localizer.Tr**）。
    /// </summary>
    public const string DefaultText = "正在加载…";
}
