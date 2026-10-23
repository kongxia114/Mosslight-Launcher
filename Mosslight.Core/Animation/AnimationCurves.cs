using System.Globalization;

namespace Mosslight.Core.Animation;

public static class AnimationCurves
{
    /// <summary>Penner 原版 Back 的系数（≈1.70158），照抄 `pcl_ease.BACK_S`。</summary>
    public const double BackS = 1.70158;

    /// <summary>照抄 `pcl_ease._clamp01`：Qt 只把 0~1 喂给自定义曲线。</summary>
    public static double Clamp01(double t) => t < 0 ? 0.0 : (t > 1 ? 1.0 : t);

    // 幂次缓动（Penner）：pow 的指数就是"力度"

    /// <summary>`make_power_in(power)`：f(t) = t^p。</summary>
    public static double PowerIn(double t, double power = 3.0) => Math.Pow(t, power);

    /// <summary>`make_power_out(power)`：f(t) = 1 - (1-t)^p。</summary>
    public static double PowerOut(double t, double power = 3.0) => 1.0 - Math.Pow(1.0 - t, power);

    /// <summary>
    /// `make_power_in_out(power)`：两半各自幂次，中间接上（= Qt 的 InOutCubic，p=3）。
    /// </summary>
    public static double PowerInOut(double t, double power = 3.0)
    {
        if (t < 0.5) return Math.Pow(2 * t, power) / 2.0;
        return 1.0 - Math.Pow(2 * (1 - t), power) / 2.0;
    }

    // Back：先往反方向退一点再冲过去。s 越大过冲越狠

    /// <summary>`make_back_in(s)`：f(t) = t·t·((s+1)·t - s)。</summary>
    public static double BackIn(double t, double s = BackS) => t * t * ((s + 1.0) * t - s);

    /// <summary>`make_back_out(s)`：u = t-1；f(t) = u·u·((s+1)·u + s) + 1。</summary>
    public static double BackOut(double t, double s = BackS)
    {
        var u = t - 1.0;
        return u * u * ((s + 1.0) * u + s) + 1.0;
    }

    /// <summary>`make_back_in_out(s)`：s2 = s·1.525，两半。</summary>
    public static double BackInOut(double t, double s = BackS)
    {
        var s2 = s * 1.525;
        if (t < 0.5)
        {
            var u = 2 * t;
            return u * u * ((s2 + 1.0) * u - s2) / 2.0;
        }
        var v = 2 * t - 2;
        return (v * v * ((s2 + 1.0) * v + s2) + 2.0) / 2.0;
    }

    /// <summary>
    /// `overshoot_for_s(s)`：给定 s 时实际过冲多少（超出终点的部分）；⚠️ 峰值是**采样**出来的（i
    /// = 1..199，步长 1/200），不是解析解 —— 采样步长和点数都要跟 Python 一样，不然反推出来的 s
    /// 会有微小偏差。
    /// </summary>
    public static double OvershootForS(double s)
    {
        if (s <= 0) return 0.0;
        var peak = 0.0;
        for (var i = 1; i < 200; i++)
        {
            var value = BackOut(i / 200.0, s);
            if (value > peak) peak = value;
        }
        return peak - 1.0;      // 超出终点的部分
    }

    /// <summary>
    /// `s_for_overshoot(ratio)`：给定"想过冲多少比例"（0.18 = 冲过头 18%），二分 40 次反推 s。
    /// </summary>
    public static double SForOvershoot(double ratio)
    {
        if (ratio <= 0) return 0.0;
        var lo = 0.0;
        var hi = 12.0;
        for (var i = 0; i < 40; i++)
        {
            var mid = (lo + hi) / 2.0;
            if (OvershootForS(mid) < ratio) lo = mid;
            else hi = mid;
        }
        return (lo + hi) / 2.0;
    }

    // Elastic：弹簧。振幅和"来回几次"都能调

    /// <summary>`_elastic_phase(amplitude, period)` 的三个返回值。</summary>
    public readonly record struct ElasticPhase(double Amp, double Per, double S);

