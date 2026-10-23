using System.Windows.Media.Animation;
using Mosslight.Core.Animation;

namespace Mosslight.App.Infrastructure;

public static class EasingFactory
{
    /// <summary>
    /// 按预设里的 Ease 字符串造缓动函数；⚠️ **不缓存实例**：这些缓动函数是 Freezable，同一个实例
    /// 被多个动画同时用，其中一个改了属性会连带影响别人。
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
                // ⚠️ WPF 的 QuinticEase（EaseOut）就是 1-(1-t)^5；这里改用自实现只是为了和 Core 共用同一份公式，行为等价。
                return EaseOut(new ParametricPowerOutEase(5.0));

            case "inout":
                // 两头慢中间快，最柔和的一种 —— 唯一不用 EaseOut 的。
                // WPF 的 EaseInOut 分段跟 Qt 的 InOutCubic 逐值一样（0.5 处都是 4t³ / 1-(2-2t)³/2）
                return new CubicEase { EasingMode = EasingMode.EaseInOut };

            case "back":
                return EaseOut(new ParametricBackEase(argument ?? 0.10));

            case "elastic":
                // 照 pcl_ease.make_elastic_out(amplitude=1.0, period=0.3) 的公式实现。
                // WPF 的 ElasticEase 用的是 Oscillations/Springiness 那套参数，
                // 跟 Python 的 amplitude/period 不是一回事，所以也自己实现。
                return EaseOut(new ParametricElasticEase());

            case "spring":
                // 阻尼正弦：过冲后收住，cycles / decay 照 SPRING_CYCLES / SPRING_DECAY。
                // ⚠️ `spring:` 后的参数是过冲比例，不是振荡次数。
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

    /// <summary>这条曲线名会不会**过冲**（峰值越过 1）。</summary>
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
