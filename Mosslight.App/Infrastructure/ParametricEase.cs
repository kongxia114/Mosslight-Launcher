using System.Windows.Media.Animation;
using Mosslight.Core.Animation;

namespace Mosslight.App.Infrastructure;

/// <summary>
/// 「Python 的缓出曲线」→ WPF 缓动函数的适配基类。
///
/// ## ⚠️ 为什么不能直接把公式塞进 EaseInCore
///
/// WPF 的 <see cref="EasingFunctionBase.Ease"/> 会**按 EasingMode 再加工一次**
/// <see cref="EasingFunctionBase.EaseInCore"/> 的返回值，而我们的场景全是 EaseOut：
///
///     Ease(t) = 1 - EaseInCore(1 - t)
///
/// 所以若把 Python 的缓出公式 f 直接写进 EaseInCore，实际播放出来的会是
/// `1 - f(1-t)` —— 曲线被**左右镜像再上下翻转**，形状完全变了。这种错编译能过、
/// 跑起来也"挺顺"，但方向是反的，只有逐值比对才抓得住。
///
/// 因此这里统一做一次**反演**：子类照抄 Python 的 f（缓出），基类负责把它变成
/// EaseInCore(u) = 1 - f(1-u)。这样 `Ease(t)` 出来的数就跟 Python 一模一样，
/// 自检里比的就是同一件事。
///
/// （自检里有一条断言专门盯着这个约定：拿**内置** CubicEase 实测
/// `Ease(t) == 1-(1-t)^3`，也就是确认 WPF 确实做了上面那次翻转。）
///
/// ## 公式住在哪
///
/// **全部公式都在 <see cref="AnimationCurves"/>（Mosslight.Core）里**，这里的类只做
/// "包一层 WPF 外壳"。这么分是因为同一批公式还有第二个消费者：Core 的
/// <see cref="SlideInTrajectory"/>（回弹要分段、PCL 要两条叠加，塞不进一条缓动曲线）。
/// 两边共用一份实现，才不会出现"缓动曲线对一个样、轨迹采样对另一个样"。
///
/// ## 参数
///
/// 参数都在构造函数里给，对象是**不可变**的（<see cref="EasingFunctionBase"/> 本身
/// 是 Freezable，但这里没有依赖属性，随便冻结都行）。曲线对象每次动画现造，
/// 不共享 —— 见 EasingFactory 的说明。
/// </summary>
public abstract class PythonOutEase : EasingFunctionBase
{
    /// <summary>
    /// 子类在这里实现 **Python 那条缓出曲线 f(u)**，照抄 `pcl_ease.py` 的公式。
    /// 不用管 EasingMode —— 基类会把 1-f(1-t) 这层算好。
    /// </summary>
    protected abstract double OutCurve(double u);

    /// <summary>反演：把"缓出曲线"折成 WPF 要的 EaseInCore。</summary>
    protected override double EaseInCore(double normalizedTime)
        => 1.0 - OutCurve(1.0 - normalizedTime);
}

/// <summary>
/// 参数化 Back（过冲）曲线 —— 对应 `pcl_ease.make_back_out(s)`。
///
///     f(t) = u·u·((s+1)·u + s) + 1,   u = t - 1
///
/// ## 为什么不用 WPF 自带的 <see cref="BackEase"/>
///
/// WPF 的 Amplitude **不是过冲比例**，只是曲线公式里的一个系数：
/// `Amplitude = 1` 时实测过冲只有约 1.7%，而 Python 那边 `back:0.18` 要的是
/// **实打实过冲 18%**。两者对不上，所以这一条必须自己实现。
///
/// `s` 也不是写死的：Python 用 `s_for_overshoot()` **二分 40 次**反推
/// （峰值位置要解超越方程，没解析解）。C# 这边照抄同样的反推过程，
/// 否则会从小数点后第 5 位开始跟 Python 分叉 —— 那是逐点比对能抓到、肉眼绝对看不出的差别。
/// 参考值：overshoot = 0.18 → s ≈ 2.424242（WPF BackEase 的等效 Amplitude 是 1.0）。
/// </summary>
public sealed class ParametricBackEase : PythonOutEase
{
    private readonly double _s;

