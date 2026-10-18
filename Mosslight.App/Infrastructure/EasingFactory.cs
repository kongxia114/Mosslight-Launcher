using System.Windows.Media.Animation;
using Mosslight.Core.Animation;

namespace Mosslight.App.Infrastructure;

/// <summary>设计说明见 docs/design-notes.md#mosslight-app-infrastructure-easingfactory-cs</summary>
public static class EasingFactory
{
    /// <summary>
    /// 按预设里的 Ease 字符串造缓动函数。
    ///
    /// ⚠️ **不缓存实例**：这些缓动函数是 <c>Freezable</c>，同一个实例被多个动画同时用，
    /// 其中一个改了属性会连带影响别人。动画本来就不常建，省这点分配不值得冒险。
    /// （Python 那边反倒必须缓存：Qt 最多只允许 10 条自定义曲线。）
    /// </summary>
    public static IEasingFunction Create(string? ease)
    {
        var (name, argument) = AnimationCurves.ParseEase(ease);

        switch (name)
        {
            case "cubic":
                // = Qt 的 OutCubic = 1-(1-t)³；WPF 内置的 CubicEase 就是这条（自检里有实测）
                return EaseOut(new CubicEase());

            case "quint":
                // `EASE_QUINT` 在 Python 那边走 Qt 内置 OutQuint = 1-(1-t)^5。
                //
                // ⚠️ 这里**以前**用的是 WPF 的 QuinticEase，注释还写着"它不是 1-(1-t)^5，
                //    0.5 处只有 0.94921875"。本轮实测把这个说法推翻了：
                //    WPF 的 QuinticEase（EaseOut）**就是** 1-(1-t)^5，逐点差 1e-16
                //    （自检里有这条断言，拿它当哨兵）。所以换成自实现是**行为等价**的重构，
                //    换的理由不是"原来算错了"，而是"公式只留一份"：Core 的
                //    AnimationCurves.PowerOut 同时喂给轨迹引擎（PCL 那条快轨用的也是 OutQuint），
                //    两边共用一份实现才不会再出现"曲线的注释跟实际不符"这种事。
                return EaseOut(new ParametricPowerOutEase(5.0));

            case "inout":
                // 两头慢中间快，最柔和的一种 —— 唯一不用 EaseOut 的。
                // WPF 的 EaseInOut 分段跟 Qt 的 InOutCubic 逐值一样（0.5 处都是 4t³ / 1-(2-2t)³/2）
                return new CubicEase { EasingMode = EasingMode.EaseInOut };

            case "back":
                // 设计说明见 docs/design-notes.md#mosslight-app-infrastructure-easingfactory-cs
                return EaseOut(new ParametricBackEase(argument ?? 0.10));

            case "elastic":
                // 照 pcl_ease.make_elastic_out(amplitude=1.0, period=0.3) 的公式实现。
                // WPF 的 ElasticEase 用的是 Oscillations/Springiness 那套参数，
                // 跟 Python 的 amplitude/period 不是一回事，所以也自己实现。
                return EaseOut(new ParametricElasticEase());

            case "spring":
                // 阻尼正弦：过冲一下再收住，cycles/decay 照抄 slide_in.py 的
                // SPRING_CYCLES / SPRING_DECAY。
                // ⚠️ `spring:0.14` 的 0.14 是**过冲比例**，不是振荡次数 ——
                // 早先版本把它塞进 Oscillations，那是错的（数值上不报错，观感完全不同）。
                // 不带参数时 Python 用的是 0.12（`named("spring")` 的默认值）
                return EaseOut(new ParametricDampedSineEase(argument ?? 0.12));

            case "power":
                // `power:5` = `named("power_out", power=5)`，就是 Qt 的 OutQuint
                return EaseOut(new ParametricPowerOutEase(argument ?? 3.0));

            case "pcl_back":
                // PCL 旧版缓出回弹 —— 「PCL 原味」那一档慢轨用的就是它（power=2.0，过冲 11.1%）
                return EaseOut(new ParametricPclBackEase(argument ?? 2.0));

            case "pcl_elastic":
                // PCL 旧版弹性缓出（跟 Penner 那条 `elastic` 是两套公式，别互相替换）
                return EaseOut(new ParametricPclElasticEase(argument ?? 2.0));

            default:
                // 认不出来不要抛（见类注释）。空串 / null 也走这里 —— 预设里的默认值就是 cubic
                return EaseOut(new CubicEase());
        }
    }

    /// <summary>
    /// 这条曲线名会不会**过冲**（峰值越过 1）。
    ///
    /// 给自检和"这条曲线能不能造成回弹"的对照用：`back` / `elastic` / `spring` 三条会，
    /// `cubic` / `quint` / `inout` 三条不会。判据直接来自 <see cref="AnimationCurves.Evaluate"/>，
    /// 也就是 WPF 那边真正在用的同一份公式。
    /// </summary>
    public static bool Overshoots(string? ease)
    {
        for (var i = 0; i <= 1000; i++)
        {
            if (AnimationCurves.Evaluate(ease, i / 1000.0) > 1.0 + 1e-9) return true;
        }
        return false;
    }

    private static T EaseOut<T>(T ease) where T : EasingFunctionBase
    {
        ease.EasingMode = EasingMode.EaseOut;
        return ease;
    }
}