    /// <summary>
    /// `_elastic_phase(amplitude, period)`：算弹性的相位偏移；⚠️ Python 那边注释里特意记了一笔：
    /// 别在闭包里写 `if period &lt;= 0: period = 0.3`，那会把 period 变成局部变量（
    /// UnboundLocalError）。
    /// </summary>
    public static ElasticPhase ElasticPhaseOf(double amplitude = 1.0, double period = 0.3)
    {
        var amp = Math.Max(1.0, amplitude);
        var per = period > 0 ? period : 0.3;
        var ratio = Math.Min(1.0, 1.0 / amp);
        return new ElasticPhase(amp, per, per / (2.0 * Math.PI) * Math.Asin(ratio));
    }

    /// <summary>
    /// `make_elastic_out(amplitude, period)`：f(t) = amp·2^(-10t)·sin((t-s)·2π/per) + 1。
    /// </summary>
    public static double ElasticOut(double t, double amplitude = 1.0, double period = 0.3)
    {
        var (amp, per, s) = ElasticPhaseOf(amplitude, period);
        if (t <= 0) return 0.0;
        if (t >= 1) return 1.0;
        return amp * Math.Pow(2, -10 * t) * Math.Sin((t - s) * (2 * Math.PI) / per) + 1.0;
    }

    /// <summary>
    /// `make_elastic_in(amplitude, period)`：f(t) = -(amp·2^(10(t-1))·sin((t-1-s)·2π/per))。
    /// </summary>
    public static double ElasticIn(double t, double amplitude = 1.0, double period = 0.3)
    {
        var (amp, per, s) = ElasticPhaseOf(amplitude, period);
        if (t <= 0) return 0.0;
        if (t >= 1) return 1.0;
        return -(amp * Math.Pow(2, 10 * (t - 1)) * Math.Sin((t - 1 - s) * (2 * Math.PI) / per));
    }

    // 阻尼正弦（我自己那版弹簧用的就是这个形状，这里统一收进来）

    /// <summary>
    /// `make_damped_sine(overshoot_ratio, cycles, decay)`：过冲后自己收敛回来的一条**连续**曲线；
    /// ⚠️ 前 1/8 用幂次加速起步（`shaped = 1-(1-t)^2.2`）——纯正弦起步太"木"。
    /// </summary>
    public static double DampedSine(double t, double overshootRatio = 0.12, double cycles = 1.15, double decay = 5.0)
    {
        var scale = DampedSineScale(overshootRatio, cycles, decay);
        if (t <= 0) return 0.0;
        if (t >= 1) return 1.0;
        var shaped = 1.0 - Math.Pow(1.0 - t, 2.2);
        return 1.0 - scale * Math.Exp(-decay * shaped) * Math.Sin(2 * Math.PI * cycles * shaped);
    }

    /// <summary>
    /// 阻尼正弦包络的归一化系数：`scale = overshoot_ratio / peak`，peak 取 `e^(-λt)·sin(2π·c·t)`
    /// 的峰值（t* 处，解析解）。
    /// </summary>
    public static double DampedSineScale(double overshootRatio, double cycles = 1.15, double decay = 5.0)
    {
        var peakTime = 1.0 / (2.0 * Math.PI * cycles) * Math.Atan(2.0 * Math.PI * cycles / decay);
        var peak = Math.Exp(-decay * peakTime) * Math.Sin(2.0 * Math.PI * cycles * peakTime);
        return peak > 1e-9 ? overshootRatio / peak : 0.0;
    }

    // PCL 旧版缓动公式（界面实际跑的是 Modules\Base\ModAnimation.cs 的 AniEase*，不是 PCL.Core 里那套新缓动）。
    // ⚠️ 两套公式不同，尤其 Back / Elastic；与 Qt 内置的过冲固定 10% 也不同，单靠 setOvershoot() 补不出来。

    /// <summary>PCL 旧版 Back 的力度档 → power（照抄 `PCL_BACK_POWER`）。</summary>
    public static class PclBackPower
    {
        public const double Weak = 2.0;           // 过冲 17.78%
        public const double Middle = 3.0;         // 过冲 25.81%
        public const double Strong = 4.0;         // 过冲 38.62%
        public const double ExtraStrong = 5.0;    // 过冲 60.24%
    }

    /// <summary>
    /// `make_pcl_back_out(power)`：PCL 旧版缓出回弹 f(t) = 1 - (1-t)^p · cos(1.5πt)，其中
    /// **p = 3 - 0.5·power**；⚠️ 这个函数**不夹端点**是刻意的：t=0 时 1 - 1·cos0 = 0、t=1 时 1 -
    /// 0·cos(1.5π) = 1，两个端点本来就是精确的，Python 那边也没写早退 —— 加了反而多一处可能分叉的
    /// 地方。
    /// </summary>
    public static double PclBackOut(double t, double power = 2.0)
    {
        var p = 3.0 - 0.5 * power;
        var u = 1.0 - t;
        return 1.0 - Math.Pow(u, p) * Math.Cos(1.5 * Math.PI * t);
    }