    /// <summary>XAML 用的无参构造。**别用它** —— 不给过冲比例就只能退成 Penner 默认值。</summary>
    public ParametricBackEase() : this(0.10) { }

    /// <param name="overshootRatio">想过冲多少比例（0.18 = 冲过头 18%）。</param>
    public ParametricBackEase(double overshootRatio)
    {
        OvershootRatio = overshootRatio;
        _s = AnimationCurves.SForOvershoot(overshootRatio);
    }

    public double OvershootRatio { get; }

    /// <summary>反推出来的 s（自检会拿它跟 Python 的 s_for_overshoot 比）。</summary>
    public double S => _s;

    protected override double OutCurve(double t) => AnimationCurves.BackOut(t, _s);

    /// <summary>
    /// ⚠️ <see cref="System.Windows.Freezable.CreateInstanceCore"/> 是抽象成员，而且
    /// <c>Freezable</c> 只认**具体类**里的实现 —— 写在抽象基类里不算数（CS0534），
    /// 每个曲线类都得各写一遍。它是给 WPF 内部克隆 Freezable 用的（<c>Clone()</c> 等）；
    /// 本类的参数都是构造时写死的只读字段、没有可变状态，克隆体不带参数也无妨。
    /// </summary>
    protected override System.Windows.Freezable CreateInstanceCore() => new ParametricBackEase(OvershootRatio);
}

/// <summary>
/// 参数化 Elastic（弹性）曲线 —— 对应 `pcl_ease.make_elastic_out(amplitude, period)`。
///
///     f(t) = amp · 2^(-10t) · sin((t - s) · 2π / per) + 1
///
/// 相位偏移 s 由 `_elastic_phase` 算：
///     per = period（缺省 0.3）、amp = max(1, amplitude)
///     s   = per / (2π) · asin(min(1, 1/amp))
///
/// ⚠️ `asin` 的参数卡在 1.0 上会出 -0.0 这种值，Python 特意用 `min(1.0, ...)` 往里收了一点，
/// 这里照抄 —— 否则 amp = 1 时相位会差一个 0 的符号。
/// </summary>
public sealed class ParametricElasticEase : PythonOutEase
{
    private readonly double _amplitude;
    private readonly double _period;

    /// <summary>XAML 用的无参构造（等于 Python 的 `make_elastic_out()` 默认参数）。</summary>
    public ParametricElasticEase() : this(1.0, 0.3) { }

    public ParametricElasticEase(double amplitude = 1.0, double period = 0.3)
    {
        // 照抄 _elastic_phase：amp 至少 1（小于 1 会得到反相的曲线），period 非正数退回 0.3
        _amplitude = Math.Max(1.0, amplitude);
        _period = period > 0 ? period : 0.3;
        Phase = AnimationCurves.ElasticPhaseOf(_amplitude, _period).S;
    }

    public double Amplitude => _amplitude;

    public double Period => _period;

    public double Phase { get; }

    protected override double OutCurve(double t) => AnimationCurves.ElasticOut(t, _amplitude, _period);

    /// <summary>见 <see cref="ParametricBackEase.CreateInstanceCore"/> 的说明：每个具体类都得自己实现。</summary>
    protected override System.Windows.Freezable CreateInstanceCore() => new ParametricElasticEase(_amplitude, _period);
}

