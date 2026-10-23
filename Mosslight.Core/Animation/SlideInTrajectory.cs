namespace Mosslight.Core.Animation;

/// <summary>轨迹引擎的取值：某个时刻**被推出去多少像素**，以及那一刻的透明度。</summary>
public readonly record struct SlideInSample(double OffsetPx, double Opacity);

/// <summary>一条入场动画的**时间轴拆分**（全部毫秒）。</summary>
public readonly record struct SlideInTimeline(
    double TotalMs,
    double MainMs,
    double ReboundMs,
    double ReboundOutMs,
    double ReboundBackMs,
    double FastMs,
    double SlowMs,
    double FadeMs)
{
    /// <summary>按预设算时间轴（速度倍率请**先**用 AnimationPresets.Scaled 乘进去）。</summary>
    public static SlideInTimeline For(AnimationPreset preset)
    {
        // Python 那边到处是 max(1, int(duration))：时长 0 会让 QPropertyAnimation 变成
        // "瞬间跳到终点"，QT 的 setDuration(0) 语义和"不动"完全不同，所以照抄这个下限。
        var duration = Math.Max(1, preset.DurationMs);

        switch (preset.Style)
        {
            case AnimationStyle.Bounce:
            case AnimationStyle.Spring:
            {
                // ⚠️ Bounce 分段：main = int(duration * bounce_ratio)，back = max(2, duration - main)，再对半分为冲过头和收回；back // 2 是整除。
                var main = Math.Max(1, (int)(duration * preset.BounceRatio));
                var back = Math.Max(2, duration - main);
                var backOut = Math.Max(1, back / 2);
                var backBack = Math.Max(1, back - back / 2);
                return new SlideInTimeline(
                    main + backOut + backBack, main, back, backOut, backBack, 0, 0, 0);
            }

            case AnimationStyle.Pcl:
            {
                // PCL 双轨：fast = max(60, duration * PCL_FAST_RATIO)，slow = max(fast + 20, duration)，fade = clamp(PCL_FADE_MS_MIN, PCL_FADE_MS, fast)；总时长取慢轨。
                var fast = Math.Max(60, (int)(duration * SlideInTrajectory.PclFastRatio));
                var slow = Math.Max(fast + 20, duration);
                var fade = Math.Max(
                    SlideInTrajectory.PclFadeMinMs,
                    Math.Min(SlideInTrajectory.PclFadeMs, fast));
                return new SlideInTimeline(Math.Max(slow, fade), 0, 0, 0, 0, fast, slow, fade);
            }

            default:
                // Simple：一条曲线走完，滑到位的时间就是总时长
                return new SlideInTimeline(duration, duration, 0, 0, 0, 0, 0, 0);
        }
    }
}

/// <summary>入场动画的轨迹引擎：按时间计算位移和透明度。</summary>
public static class SlideInTrajectory
{
    // 常数：逐条照抄 slide_in.py 顶部的定义

    /// <summary>`SPRING_CYCLES`：周期数越多，来回弹的次数越多。</summary>
    public const double SpringCycles = 1.15;

    /// <summary>`SPRING_DECAY`：阻尼系数（越大衰减越快）。</summary>
    public const double SpringDecay = 5.0;

    /// <summary>`PCL_FADE_MS`：PCL 透明度那条的时长。</summary>
    public const int PclFadeMs = 100;

    /// <summary>`PCL_FADE_MS_MIN`：太短会看不见淡入。</summary>
    public const int PclFadeMinMs = 60;

    /// <summary>`PCL_FAST_RATIO`：位移 A 占总时长的比例（250/350）。</summary>
    public const double PclFastRatio = 0.71;

    /// <summary>`PCL_FAST_SHARE`：A 承担多少位移（5/16）。</summary>
    public const double PclFastShare = 0.31;

    /// <summary>`PCL_SLOW_SHARE`：B 承担多少位移（11/16）。</summary>
    public const double PclSlowShare = 0.69;

    /// <summary>关键帧的采样步长（毫秒）。</summary>
    public const double DefaultKeyframeStepMs = 8.0;

    /// <summary>自适应细分的目标误差（像素）：中点偏离直线超过它就再切一刀。</summary>
    public const double DefaultKeyframeTolerancePx = 0.02;

    /// <summary>细分的最细粒度（毫秒）—— 再细下去关键帧数量涨得比画质收益快。</summary>
    public const double MinKeyframeSpanMs = 2.0;

    private const int MaxRefinePasses = 12;

    /// <summary>这条预设的时间轴拆分。</summary>
    public static SlideInTimeline Timeline(AnimationPreset preset) => SlideInTimeline.For(preset);

    /// <summary>整条轨迹的总时长（PCL 那档可能比预设表里写的 duration 长）。</summary>
    public static double TotalDurationMs(AnimationPreset preset) => Timeline(preset).TotalMs;

