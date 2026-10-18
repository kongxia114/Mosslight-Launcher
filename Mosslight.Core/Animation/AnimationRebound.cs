namespace Mosslight.Core.Animation;

/// <summary>
/// 「这一档到底有没有回弹」的测量结果。
/// </summary>
/// <param name="HasRebound">轨迹有没有越过终点（冲过头）到肉眼看得见的程度。</param>
/// <param name="OvershootPx">冲过头最深多少像素（0 = 没过冲）。</param>
/// <param name="PeakOffsetPx">最深处的偏移值（负值，= -OvershootPx）。</param>
/// <param name="PeakTimeMs">冲过头最深发生在第几毫秒。</param>
/// <param name="Source">回弹是**哪来的**（三段回弹 / 阻尼正弦 / PCL 慢轨 / 缓动曲线过冲 / 无）。</param>
public sealed record ReboundMeasurement(
    bool HasRebound,
    double OvershootPx,
    double PeakOffsetPx,
    double PeakTimeMs,
    string Source);

/// <summary>设计说明见 docs/design-notes.md#mosslight-core-animation-animationrebound-cs</summary>
public static class AnimationRebound
{
    /// <summary>小于这个像素数的过冲不算"有回弹"（亚像素看不见）。</summary>
    public const double ReboundEpsilonPx = 0.5;

    /// <summary>采样步长（毫秒）。0.5ms 足够抓到峰值，又不会让自检变慢。</summary>
    private const double ScanStepMs = 0.5;

    /// <summary>量一档预设的回弹。传入的预设请**已经**乘过速度倍率（量的是真正会播的那条）。</summary>
    public static ReboundMeasurement Measure(AnimationPreset preset)
    {
        var total = SlideInTrajectory.TotalDurationMs(preset);

        var lowest = double.MaxValue;
        var lowestAt = 0.0;
        var steps = (int)Math.Ceiling(total / ScanStepMs);
        for (var i = 0; i <= steps; i++)
        {
            var t = i * ScanStepMs;
            var value = SlideInTrajectory.DisplacementAt(preset, t);
            if (value < lowest)
            {
                lowest = value;
                lowestAt = t;
            }
        }

        if (lowest == double.MaxValue) lowest = 0.0;      // 总时长为 0 之类的退化情形
        var overshoot = Math.Max(0.0, -lowest);
        return new ReboundMeasurement(
            HasRebound: overshoot > ReboundEpsilonPx,
            OvershootPx: overshoot,
            PeakOffsetPx: lowest,
            PeakTimeMs: lowestAt,
            Source: SourceOf(preset));
    }

    /// <summary>
    /// 回弹是哪来的 —— 只给自检 / 探针 / 报告看，**不进界面文案**
    /// （界面只显示「有回弹 / 无回弹」两个已有词条，免得为这一句话去动五个语言文件）。
    /// </summary>
    public static string SourceOf(AnimationPreset preset) => preset.Style switch
    {
        AnimationStyle.Bounce => "三段回弹（滑到位 → 冲过头 → 收回）",
        AnimationStyle.Spring => "阻尼正弦（spring_bump：e^(-λt)·sin(2π·cycles·t)）",
        AnimationStyle.Pcl => "PCL 慢轨（位移 B 用 pcl_back_out，p=2.0 时过冲 17.77%）",
        _ => EaseOvershoots(preset.Ease)
            ? $"缓动曲线过冲（{AnimationCurves.Describe(preset.Ease)}）"
            : "无（单条缓出曲线，单调到位）",
    };

    /// <summary>某条缓动曲线的峰值是否越过 1（越过就是"冲过头"）。</summary>
    private static bool EaseOvershoots(string? ease)
    {
        for (var i = 0; i <= 1000; i++)
        {
            if (AnimationCurves.Evaluate(ease, i / 1000.0) > 1.0 + 1e-9) return true;
        }
        return false;
    }
}