/// <summary>
/// 阻尼正弦（弹簧）曲线 —— 对应 `pcl_ease.make_damped_sine(overshoot_ratio, cycles, decay)`。
///
///     t_peak = 1/(2π·cycles) · atan(2π·cycles / decay)
///     peak   = e^(-decay·t_peak) · sin(2π·cycles·t_peak)
///     scale  = overshoot_ratio / peak                （把过冲归一化到给定比例）
///     shaped = 1 - (1-t)^2.2                         （前段用幂次加速起步）
///     f(t)   = 1 - scale · e^(-decay·shaped) · sin(2π·cycles·shaped)
///
/// 参数 `cycles = 1.15` / `decay = 5.0` 就是 `slide_in.py` 的
/// `SPRING_CYCLES` / `SPRING_DECAY`，照抄。
///
/// ⚠️ 两个容易搞混的地方：
///   1. `slide_in.py` 里那条弹簧**动画**用的是同一个包络，但形状是 0→0 的 bump
///      （`-scale·e^(-λt)·sin(...)`，t=0 时已就位、往负方向冲出去再收敛回 0）。
///      这里要的是**缓动曲线**，是 0→1 的那条。别把两者对调
///      （0→0 的那条在 <see cref="SlideInTrajectory.SpringBump"/>，轨迹引擎用它）。
///   2. `overshoot_ratio = 0.14` **不等于**最终过冲 14%：`scale` 是按"**未加 shaped
///      整形前**的包络峰值"归一化的，而实际曲线走的是 shaped 后的时间轴，
///      实测峰值过冲只有约 1.6%。这是 1.0.0 的原样行为（不是这次移植的误差），
///      C# 这边照抄，自检里也把这个峰值记下来了。
/// </summary>
public sealed class ParametricDampedSineEase : PythonOutEase
{
    private readonly double _cycles;
    private readonly double _decay;

    /// <summary>XAML 用的无参构造（等于 Python 的 `make_damped_sine()` 默认参数）。</summary>
    public ParametricDampedSineEase() : this(0.12, 1.15, 5.0) { }

    public ParametricDampedSineEase(double overshootRatio = 0.12, double cycles = 1.15, double decay = 5.0)
    {
        OvershootRatio = overshootRatio;
        _cycles = cycles;
        _decay = decay;
    }

    public double OvershootRatio { get; }

    public double Cycles => _cycles;

    public double Decay => _decay;

    protected override double OutCurve(double t) => AnimationCurves.DampedSine(t, OvershootRatio, _cycles, _decay);

    /// <summary>见 <see cref="ParametricBackEase.CreateInstanceCore"/> 的说明：每个具体类都得自己实现。</summary>
    protected override System.Windows.Freezable CreateInstanceCore()
        => new ParametricDampedSineEase(OvershootRatio, _cycles, _decay);
}

/// <summary>
/// 幂次缓出曲线 —— 对应 `pcl_ease.make_power_out(power)`：f(t) = 1 - (1-t)^p。
///
/// ## 为什么需要它（以及一个被实测推翻的旧说法）
/// `core/anim_prefs.py` 里的 `EASE_QUINT` 走的是 **Qt 内置的 `OutQuint`**，
/// 而 Qt 的 OutQuint **就是** `1-(1-t)^5`。
///
/// ⚠️ 工程里原来说"WPF 的 `QuinticEase` 不是这条公式，0.5 处只有 0.94921875"——
///    **实测是错的**：WPF 的 `QuinticEase`（EaseOut）逐点都等于 `1-(1-t)^5`，
///    最大偏差 1e-16（自检里有一条断言专门盯着这件事）。所以本类**不是**在修一个数值错误，
///    它的价值是"公式只留一份"：PCL 那条"位移 A"（`_track(a0, 0, fast_ms, OutQuint)`）
///    和 `EASE_QUINT` 用的是同一条公式，现在都出自
///    <see cref="AnimationCurves.PowerOut"/>，不会再出现"两处各写一遍、注释还对不上"。
/// </summary>
public sealed class ParametricPowerOutEase : PythonOutEase
{
    /// <summary>XAML 用的无参构造（= Python 的 `make_power_out()` 默认 power=3）。</summary>
    public ParametricPowerOutEase() : this(3.0) { }

    public ParametricPowerOutEase(double power) => Power = power;

    public double Power { get; }

