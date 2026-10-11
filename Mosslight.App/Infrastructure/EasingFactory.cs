using System.Globalization;
using System.Windows.Media.Animation;

namespace Mosslight.App.Infrastructure;

/// <summary>
/// 缓动曲线名 → WPF 的缓动函数。
///
/// Python 那份（`core/anim_prefs.py` 的 EASE_* 常量 + `ui/widgets/pcl_ease.py`）里，
/// 曲线名是**字符串**，因为 Qt 只认它自带的几条，参数化的过冲得自己写。
///
/// C# 这边分两路：
///   · `cubic` / `quint` / `inout` —— 语义跟 WPF 内置的能对上，直接用内置的；
///   · `back:参数` / `elastic` / `spring:参数` —— 走 <see cref="ParametricEase"/> 里
///     照抄 `pcl_ease.py` 的实现（WPF 内置的 BackEase / ElasticEase 是另一套参数化，
///     表达不出"过冲就是 18%"这种话）。
///
/// 为什么名字里有 `back:0.18` / `spring:0.14` 这种带冒号的写法：那是 Python 侧的
/// 参数化约定（Qt 内置 OutBack 永远冲 10%），C# 这边跟着解析，好让预设表两边能逐字对照。
/// </summary>
public static class EasingFactory
{
    /// <summary>
    /// 按预设里的 Ease 字符串造缓动函数；认不出来回 EaseOut 的三次曲线。
    ///
    /// ⚠️ **不缓存实例**：这些缓动函数是 <c>Freezable</c>，同一个实例被多个动画同时用，
    /// 其中一个改了属性会连带影响别人。动画本来就不常建，省这点分配不值得冒险。
    /// （Python 那边反倒必须缓存：Qt 最多只允许 10 条自定义曲线。）
    ///
    /// ⚠️ 一律返回 <c>EaseOut</c>（除了 `inout`）：PCL2 给人顺滑的核心就是"快出慢收"，
    /// 换成 EaseIn 会变成"先慢后猛地撞上去"，观感完全相反。
    /// </summary>
    public static IEasingFunction Create(string? ease)
    {
        // 空 / null 跟"认不出来"一样处理：预设里的默认值就是 cubic
        if (string.IsNullOrWhiteSpace(ease)) return EaseOut(new CubicEase());

        var (name, argument) = Parse(ease);

        switch (name)
        {
            case "cubic":
                // ≈ Python 的 named("power_out", power=3)
                return EaseOut(new CubicEase());

            case "quint":
                // ⚠️ WPF 的 QuinticEase 并不是 1-(1-t)^5，是它自己的四次多项式
                // （0.5 处 0.94921875）；Python 的 EASE_QUINT 用的是 Qt 的 OutQuint。
                // 两者都是"比 cubic 更绵长的收尾"，这条按 WPF 内置的走。
                return EaseOut(new QuinticEase());

            case "inout":
                // 两头慢中间快，最柔和的一种 —— 唯一不用 EaseOut 的
                return new CubicEase { EasingMode = EasingMode.EaseInOut };

            case "back":
                // 过冲曲线：**必须**用自己实现的 ParametricBackEase。
                // WPF 的 BackEase.Amplitude 不是过冲比例、只是公式系数（Amplitude=1
                // 实测过冲才约 1.7%），表达不出 Python 的"过冲 18%"。
                // 参数是过冲比例，照传；`back:` 参数写坏了退回 0.18（预设里的默认值）。
                return EaseOut(new ParametricBackEase(argument ?? 0.18));

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
                return EaseOut(new ParametricDampedSineEase(argument ?? 0.14));

            default:
                // 认不出来不要抛：预设表是跟着 Python 走的，那边加一条新的曲线名时，
                // 这里应该退化成"普通缓动"，而不是让整个界面在动画开始时崩掉。
                return EaseOut(new CubicEase());
        }
    }

    /// <summary>
    /// 拆 `名字:参数`。参数用不变文化解析 —— 配置和预设里都是小数点写法，
    /// 跟着系统区域走的话，逗号小数点的系统上会解析失败。
    /// </summary>
    private static (string Name, double? Argument) Parse(string ease)
    {
        var text = ease.Trim();
        var colon = text.IndexOf(':');
        if (colon < 0) return (text.ToLowerInvariant(), null);

        var name = text[..colon].Trim().ToLowerInvariant();
        var raw = text[(colon + 1)..].Trim();
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return (name, value);
        }
        // `back:` 这种参数写坏了的，当没给参数处理，继续用曲线自己的默认值
        return (name, null);
    }

    private static T EaseOut<T>(T ease) where T : EasingFunctionBase
    {
        ease.EasingMode = EasingMode.EaseOut;
        return ease;
    }
}