    /// <summary>
    /// `make_pcl_elastic_out(power)`：PCL 旧版弹性缓出，一条闭式曲线 （常数 `ln2*10` 和 `pi*6.5`），
    /// p = power + 4 决定弹几下；⚠️ `pow(2, -ln2_10*t)` 里的指数是 `-10·ln2·t`（不是 -10t），
    /// 照抄别"顺手化简"。
    /// </summary>
    public static double PclElasticOut(double t, double power = 2.0)
    {
        var p = power + 4.0;
        var ln2Ten = Math.Log(2) * 10.0;
        var pi65 = Math.PI * 6.5;
        _ = p;      // p 只参与"弹几下"的语义命名，公式里没有它（Python 那边同样没用到）
        if (t <= 0) return 0.0;
        if (t >= 1) return 1.0;
        return 1.0 - Math.Pow(2, -ln2Ten * t) * Math.Cos(pi65 * t);
    }

    // 按名字取曲线（= `pcl_ease.named()` + `slide_in._ease_curve()`）

    /// <summary>名字 → 曲线的解析结果（给自检 / 探针看"这个名字到底解析成了哪条"）。</summary>
    public static string Describe(string? ease)
    {
        var (name, argument) = ParseEase(ease);
        return name switch
        {
            "cubic" => "power_out(power=3)  # Qt OutCubic",
            "quint" => "power_out(power=5)  # Qt OutQuint",
            "inout" => "power_in_out(power=3)  # Qt InOutCubic",
            "back" => $"back_out(s={SForOvershoot(argument ?? 0.10):R})",
            "elastic" => "elastic_out(amplitude=1, period=0.3)",
            "spring" => $"spring(overshoot={argument ?? 0.12:R}, cycles=1.15, decay=5)",
            "power" => $"power_out(power={argument ?? 3.0:R})",
            "pcl_back" => $"pcl_back_out(power={argument ?? 2.0:R})",
            "pcl_elastic" => $"pcl_elastic_out(power={argument ?? 2.0:R})",
            _ => "power_out(power=3)  # 认不出来 → 退回 OutCubic",
        };
    }

    /// <summary>
    /// 按 slide_in._ease_curve() 的规则求值；⚠️ 认不出来**不抛异常**（退回 OutCubic）：预设表是跟
    /// 着 Python 走的，那边加一条新曲线名时这里应该退化成普通缓动，而不是让整个界面在动画开始时崩
    /// 掉。
    /// </summary>
    public static double Evaluate(string? ease, double t)
    {
        var u = Clamp01(t);     // = as_curve() 里那层 _clamp01
        var (name, argument) = ParseEase(ease);

        return name switch
        {
            "cubic" => PowerOut(u, 3.0),
            "quint" => PowerOut(u, 5.0),
            "inout" => PowerInOut(u, 3.0),
            "back" => BackOut(u, SForOvershoot(argument ?? 0.10)),
            "elastic" => ElasticOut(u),
            "spring" => DampedSine(u, argument ?? 0.12),
            "power" => PowerOut(u, argument ?? 3.0),
            "pcl_back" => PclBackOut(u, argument ?? 2.0),
            "pcl_elastic" => PclElasticOut(u, argument ?? 2.0),
            _ => PowerOut(u, 3.0),
        };
    }

    /// <summary>
    /// 拆 `名字:参数` —— 对应 `_ease_curve()` 里的 `partition(":")` + `float(raw)`。
    /// </summary>
    public static (string Name, double? Argument) ParseEase(string? ease)
    {
        if (string.IsNullOrWhiteSpace(ease)) return (string.Empty, null);
        var text = ease.Trim();
        var colon = text.IndexOf(':');
        if (colon < 0) return (text, null);

        var name = text[..colon].Trim();
        var raw = text[(colon + 1)..].Trim();
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return (name, value);
        }
        // ⚠️ 参数写坏了要**连头名一起作废**（返回 "back:" 那种认不出来的名字），见上面第 1 条
        return (text, null);
    }
}
