using System.Windows.Media.Animation;
using Mosslight.Core.Animation;

namespace Mosslight.App.Infrastructure;

public abstract class PythonOutEase : EasingFunctionBase
{
    /// <summary>子类在这里实现 **Python 那条缓出曲线 f(u)**，照抄 `pcl_ease.py` 的公式。</summary>
    protected abstract double OutCurve(double u);

    /// <summary>反演：把"缓出曲线"折成 WPF 要的 EaseInCore。</summary>
    protected override double EaseInCore(double normalizedTime)
        => 1.0 - OutCurve(1.0 - normalizedTime);
}

public sealed class ParametricBackEase : PythonOutEase
{
    private readonly double _s;

    /// <summary>XAML 用的无参构造。</summary>
    public ParametricBackEase() : this(0.10) { }

    public ParametricBackEase(double overshootRatio)
    {
        OvershootRatio = overshootRatio;
        _s = AnimationCurves.SForOvershoot(overshootRatio);
    }

    public double OvershootRatio { get; }

    /// <summary>反推出来的 s。</summary>
    public double S => _s;

    protected override double OutCurve(double t) => AnimationCurves.BackOut(t, _s);

    protected override System.Windows.Freezable CreateInstanceCore() => new ParametricBackEase(OvershootRatio);
}

/// <summary>
/// 参数化 Elastic（弹性）曲线 —— 对应 `pcl_ease.make_elastic_out(amplitude, period)`；⚠️ `asin`
/// 的参数卡在 1.0 上会出 -0.0 这种值，Python 特意用 `min(1.0, ...)` 往里收了一点，这里照抄 —— 否
/// 则 amp = 1 时相位会差一个 0 的符号。
/// </summary>
public sealed class ParametricElasticEase : PythonOutEase
{
    private readonly double _amplitude;
    private readonly double _period;

    /// <summary>XAML 用的无参构造。</summary>
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

    /// <summary>见 ParametricBackEase.CreateInstanceCore 的说明：每个具体类都得自己实现。</summary>
    protected override System.Windows.Freezable CreateInstanceCore() => new ParametricElasticEase(_amplitude, _period);
}

public sealed class ParametricDampedSineEase : PythonOutEase
{
    private readonly double _cycles;
    private readonly double _decay;

    /// <summary>XAML 用的无参构造。</summary>
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

    /// <summary>见 ParametricBackEase.CreateInstanceCore 的说明：每个具体类都得自己实现。</summary>
    protected override System.Windows.Freezable CreateInstanceCore()
        => new ParametricDampedSineEase(OvershootRatio, _cycles, _decay);
}

public sealed class ParametricPowerOutEase : PythonOutEase
{
    /// <summary>XAML 用的无参构造。</summary>
    public ParametricPowerOutEase() : this(3.0) { }

    public ParametricPowerOutEase(double power) => Power = power;

    public double Power { get; }

    protected override double OutCurve(double t) => AnimationCurves.PowerOut(t, Power);

    /// <summary>见 ParametricBackEase.CreateInstanceCore 的说明：每个具体类都得自己实现。</summary>
    protected override System.Windows.Freezable CreateInstanceCore() => new ParametricPowerOutEase(Power);
}

/// <summary>
/// PCL 旧版缓出回弹 —— 对应 `pcl_ease.make_pcl_back_out(power)`：
/// `f(t) = 1 - (1-t)^p · cos(1.5πt)`，其中 **p = 3 - 0.5·power**；⚠️ p 越大过冲越猛，但方向和"标
/// 准 Back"相反的那档（power=0）会退化成一条 1-(1-t)³·cos 的怪曲线 —— 取值只在 `PCL_BACK_POWER`
/// 那四个档里选。
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

    /// <summary>见 ParametricBackEase.CreateInstanceCore 的说明：每个具体类都得自己实现。</summary>
    protected override System.Windows.Freezable CreateInstanceCore() => new ParametricPclBackEase(Power);
}

/// <summary>
/// PCL 旧版弹性缓出 —— 对应 `pcl_ease.make_pcl_elastic_out(power)`：
/// `f(t) = 1 - 2^(-10·ln2·t) · cos(6.5π·t)`；⚠️ 它和 ParametricElasticEase 是**两套不同的公式**（
/// PCL 旧版 vs Penner），别互相替换：预设表里的 `elastic` 指的是 Penner 那条，`pcl_elastic` 才是
/// 这条。
/// </summary>
public sealed class ParametricPclElasticEase : PythonOutEase
{
    /// <summary>XAML 用的无参构造。</summary>
    public ParametricPclElasticEase() : this(2.0) { }

    public ParametricPclElasticEase(double power) => Power = power;

    public double Power { get; }

    protected override double OutCurve(double t) => AnimationCurves.PclElasticOut(t, Power);

    /// <summary>见 ParametricBackEase.CreateInstanceCore 的说明：每个具体类都得自己实现。</summary>
    protected override System.Windows.Freezable CreateInstanceCore() => new ParametricPclElasticEase(Power);
}