    // 分段的缓动（Python 里是 QEasingCurve.Type.OutQuad / InOutQuad）

    /// <summary>Qt 的 `OutQuad`：1 - (1-t)²。</summary>
    private static double OutQuad(double t) => 1.0 - (1.0 - t) * (1.0 - t);

    /// <summary>Qt 的 `InOutQuad`：两半接起来。</summary>
    private static double InOutQuad(double t)
        => t < 0.5 ? 2.0 * t * t : 1.0 - (-2.0 * t + 2.0) * (-2.0 * t + 2.0) / 2.0;


    /// <summary>
    /// 阻尼正弦 bump：`-(scale · e^(-λu) · sin(2π·cycles·u))`，u 从 0 到 1；⚠️ 这里**不夹端点**：
    /// Python 的 `on_value` 也不夹（t=1 时还剩约 -0.36px 的尾数，由 `_on_finished()` 兜底归零）。
    /// </summary>
    public static double SpringBump(double u, double overshootPx)
    {
        var scale = AnimationCurves.DampedSineScale(1.0, SpringCycles, SpringDecay) * overshootPx;
        return -(scale * Math.Exp(-SpringDecay * u) * Math.Sin(2 * Math.PI * SpringCycles * u));
    }

    /// <summary>
    /// 过冲峰值出现在这一段的哪个位置（u，0~1）—— 关键帧要在这里落一个点，峰值才不被采样漏掉。
    /// </summary>
    public static double SpringBumpPeakU
        => 1.0 / (2.0 * Math.PI * SpringCycles) * Math.Atan(2.0 * Math.PI * SpringCycles / SpringDecay);

    // 主入口：某一时刻的偏移

    /// <summary>某一时刻"被推出去多少像素"（正 = 还没到位，负 = 冲过头）。</summary>
    public static double DisplacementAt(AnimationPreset preset, double timeMs)
    {
        var tl = Timeline(preset);
        var offset = Math.Max(0, preset.OffsetPx);
        var overshoot = Math.Max(1, preset.OvershootPx);   // Python: max(1, int(overshoot))

        // 起手：Python 的 `reveal()` 先 `_apply(self._offset)` 把卡片摆到屏幕外，
        // 所以 t=0 一定是"满偏移"。终点一律精确归零（`_on_finished()` 的兜底）。
        if (timeMs <= 0) return offset;
        if (timeMs >= tl.TotalMs) return 0.0;

        switch (preset.Style)
        {
            case AnimationStyle.Bounce:
            {
                // slide_in.py `_build_animations()` 的 ANIM_BOUNCE 分支
                // anim.addAnimation(_track(offset, 0, main,       _ease_curve(ease)))
                // anim.addAnimation(_track(0, -overshoot, back//2, OutQuad))       ← 冲过头
                // anim.addAnimation(_track(-overshoot, 0, back-back//2, InOutQuad)) ← 收回来
                if (timeMs <= tl.MainMs)
                {
                    var u = timeMs / tl.MainMs;
                    return offset * (1.0 - AnimationCurves.Evaluate(preset.Ease, u));
                }
                if (timeMs <= tl.MainMs + tl.ReboundOutMs)
                {
                    var u = (timeMs - tl.MainMs) / tl.ReboundOutMs;
                    return -overshoot * OutQuad(u);
                }
                var v = (timeMs - tl.MainMs - tl.ReboundOutMs) / tl.ReboundBackMs;
                return -overshoot * (1.0 - InOutQuad(v));
            }

            case AnimationStyle.Spring:
            {
                // slide_in.py `_spring()`（回弹段逐值照抄）+ 前段滑到位
                if (timeMs <= tl.MainMs)
                {
                    var u = timeMs / tl.MainMs;
                    return offset * (1.0 - AnimationCurves.Evaluate(preset.Ease, u));
                }
                var w = (timeMs - tl.MainMs) / tl.ReboundMs;
                // Python 的 `_on_finished()` 会把最后那点尾数抹平，这里等价地做在 t=1 上
                if (w >= 1.0) return 0.0;
                return SpringBump(w, overshoot);
            }

            case AnimationStyle.Pcl:
            {
                // PCL 双轨同时跑：A 段 offset*0.31 走 OutQuint，B 段 offset*0.69 走 pcl_back(2.0)，合成轨迹 = fast + slow。
                var a0 = offset * PclFastShare;
                var b0 = offset * PclSlowShare;
                var fast = timeMs >= tl.FastMs
                    ? 0.0
                    : a0 * (1.0 - AnimationCurves.PowerOut(timeMs / tl.FastMs, 5.0));
                var slow = timeMs >= tl.SlowMs
                    ? 0.0
                    : b0 * (1.0 - AnimationCurves.PclBackOut(timeMs / tl.SlowMs, 2.0));
                return fast + slow;
            }

            default:
            {
                // 普通轨迹：从 offset 到 0 按 duration 和 ease 走一条曲线；elastic / back 在这一分支里是缓动，不是独立形态。
                var u = timeMs / tl.TotalMs;
                return offset * (1.0 - AnimationCurves.Evaluate(preset.Ease, u));
            }
        }
    }

