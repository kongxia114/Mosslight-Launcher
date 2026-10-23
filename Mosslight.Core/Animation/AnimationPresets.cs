namespace Mosslight.Core.Animation;

/// <summary>卡片从哪边滑进来。</summary>
public enum AnimationDirection
{
    /// <summary>水平：左右滑。</summary>
    Horizontal,

    /// <summary>垂直：上下滑。</summary>
    Vertical,
}

public enum AnimationStyle
{
    /// <summary>一条曲线滑到位。</summary>
    Simple,

    /// <summary>滑到位 → 再补一段小回弹（两段，比较"硬"）。</summary>
    Bounce,

    /// <summary>真·弹簧：阻尼正弦，过冲后自己收敛回来（连贯）。</summary>
    Spring,

    /// <summary>PCL 那套：位移拆两条并行曲线 + 淡入（最自然）。</summary>
    Pcl,
}

/// <summary>
/// 一个入场动画预设；⚠️ Name / Description 是**界面文案**：翻不翻由界面层调 Localizer.Tr() 决定，
/// 这一层保持中文原样，别在这儿翻两遍。
/// </summary>
public sealed record AnimationPreset(
    string Key,
    string Name,
    string Description,
    AnimationDirection Direction,
    int DurationMs,
    int OffsetPx,
    int IntervalMs,
    AnimationStyle Style = AnimationStyle.Simple,
    int OvershootPx = 12,
    double BounceRatio = 0.72,
    string Ease = "cubic",
    double FadeRatio = 1.0);

/// <summary>入场动画的预设表。</summary>
public static class AnimationPresets
{
    // 形态字符串
    //
    // 这几个常量只用来**在表里写得清楚**，也是配置 / 日志里可能出现的字面量。
    // 外面拿到的是枚举，不是这些字符串 —— 字符串比较容易被拼错还没人拦。

    private const string AnimSimple = "simple";
    private const string AnimBounce = "bounce";
    private const string AnimSpring = "spring";
    private const string AnimPcl = "pcl";

    // 缓动名：前三个是 Qt 内置，带 `back:` / `elastic:` / `spring:` 的是参数化版本；由 EasingFactory 解析。

    private const string EaseCubic = "cubic";          // OutCubic：起步快、结尾缓
    private const string EaseQuint = "quint";          // OutQuint：起步更快、收尾更绵长 —— 更"顺"
    private const string EaseInOut = "inout";          // InOutCubic：两头慢中间快，最柔和
    private const string EaseBack = "back:0.10";       // 冲过头 10% 再回来
    private const string EaseElastic = "elastic";      // 弹性：一下冲出去、一下弹回来
    // ⚠️ 不要用 `spring:` 当“弹性”档主段缓动：DampedSine 的时间轴重整会让实际过冲只剩设定值的约 13%。

    /// <summary>默认那档（slide_left）的 key。</summary>
    public const string DefaultPreset = "slide_left";
    public const string DefaultSpeed = "normal";

