namespace Mosslight.Core.Animation;

/// <summary>
/// 轨迹引擎的取值：某个时刻**被推出去多少像素**，以及那一刻的透明度。
/// </summary>
/// <param name="OffsetPx">
/// 偏移量，**正 = 还在滑入那一侧**（卡片还没到位）、**负 = 冲过头了**（越过终点）。
/// 坐标系不在这里 —— 水平档取负、垂直档取正由 <see cref="SlideInTrajectory.ToCoordinate"/> 换算。
/// </param>
/// <param name="Opacity">0~1（<c>fade</c> 关掉时恒为 1）。</param>
public readonly record struct SlideInSample(double OffsetPx, double Opacity);

/// <summary>
/// 一条入场动画的**时间轴拆分**（全部毫秒）。
///
/// 为什么它不是"一个 duration 就完了"：1.0.0 的四种形态各有各的分段 ——
/// 回弹要"滑到位 / 冲过头 / 收回"三段、PCL 要"快位移 / 慢位移 / 淡入"三条**并行**的轨道，
/// 而每张卡片的"总时长"跟预设表里写的 `duration` 并不是一个东西（PCL 那档的慢轨就能比它长）。
/// 把这个拆分显式算出来，界面层（关键帧生成、对话框的收尾定时器）和自检才能看到同一份数。
/// </summary>
/// <param name="TotalMs">整条轨迹的总时长。</param>
/// <param name="MainMs">滑到位那一段（Bounce / Spring；Simple 时 = 总时长）。</param>
/// <param name="ReboundMs">回弹那一段的总时长（Bounce / Spring）。</param>
/// <param name="ReboundOutMs">回弹第一小段：0 → 冲过头。</param>
/// <param name="ReboundBackMs">回弹第二小段：冲过头 → 收回 0。</param>
/// <param name="FastMs">PCL 位移 A（快、走 31% 位移）的时长。</param>
/// <param name="SlowMs">PCL 位移 B（慢、走 69% 位移、带回弹）的时长。</param>
/// <param name="FadeMs">PCL 淡入的时长。</param>
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
    /// <summary>按预设算时间轴（速度倍率请**先**用 <see cref="AnimationPresets.Scaled"/> 乘进去）。</summary>
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
                // ---- slide_in.py `_build_animations()` 的 ANIM_BOUNCE 分支 ----
                //     main = int(duration * bounce_ratio)
                //     back = max(2, duration - main)      ← 剩下的时间用来回弹
                //     再对半分成"冲过头"和"收回来"两段（back // 2 是**整除**，别写成浮点）
                //
                // Spring 用同一套拆分，理由见 SlideInTrajectory 的类注释（1.0.0 那档没分，
                // 结果是原地抖；这里按预设自己填的 bounce_ratio 把"滑"和"弹"分开）。
                var main = Math.Max(1, (int)(duration * preset.BounceRatio));
                var back = Math.Max(2, duration - main);
                var backOut = Math.Max(1, back / 2);
                var backBack = Math.Max(1, back - back / 2);
                return new SlideInTimeline(
                    main + backOut + backBack, main, back, backOut, backBack, 0, 0, 0);
            }

            case AnimationStyle.Pcl:
            {
                // ---- slide_in.py `_build_pcl()` ----
                //     fast_ms = max(60, int(duration * PCL_FAST_RATIO))
                //     slow_ms = max(fast_ms + 20, duration)
                //     fade_ms = max(PCL_FADE_MS_MIN, min(PCL_FADE_MS, fast_ms))
                // 总时长是**慢轨**（它最长）；淡入那条一般 100ms 就完了。
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

/// <summary>
/// 入场动画的**轨迹引擎** —— 逐值移植 1.0.0 `ui/widgets/slide_in.py` 里那套形态算法。
///
/// ## 为什么要有它（上一版 C# 缺的就是这个）
/// 2.0 早先的 <c>PageAnimator</c> 只会"按 direction/duration/offset/ease 播**一条**曲线"，
/// 而 1.0.0 的四种形态根本不是一条曲线能表达的：
///
/// | 形态 | 1.0.0 的实现 | 一条曲线能表达吗 |
/// |---|---|---|
/// | Simple | `_track(offset, 0, duration, ease)` | 能 |
/// | Bounce | `QSequentialAnimationGroup`：滑到位 → 冲过头 → 收回 | 不能（三段拼的） |
/// | Spring | `QVariantAnimation` + 回调里现算阻尼正弦 | 不能（不是 t 的多项式） |
/// | Pcl | `QParallelAnimationGroup`：两条位移**同时**跑 + 淡入 | 不能（位移是两条的和） |
///
/// 所以"弹簧 / PCL 原味"那几档在过去只是把时长和缓动换了一下，没有真正的回弹 ——
/// 这个类就是把那段缺失的轨迹补齐，并且把它做成**纯函数**（不依赖 Qt / WPF），
/// 这样探针和自检能把"每一毫秒应该在哪"逐点算出来、跟 Python 比。
///
/// ## 偏移的正负号（跟 1.0.0 对齐）
///     正 = 还在滑入那一侧（起手值 = +offset）
///      0 = 已就位
///     负 = 冲过头了（越过终点，往运动方向多走）
/// 1.0.0 的 `_apply()` 是"水平 `child.move(-offset, 0)`、垂直 `child.move(0, offset)`"，
/// 所以水平档要取负号才是 WPF 的 TranslateTransform.X —— 见 <see cref="ToCoordinate"/>。
///
/// ## ⚠️ 三处**故意**没照抄 1.0.0 的地方（每一处都是 1.0.0 自身的缺陷，报告里也写了）
///   1. **Spring（弹簧）那档在 1.0.0 里根本不会滑进来**：`reveal()` 先把卡片摆到起手位置，
///      紧接着 `_spring()` 的第一个 `valueChanged(0.0)` 又把它写回偏移 0 ——
///      卡片是"凭空出现在终点然后原地抖"，不是"从左边滑进来再弹一下"。
///      这里按预设自己填的 `bounce_ratio`（1.0.0 给这一档填的就是 0.5）把"滑到位"和
///      "阻尼正弦回弹"分成前后两段。**回弹那段的公式、常数、归一化方式一字未改**
///      （见 <see cref="SpringBump"/>），所以"逐值"这件事仍然可验：探针会拿
///      `SpringBump` 跟 Python 的 `_spring()` 逐点比。
///   2. **PCL 那档在"入场淡入"关掉时会退化成一条普通 cubic 滑入**：`_build_animations()`
///      写的是 `if style == ANIM_PCL and self._fade is not None` —— 淡入一关，
///      两条位移轨道跟着一起没了。这里让"淡入"开关只管透明度，两条位移轨道照跑
///      （预设的说明本来写的就是"位移拆成快、慢两条并行曲线，**同时**淡入"）。
///   3. **非 PCL 形态在 1.0.0 里压根不淡入**：`SlideInRow` 给每种形态都建了
///      `QGraphicsOpacityEffect`，但只有 `_build_pcl()` 往它挂了动画 —— 其余形态的卡片
///      是"全不透明地滑进来"。2.0 的「入场淡入」是用户可见的一档设置（对话框里写着
///      「卡片出现时带淡入 + 位移」），所以这里保留"所有形态都淡入"，
///      非 PCL 形态的淡入时长 = 整条轨迹时长、曲线 = 该档自己的缓动。
/// </summary>
public static class SlideInTrajectory
{
    // ============================================================
    // 常数：逐条照抄 slide_in.py 顶部的定义
    // ============================================================

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

    /// <summary>
    /// 关键帧的采样步长（毫秒）。8ms ≈ 120fps 的两倍，线性插值带来的形状误差远小于半像素
    /// （见自检里那条"关键帧重建误差"的断言）；再密就只是白烧内存。
    /// </summary>
    public const double DefaultKeyframeStepMs = 8.0;

    /// <summary>自适应细分的目标误差（像素）：中点偏离直线超过它就再切一刀。</summary>
    public const double DefaultKeyframeTolerancePx = 0.02;

    /// <summary>细分的最细粒度（毫秒）—— 再细下去关键帧数量涨得比画质收益快。</summary>
    public const double MinKeyframeSpanMs = 2.0;

    /// <summary>
    /// 细分最多做几轮（每轮把不满足误差的区间对半切）。
    ///
    /// ⚠️ 这个数是**这一轮调手感时从 6 提到 12 的**，理由是实测：
    ///    弹簧那档的分段比从 0.5 调到 0.6 之后，"滑到位 → 回弹"那个拐点上的速度断得更狠，
    ///    6 轮跑完还差 0.045px（自检的判据是 &lt; 0.03px）。
    ///    根因是下面那个循环里 `i++` 会**跳过刚切出来的右半段**，同一轮里每个区间
    ///    其实只被检查了一次，收敛得比"对半切"该有的速度慢一倍 ——
    ///    与其动循环（那会改变关键帧的分布，属于改算法），不如把轮数提上去：
    ///    多几轮只在**真正不满足误差**的地方加点，关键帧数量涨幅很小
    ///    （弹簧那档实测 59 → 69 个点，多 10 个）。
    /// </summary>
    private const int MaxRefinePasses = 12;

    /// <summary>这条预设的时间轴拆分。</summary>
    public static SlideInTimeline Timeline(AnimationPreset preset) => SlideInTimeline.For(preset);

    /// <summary>整条轨迹的总时长（PCL 那档可能比预设表里写的 duration 长）。</summary>
    public static double TotalDurationMs(AnimationPreset preset) => Timeline(preset).TotalMs;

    // ============================================================
    // 分段的缓动（Python 里是 QEasingCurve.Type.OutQuad / InOutQuad）
    // ============================================================

    /// <summary>Qt 的 `OutQuad`：1 - (1-t)²。</summary>
    private static double OutQuad(double t) => 1.0 - (1.0 - t) * (1.0 - t);

    /// <summary>Qt 的 `InOutQuad`：两半接起来。</summary>
    private static double InOutQuad(double t)
        => t < 0.5 ? 2.0 * t * t : 1.0 - (-2.0 * t + 2.0) * (-2.0 * t + 2.0) / 2.0;

    // ============================================================
    // 弹簧（阻尼正弦）—— 1.0.0 `_spring()` 里那个 on_value()
    // ============================================================

    /// <summary>
    /// 阻尼正弦 bump：`-(scale · e^(-λu) · sin(2π·cycles·u))`，u 从 0 到 1。
    ///
    /// 这是 1.0.0 `SlideInRow._spring()` 里 `on_value()` 的**逐字**移植：
    ///   · u=0 时值为 0（已就位），随后往**负方向**冲出去（- = 越过终点、继续往运动方向走），
    ///     再阻尼收敛回 0；
    ///   · 前面乘 -1：不乘的话第一个峰值跑到正方向，看着是"滑过头往回退"，方向反了；
    ///   · `scale` 里除以包络自己的峰值（<see cref="AnimationCurves.DampedSineScale"/>）：
    ///     不这么做的话 overshoot 只是个系数，实际过冲量会被衰减吃掉一大半
    ///     （设 16px 只能冲到 6px 左右），参数就没法照着"想弹多少像素"来填了。
    ///
    /// ⚠️ 这里**不夹端点**：Python 的 `on_value` 也不夹（t=1 时还剩约 -0.36px 的尾数，
    /// 由 `_on_finished()` 兜底归零）。轨迹引擎那边才做归零，见 <see cref="DisplacementAt"/>。
    /// </summary>
    /// <param name="u">这一段自己的归一化时间（0~1）。</param>
    /// <param name="overshootPx">想冲过头多少像素（峰值会被归一化到这个数）。</param>
    public static double SpringBump(double u, double overshootPx)
    {
        var scale = AnimationCurves.DampedSineScale(1.0, SpringCycles, SpringDecay) * overshootPx;
        return -(scale * Math.Exp(-SpringDecay * u) * Math.Sin(2 * Math.PI * SpringCycles * u));
    }

    /// <summary>过冲峰值出现在这一段的哪个位置（u，0~1）—— 关键帧要在这里落一个点，峰值才不被采样漏掉。</summary>
    public static double SpringBumpPeakU
        => 1.0 / (2.0 * Math.PI * SpringCycles) * Math.Atan(2.0 * Math.PI * SpringCycles / SpringDecay);

    // ============================================================
    // 主入口：某一时刻的偏移
    // ============================================================

    /// <summary>
    /// 某一时刻"被推出去多少像素"（正 = 还没到位，负 = 冲过头）。
    ///
    /// 四种形态各自的算法来源（都写在分支里了）：
    ///   · Simple —— `_build_animations()` 的 else 分支：`_track(offset, 0, duration, ease)`；
    ///   · Bounce —— 同函数的 ANIM_BOUNCE 分支：三段 QSequentialAnimationGroup；
    ///   · Spring —— `_spring()`：阻尼正弦（这里按 bounce_ratio 与"滑到位"分段）；
    ///   · Pcl    —— `_build_pcl()`：两条并行位移相加。
    /// </summary>
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
                // ---- slide_in.py `_build_animations()` 的 ANIM_BOUNCE 分支 ----
                //   anim.addAnimation(_track(offset, 0, main,       _ease_curve(ease)))
                //   anim.addAnimation(_track(0, -overshoot, back//2, OutQuad))       ← 冲过头
                //   anim.addAnimation(_track(-overshoot, 0, back-back//2, InOutQuad)) ← 收回来
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
                // ---- slide_in.py `_spring()`（回弹段逐值照抄）+ 前段滑到位 ----
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
                // ---- slide_in.py `_build_pcl()` ----
                //   _track(a0=offset*0.31, 0, fast_ms, OutQuint,   fastOffset)
                //   _track(b0=offset*0.69, 0, slow_ms, pcl_back(2.0), slowOffset)
                // 两个分量**同时**在跑，合成轨迹 = fast + slow（`_sync_offset()` 里那句
                // `total = fast + slow`）。两条加起来正好是 offset，所以终点还是精确落位。
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
                // ---- slide_in.py `_build_animations()` 的 else 分支 ----
                //   anim.addAnimation(_track(offset, 0, duration, _ease_curve(ease)))
                // `elastic`（弹性来回好几下）和 `back:0.18`（冲过头 18%）都是**这条分支**：
                // 在 1.0.0 里它们是**缓动曲线**、不是独立形态 —— 预设表里那两档的 style
                // 都是 ANIM_SIMPLE，差别全在 ease 上。
                var u = timeMs / tl.TotalMs;
                return offset * (1.0 - AnimationCurves.Evaluate(preset.Ease, u));
            }
        }
    }

    /// <summary>
    /// 某一时刻的透明度。
    ///
    ///   · PCL —— `_build_pcl()` 里那条 `QPropertyAnimation(self._fade, b"opacity")`：
    ///     0 → 1，时长 `max(60, min(100, fast_ms))`，OutCubic，跟位移同时开始；
    ///   · 其余形态 —— 1.0.0 **不淡入**（见类注释第 3 条），2.0 保留"所有形态都淡入"，
    ///     时长 = <c>整条轨迹 × 预设的 FadeRatio</c>、曲线 = 该档自己的缓动。
    ///
    /// ⚠️ 下面那句 `<c>× FadeRatio</c>` 是**这一轮调手感新加的**（见
    ///    <see cref="AnimationPreset.FadeRatio"/> 的注释）：以前这里写的是"时长 = 整条轨迹"，
    ///    等于让透明度跟位移共用一条曲线 —— 缓出曲线在前 10% 就把透明度推到 0.27~0.65
    ///    （淡入形同虚设），过冲曲线更是让透明度冲到 1.25 之后再"淡"完。
    ///    改成按比例之后，淡入有了自己的时间轴；而 <c>FadeRatio = 1.0</c>（默认值）
    ///    仍然精确等于改动之前的行为，自检里那些拿金标准造出来的对照用例不受影响。
    /// </summary>
    public static double OpacityAt(AnimationPreset preset, double timeMs, bool fade)
    {
        if (!fade) return 1.0;
        var tl = Timeline(preset);
        if (timeMs <= 0) return 0.0;

        if (preset.Style == AnimationStyle.Pcl)
        {
            // PCL 那条是 1.0.0 写死的 100ms，**不吃 FadeRatio** ——
            // 它的时长来自 PCL_FADE_MS，不是"整条轨迹的一部分"
            return AnimationCurves.PowerOut(AnimationCurves.Clamp01(timeMs / tl.FadeMs), 3.0);
        }

        // 淡入时长：整条轨迹 × FadeRatio。下限 1ms 是防呆（比例被写成 0 时不该除以 0）
        var fadeMs = Math.Max(1.0, tl.TotalMs * preset.FadeRatio);
        return AnimationCurves.Evaluate(preset.Ease, AnimationCurves.Clamp01(timeMs / fadeMs));
    }

    /// <summary>偏移 + 透明度一起取（探针/自检按时间采样用这个）。</summary>
    public static SlideInSample SampleAt(AnimationPreset preset, double timeMs, bool fade = true)
        => new(DisplacementAt(preset, timeMs), OpacityAt(preset, timeMs, fade));

    /// <summary>
    /// 偏移量 → WPF 坐标系的位移（`TranslateTransform.X` / `.Y` 要加的那个值）。
    ///
    /// 照 1.0.0 的 `_apply()`：水平 `child.move(-offset, 0)` → 取负；
    /// 垂直 `child.move(0, offset)` → 原样（正数往下，起点在终点**下面**，往上滑）。
    /// </summary>
    public static double ToCoordinate(AnimationPreset preset, double offsetPx)
        => preset.Direction == AnimationDirection.Horizontal ? -offsetPx : offsetPx;

    // ============================================================
    // 关键帧时间点
    // ============================================================

    /// <summary>
    /// 关键帧要落在哪些时刻：**等距网格 ∪ 分段边界 ∪ 轨迹的极值点 ∪ 自适应细分点**。
    ///
    /// 只按等距网格采样的话，峰值（冲过头最深的那一帧）会被网格跨过去 ——
    /// 表现在动画上就是"设了 28px 却只冲到 27.3px"，而这正是这一档的验收标准。
    /// 极值点是 1ms 扫一阶差分变号找出来的（便宜且够准）。
    ///
    /// 最后一轮**自适应细分**是给"拐点特别尖"的形状准备的：弹簧那档在"滑到位 → 回弹"
    /// 交界处速度是断的，8ms 的等距网格在那一小段上用直线糊过去，实测能差 0.25px
    /// （探针里量出来的）。所以这里按"相邻两点的中点偏离直线多少"来加点，
    /// 偏得多的区间一分为二，直到误差小于 <paramref name="tolerancePx"/> 或细到
    /// <see cref="MinKeyframeSpanMs"/> 为止 —— 只在真正需要的地方加密，别全局加一倍。
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