    public static double OpacityAt(AnimationPreset preset, double timeMs, bool fade)
    {
        if (!fade) return 1.0;
        var tl = Timeline(preset);
        if (timeMs <= 0) return 0.0;

        if (preset.Style == AnimationStyle.Pcl)
        {
            return AnimationCurves.PowerOut(AnimationCurves.Clamp01(timeMs / tl.FadeMs), 3.0);
        }

        // 淡入时长：整条轨迹 × FadeRatio。下限 1ms 是防呆（比例被写成 0 时不该除以 0）
        var fadeMs = Math.Max(1.0, tl.TotalMs * preset.FadeRatio);
        return AnimationCurves.Evaluate(preset.Ease, AnimationCurves.Clamp01(timeMs / fadeMs));
    }

    /// <summary>偏移 + 透明度一起取（探针/自检按时间采样用这个）。</summary>
    public static SlideInSample SampleAt(AnimationPreset preset, double timeMs, bool fade = true)
        => new(DisplacementAt(preset, timeMs), OpacityAt(preset, timeMs, fade));

    /// <summary>偏移量 → WPF 坐标系的位移（`TranslateTransform.X` / `.Y` 要加的那个值）。</summary>
    public static double ToCoordinate(AnimationPreset preset, double offsetPx)
        => preset.Direction == AnimationDirection.Horizontal ? -offsetPx : offsetPx;

    // 关键帧时间点

    /// <summary>
    /// 关键帧要落在哪些时刻：**等距网格 ∪ 分段边界 ∪ 轨迹的极值点 ∪ 自适应细分点**。
    /// </summary>
    public static IReadOnlyList<double> KeyTimes(
        AnimationPreset preset,
        double stepMs = DefaultKeyframeStepMs,
        double tolerancePx = DefaultKeyframeTolerancePx)
    {
        var tl = Timeline(preset);
        var total = tl.TotalMs;
        var times = new SortedSet<double> { 0.0, total };

        var step = Math.Max(1.0, stepMs);
        for (var t = step; t < total; t += step)
        {
            times.Add(Math.Round(t, 6));
        }

        // 分段边界：三段回弹的两个拐点、PCL 两条轨道的收尾时刻、弹簧回弹段的峰值
        foreach (var boundary in new[]
                 {
                     tl.MainMs, tl.MainMs + tl.ReboundOutMs, tl.FastMs, tl.SlowMs, tl.FadeMs,
                 })
        {
            if (boundary > 0 && boundary < total) times.Add(Math.Round(boundary, 6));
        }
        if (preset.Style == AnimationStyle.Spring && tl.ReboundMs > 0)
        {
            var peak = tl.MainMs + SpringBumpPeakU * tl.ReboundMs;   // 最深的那次过冲
            if (peak > 0 && peak < total) times.Add(Math.Round(peak, 6));
        }

        // 极值点：1ms 粒度扫一遍，一阶差分变号处就是峰/谷
        var previous = DisplacementAt(preset, 0);
        var previousSlope = 0.0;
        var last = (int)Math.Ceiling(total);
        for (var ms = 1; ms <= last; ms++)
        {
            var current = DisplacementAt(preset, ms);
            var slope = current - previous;
            if (previousSlope != 0 && slope != 0 && Math.Sign(slope) != Math.Sign(previousSlope))
            {
                times.Add(ms - 0.5);
            }
            if (slope != 0) previousSlope = slope;
            previous = current;
        }

        // 自适应细分（见方法注释）。取中点判据：中点离"两端连成的直线"有多远。
        var points = times.ToList();
        for (var pass = 0; pass < MaxRefinePasses; pass++)
        {
            var inserted = false;
            for (var i = 0; i + 1 < points.Count; i++)
            {
                var a = points[i];
                var b = points[i + 1];
                var span = b - a;
                if (span <= MinKeyframeSpanMs) continue;

                var mid = a + span / 2.0;
                var straight = (DisplacementAt(preset, a) + DisplacementAt(preset, b)) / 2.0;
                if (Math.Abs(DisplacementAt(preset, mid) - straight) <= tolerancePx) continue;

                points.Insert(i + 1, mid);
                inserted = true;
                i++;        // 跳过刚插进去的点，免得同一轮里反复切同一段
            }
            if (!inserted) break;
        }

        return points;
    }
}