    public static IReadOnlyDictionary<string, AnimationPreset> Presets { get; } =
        new Dictionary<string, AnimationPreset>(StringComparer.Ordinal)
        {
            // 1/7 基准档：330ms / 120px / 60ms / cubic，淡入 0.75；12 张约 990ms 铺完。
            ["slide_left"] = new AnimationPreset(
                Key: "slide_left",
                Name: "左侧滑入",
                Description: "从左边滑进来，一步到位（默认）",
                Direction: AnimationDirection.Horizontal,
                DurationMs: 330,
                OffsetPx: 120,
                IntervalMs: 60,
                Ease: EaseCubic,
                FadeRatio: 0.75),

            ["slide_up"] = new AnimationPreset(
                Key: "slide_up",
                Name: "下方滑入",
                Description: "从下面升上来，一路淡入到最后一刻（easeOutQuint）",
                Direction: AnimationDirection.Vertical,
                DurationMs: 380,
                OffsetPx: 84,
                IntervalMs: 70,
                Ease: EaseQuint,
                FadeRatio: 0.65),

            // 3/7 弹簧档：460ms / 120px / 85ms，滑到位 276ms + 回弹 184ms，OvershootPx=18（15%）、BounceRatio=0.6。
            // ⚠️ BounceRatio 不宜超过 0.6，否则回弹时间不足，阻尼正弦尾巴会被终点归零硬切。
            ["slide_left_bounce"] = new AnimationPreset(
                Key: "slide_left_bounce",
                Name: "左侧滑入（弹簧）",
                Description: "从左边滑进来，冲过头 15% 再自己收住（阻尼正弦，连贯的一下）",
                Direction: AnimationDirection.Horizontal,
                DurationMs: 460,
                OffsetPx: 120,
                IntervalMs: 85,
                Style: AnimationStyle.Spring,
                OvershootPx: 18,
                BounceRatio: 0.6,
                Ease: EaseCubic,
                FadeRatio: 0.45),
            // 4/7 克制档：300ms / 26px / 45ms / cubic；单张最慢，但错峰小，整列 795ms 铺完。
            ["pcl_like"] = new AnimationPreset(
                Key: "pcl_like",
                Name: "仿 PCL2（克制）",
                Description: "位移很短、不回弹、干脆地淡进来 —— 最接近原生控件的那种顺滑",
                Direction: AnimationDirection.Vertical,
                DurationMs: 300,
                OffsetPx: 26,
                IntervalMs: 45,
                Ease: EaseCubic,
                FadeRatio: 0.75),

            ["slide_up_elastic"] = new AnimationPreset(
                Key: "slide_up_elastic",
                Name: "下方滑入（弹性）",
                Description: "从下面升上来，弹得最欢的一档：冲过头之后还要回摆两下才停",
                Direction: AnimationDirection.Vertical,
                DurationMs: 560,
                OffsetPx: 52,
                IntervalMs: 55,
                Ease: EaseElastic,
                FadeRatio: 0.21),

            // 6/7 过冲档：360ms / 120px / 65ms / back:0.10，峰值约在 52%，收尾 173ms。
            ["slide_left_back"] = new AnimationPreset(
                Key: "slide_left_back",
                Name: "左侧滑入（过冲 10%）",
                Description: "滑过头 10% 再退回来，收得干脆。过冲量可调，不是 Qt 那个写死的 10%",
                Direction: AnimationDirection.Horizontal,
                DurationMs: 360,
                OffsetPx: 120,
                IntervalMs: 65,
                Ease: EaseBack,
                FadeRatio: 0.45),
            // 7/7 PCL 原味：300ms / 24px / 32ms / Pcl 形态；双轨 + 淡入保持不变。
            ["pcl_recipe"] = new AnimationPreset(
                Key: "pcl_recipe",
                Name: "PCL 原味（拆两条 + 淡入）",
                Description: "照 PCL 的真实配方：位移拆成快、慢两条并行曲线，同时淡入。最自然的一档，建议先试它",
                Direction: AnimationDirection.Vertical,
                DurationMs: 300,
                OffsetPx: 24,
                IntervalMs: 32,
                Style: AnimationStyle.Pcl,
                Ease: EaseCubic,
                FadeRatio: 0.42),
        };
    /// <summary>下拉框 / 设置界面里的顺序。</summary>
    public static IReadOnlyList<string> PresetOrder { get; } = new[]
    {
        "slide_left", "slide_up", "slide_left_bounce", "pcl_like",
        "slide_up_elastic", "slide_left_back", "pcl_recipe",
    };

    // 给版本列表用的更短参数：分组展开后组内卡片一下出来很多，用搜索页那套参数会显得拖沓。
    private static readonly IReadOnlyDictionary<string, (int DurationMs, int IntervalMs, int OffsetPx)> VersionVariant =
        new Dictionary<string, (int, int, int)>(StringComparer.Ordinal)
        {
            ["slide_left"] = (280, 55, 80),
            ["slide_up"] = (340, 65, 60),
            ["slide_left_bounce"] = (390, 65, 90),
            ["pcl_like"] = (255, 35, 22),
            ["slide_left_back"] = (305, 50, 80),
            ["slide_up_elastic"] = (440, 48, 75),
            ["pcl_recipe"] = (255, 26, 20),
        };