    protected override double OutCurve(double t) => AnimationCurves.PowerOut(t, Power);

    /// <summary>见 <see cref="ParametricBackEase.CreateInstanceCore"/> 的说明：每个具体类都得自己实现。</summary>
    protected override System.Windows.Freezable CreateInstanceCore() => new ParametricPowerOutEase(Power);
}

/// <summary>
/// PCL 旧版缓出回弹 —— 对应 `pcl_ease.make_pcl_back_out(power)`：
/// `f(t) = 1 - (1-t)^p · cos(1.5πt)`，其中 **p = 3 - 0.5·power**。
///
/// 这是 1.0.0「PCL 原味」那一档里**慢轨**用的曲线
/// （`_build_pcl()` 的 `pcl_ease.named("pcl_back", power=2.0)`）——
/// 也就是那一档"结尾轻轻一顿"的来源。p = 2.0 时过冲 **17.7663%**、
/// 峰值落在 u ≈ **0.5143**（不是 Qt OutBack 的 t≈0.58，也不是 Penner 的 0.528）：
/// 形状本身就不一样，光调 Qt 的 `setOvershoot()` 补不出来。
/// （这组数是拿真 `pcl_ease.make_pcl_back_out(2.0)` 以 1/200000 步长扫出来的，
///   探针 `anim_probe_py.py` 里也逐点采了一份。）
///
/// ⚠️ p 越大过冲越猛，但方向和"标准 Back"相反的那档（power=0）会退化成一条
/// 1-(1-t)³·cos 的怪曲线 —— 取值只在 `PCL_BACK_POWER` 那四个档里选。
/// </summary>
public sealed class ParametricPclBackEase : PythonOutEase
{
    /// <summary>XAML 用的无参构造（= PCL 的默认力度 middle 之外最常用的 power=2.0）。</summary>
    public ParametricPclBackEase() : this(2.0) { }

    public ParametricPclBackEase(double power) => Power = power;

    public double Power { get; }

    /// <summary>曲线里真正用的指数 p = 3 - 0.5·power（自检会盯它）。</summary>
    public double Exponent => 3.0 - 0.5 * Power;

    protected override double OutCurve(double t) => AnimationCurves.PclBackOut(t, Power);

    /// <summary>见 <see cref="ParametricBackEase.CreateInstanceCore"/> 的说明：每个具体类都得自己实现。</summary>
    protected override System.Windows.Freezable CreateInstanceCore() => new ParametricPclBackEase(Power);
}

/// <summary>
/// PCL 旧版弹性缓出 —— 对应 `pcl_ease.make_pcl_elastic_out(power)`：
/// `f(t) = 1 - 2^(-10·ln2·t) · cos(6.5π·t)`。
///
/// 用的是一条闭式曲线（常数 `ln2*10` 和 `pi*6.5`），p = power + 4 决定弹几下：
/// p 越大过冲越大（1.394 → 1.470），而且**峰值位置会往左移**（0.508 → 0.307），
/// 所以手感从"软弹"变成"硬弹簧"。
///
/// ⚠️ 它和 <see cref="ParametricElasticEase"/> 是**两套不同的公式**（PCL 旧版 vs Penner），
/// 别互相替换：预设表里的 `elastic` 指的是 Penner 那条，`pcl_elastic` 才是这条。
/// </summary>
public sealed class ParametricPclElasticEase : PythonOutEase
{
    /// <summary>XAML 用的无参构造。</summary>
    public ParametricPclElasticEase() : this(2.0) { }

    public ParametricPclElasticEase(double power) => Power = power;

    public double Power { get; }

    protected override double OutCurve(double t) => AnimationCurves.PclElasticOut(t, Power);

    /// <summary>见 <see cref="ParametricBackEase.CreateInstanceCore"/> 的说明：每个具体类都得自己实现。</summary>
    protected override System.Windows.Freezable CreateInstanceCore() => new ParametricPclElasticEase(Power);
}