    // 速度档位：时长和错峰一起乘，不是各写一份。
    // 要放慢整批主要靠加大错峰；只放慢单张会让整体断断续续。

    /// <summary>速度档位 → 倍率。</summary>
    public static IReadOnlyDictionary<string, double> Speeds { get; } =
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["fast"] = 0.7,
            ["normal"] = 1.0,
            ["slow"] = 1.5,
            ["very_slow"] = 2.2,
            ["extreme"] = 3.2,
        };

    // 速度档位 → 界面文案（中文原文，翻不翻由界面层决定）。
    private static readonly IReadOnlyDictionary<string, string> SpeedLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fast"] = "快（0.7×）",
            ["normal"] = "标准（1×）",
            ["slow"] = "慢（1.5×）",
            ["very_slow"] = "很慢（2.2×）",
            ["extreme"] = "夸张（3.2×）",
        };

    public static IReadOnlyList<string> SpeedOrder { get; } = new[]
    {
        "fast", "normal", "slow", "very_slow", "extreme",
    };

    /// <summary>按 key 取预设；认不出来就退回默认 —— **不抛异常**。</summary>
    public static AnimationPreset Preset(string? key)
        => key is not null && Presets.TryGetValue(key, out var preset) ? preset : Presets[DefaultPreset];

    /// <summary>版本列表里的卡片用哪套参数（同一风格、更短的时长）。</summary>
    public static AnimationPreset ForVersions(string? key)
    {
        var baseline = Preset(key);
        if (!VersionVariant.TryGetValue(baseline.Key, out var variant))
        {
            // 主表里有、变体表里没有（以后新加的档）→ 原样用，别当成错误
            return baseline;
        }
        return baseline with
        {
            DurationMs = variant.DurationMs,
            IntervalMs = variant.IntervalMs,
            OffsetPx = variant.OffsetPx,
        };
    }

    /// <summary>速度档位 → 倍率（认不出来就按标准 1.0）。</summary>
    public static double SpeedFactor(string? key)
        => key is not null && Speeds.TryGetValue(key, out var factor) ? factor : Speeds[DefaultSpeed];

    /// <summary>
    /// 把预设施加一个速度倍率；⚠️ 取整必须跟 Python 的 `int(round(...))` 对齐：那是**银行家舍入**
    /// （.5 进偶数），跟"四舍五入"在 0.5 这类值上差 1。
    /// </summary>
    public static AnimationPreset Scaled(AnimationPreset p, double factor)
    {
        if (factor == 1.0) return p;
        return p with
        {
            DurationMs = Math.Max(40, (int)Math.Round(p.DurationMs * factor, MidpointRounding.ToEven)),
            IntervalMs = Math.Max(8, (int)Math.Round(p.IntervalMs * factor, MidpointRounding.ToEven)),
        };
    }

    /// <summary>速度档位的界面文案（中文原文，界面层再决定要不要翻）。</summary>
    public static string SpeedText(string? key)
        => key is not null && SpeedLabels.TryGetValue(key, out var text) ? text : SpeedLabels[DefaultSpeed];

    /// <summary>方向的界面文案（中文原文，界面层再决定要不要翻）。</summary>
    public static string DirectionText(AnimationDirection d)
        => d == AnimationDirection.Horizontal ? "水平（左右）" : "垂直（上下）";

    /// <summary>形态 → 配置里那种字符串。</summary>
    public static string StyleName(AnimationStyle style) => style switch
    {
        AnimationStyle.Bounce => AnimBounce,
        AnimationStyle.Spring => AnimSpring,
        AnimationStyle.Pcl => AnimPcl,
        _ => AnimSimple,
    };
}
