namespace Mosslight.Core.Animation;

/// <summary>卡片从哪边滑进来（对应 Python 的 DIR_HORIZONTAL / DIR_VERTICAL）。</summary>
public enum AnimationDirection
{
    /// <summary>水平：左右滑（Python 的 `h`）</summary>
    Horizontal,

    /// <summary>垂直：上下滑（Python 的 `v`）</summary>
    Vertical,
}

/// <summary>
/// 动画形态。四个值对应 Python 的 ANIM_SIMPLE / ANIM_BOUNCE / ANIM_SPRING / ANIM_PCL：
/// 界面层按这个分支决定"怎么滑"，而不是按 key 判断 —— 加第四种风格只需加预设，不用改播放代码。
/// </summary>
public enum AnimationStyle
{
    /// <summary>一条曲线滑到位</summary>
    Simple,

    /// <summary>滑到位 → 再补一段小回弹（两段，比较"硬"）</summary>
    Bounce,

    /// <summary>真·弹簧：阻尼正弦，过冲后自己收敛回来（连贯）</summary>
    Spring,

    /// <summary>PCL 那套：位移拆两条并行曲线 + 淡入（最自然）</summary>
    Pcl,
}

/// <summary>
/// 一个入场动画预设。字段和 <c>core/anim_prefs.py</c> 的 frozen dataclass 一一对应，
/// **语义没动**：<paramref name="OffsetPx"/> 是"起点离终点多远"（正数），
/// 不是坐标系里的位移量 —— 水平就是往左偏、垂直就是往下偏。
///
/// ⚠️ <paramref name="Name"/> / <paramref name="Description"/> 是**界面文案**：
/// 翻不翻由界面层调 <c>Localizer.Tr()</c> 决定，这一层保持中文原样，别在这儿翻两遍。
/// </summary>
/// <param name="Key">配置里存的字符串（`slide_left` 这种，永远不翻）</param>
/// <param name="Name">界面上显示的名字</param>
/// <param name="Description">一句话说明</param>
/// <param name="Direction">从哪边滑进来</param>
/// <param name="DurationMs">单张卡片滑完要多久（毫秒）</param>
/// <param name="OffsetPx">起点离终点多远（像素，正数）</param>
/// <param name="IntervalMs">相邻两张错开多久（毫秒）</param>
/// <param name="Style">动画形态</param>
/// <param name="OvershootPx">只对回弹类（Bounce / Spring）有效：冲过头多少像素</param>
/// <param name="BounceRatio">只对 Bounce 有效：多久滑到位（剩下的时间用来回弹）</param>
/// <param name="Ease">主段滑入用哪条缓动曲线（字符串交给 EasingFactory 解析）</param>
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
    string Ease = "cubic");

/// <summary>
/// 入场动画的预设表。
///
/// 界面里所有"卡片滑入"都用这里的预设决定**方向、时长、错峰间隔、回弹**。
/// 想要第四种风格，只要往 <see cref="Presets"/> 里加一行、再给个名字就行，
/// 不用去改播放代码和各个页面。
///
/// ## 关于 PCL2 那种风格
///
/// PCL2 是 C# / WPF 写的，它的 `DoubleAnimation` + `CubicEase` 那套在 Qt 上没法直接用，
/// 但"感觉"是能对出来的。它给人顺滑的原因大概三条：
///   1. **位移很短**（几十像素，不是从屏幕外面飞进来）
///   2. **时长偏短**（0.2 秒上下，不做慢动作）
///   3. **缓动是"快出慢收"**（EaseOut），并且**不做回弹**（回弹会显得软件味重）
/// 所以「仿 PCL2」这一档刻意把位移调到 28px、时长 170ms 上下、无回弹 ——
/// 跟其他几档比，它是最"没存在感"的那个，也就是最像原生控件的那种。
///
/// ## 回弹（overshoot）
///
/// 回弹**不是**靠 Back 曲线做的：Back 是先往**反方向**拉一下再过去，
/// 用在"从左边滑入"上会先往右退一点，看着很别扭。
/// 真正的"滑到位再弹一下"是三段：滑到 0 → 冲过头 → 收回来 —— 那是 Bounce / Spring 形态的事。
/// </summary>
public static class AnimationPresets
{
    // ---------- 形态字符串 ----------
    //
    // 这几个常量只用来**在表里写得清楚**，也是配置 / 日志里可能出现的字面量。
    // 外面拿到的是枚举，不是这些字符串 —— 字符串比较容易被拼错还没人拦。

    private const string AnimSimple = "simple";
    private const string AnimBounce = "bounce";
    private const string AnimSpring = "spring";
    private const string AnimPcl = "pcl";

    // ---------- 缓动曲线名 ----------
    //
    // 前三个是 Qt 内置的；带 `back:` / `elastic:` / `spring:` 前缀的是参数化版本 ——
    // Qt 内置的过冲是写死的（OutBack 永远冲 10%），参数化之后才能"想弹多少写多少"。
    // C# 这边由 Mosslight.App 的 EasingFactory 负责把名字解析成 WPF 的缓动函数。

    private const string EaseCubic = "cubic";          // OutCubic：起步快、结尾缓
    private const string EaseQuint = "quint";          // OutQuint：起步更快、收尾更绵长 —— 更"顺"
    private const string EaseInOut = "inout";          // InOutCubic：两头慢中间快，最柔和
    private const string EaseBack = "back:0.18";       // 冲过头 18% 再回来（Qt 的 OutBack 只有 10%）
    private const string EaseElastic = "elastic";      // 弹性：来回弹好几下
    private const string EaseSpring = "spring:0.14";    // 阻尼正弦：弹一下收住

    public const string DefaultPreset = "slide_left";
    public const string DefaultSpeed = "normal";

    /// <summary>
    /// 所有预设。**插入顺序就是 Python 里 PRESETS 的顺序**（.NET 的 Dictionary
    /// 不保证顺序，但实测按插入序枚举；要顺序请一律走 <see cref="PresetOrder"/>）。
    /// </summary>
    public static IReadOnlyDictionary<string, AnimationPreset> Presets { get; } =
        new Dictionary<string, AnimationPreset>(StringComparer.Ordinal)
        {
            ["slide_left"] = new AnimationPreset(
                Key: "slide_left",
                Name: "左侧滑入",
                Description: "从左边滑进来，干脆利落（默认）",
                Direction: AnimationDirection.Horizontal,
                DurationMs: 420,
                OffsetPx: 120,
                IntervalMs: 95),

            ["slide_up"] = new AnimationPreset(
                Key: "slide_up",
                Name: "下方滑入",
                Description: "从下面升上来，收尾绵长（easeOutQuint）",
                Direction: AnimationDirection.Vertical,
                DurationMs: 460,
                OffsetPx: 80,
                IntervalMs: 100,
                Ease: EaseQuint),

            ["slide_left_bounce"] = new AnimationPreset(
                Key: "slide_left_bounce",
                Name: "左侧滑入（弹簧）",
                Description: "从左边滑进来，用弹簧的方式过冲一下再自己收住（连贯，不是两段拼的）",
                Direction: AnimationDirection.Horizontal,
                DurationMs: 760,
                OffsetPx: 120,
                IntervalMs: 115,
                Style: AnimationStyle.Spring,
                OvershootPx: 28,
                BounceRatio: 0.5),

            // ⚠️ 这一档是 Simple + quint，**不是** Pcl 形态：
            // "仿 PCL2"是照着 PCL 的**观感**调出来的一组数值，不是它的实现方式。
            ["pcl_like"] = new AnimationPreset(
                Key: "pcl_like",
                Name: "仿 PCL2（克制）",
                Description: "位移很短、不回弹、收尾绵长 —— 最接近原生控件的那种顺滑",
                Direction: AnimationDirection.Vertical,
                DurationMs: 340,
                OffsetPx: 28,
                IntervalMs: 55,
                Ease: EaseQuint),

            ["slide_up_elastic"] = new AnimationPreset(
                Key: "slide_up_elastic",
                Name: "下方滑入（弹性）",
                Description: "从下面升上来，像弹簧一样来回弹几下才停（参数化 elastic）",
                Direction: AnimationDirection.Vertical,
                DurationMs: 900,
                OffsetPx: 110,
                IntervalMs: 95,
                Ease: EaseElastic),

            ["slide_left_back"] = new AnimationPreset(
                Key: "slide_left_back",
                Name: "左侧滑入（过冲 18%）",
                Description: "滑过头 18% 再退回来。过冲量可调，不是 Qt 那个写死的 10%",
                Direction: AnimationDirection.Horizontal,
                DurationMs: 520,
                OffsetPx: 120,
                IntervalMs: 85,
                Ease: EaseBack),

            // 只有这一档是 Pcl 形态：位移拆两条并行曲线 + 淡入
            ["pcl_recipe"] = new AnimationPreset(
                Key: "pcl_recipe",
                Name: "PCL 原味（拆两条 + 淡入）",
                Description: "照 PCL 的真实配方：位移拆成快、慢两条并行曲线，同时淡入。最自然的一档，建议先试它",
                Direction: AnimationDirection.Vertical,
                DurationMs: 350,
                OffsetPx: 16,
                IntervalMs: 25,
                Style: AnimationStyle.Pcl),
        };

    /// <summary>
    /// 下拉框 / 设置界面里的顺序（照 Python 的 PRESET_ORDER，**不是**字典序）。
    /// ⚠️ 它跟上面 <see cref="Presets"/> 的书写顺序**不一样**：
    /// 字典里 slide_left_back 写在 slide_up_elastic 前面，这里必须反过来 ——
    /// 照字典顺序抄，下拉框的排序就跟 1.0.0 分叉了。
    /// </summary>
    public static IReadOnlyList<string> PresetOrder { get; } = new[]
    {
        "slide_left", "slide_up", "slide_left_bounce", "pcl_like",
        "slide_up_elastic", "slide_left_back", "pcl_recipe",
    };

    /// <summary>
    /// 给版本列表用的更短参数：分组展开后组内卡片一下出来很多，
    /// 用搜索页那套参数会显得拖沓。只覆盖 duration / interval / offset 三项，
    /// 方向、形态、缓动全跟着主表走。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (int DurationMs, int IntervalMs, int OffsetPx)> VersionVariant =
        new Dictionary<string, (int, int, int)>(StringComparer.Ordinal)
        {
            ["slide_left"] = (330, 70, 90),
            ["slide_up"] = (360, 75, 60),
            ["slide_left_bounce"] = (560, 85, 100),
            ["pcl_like"] = (280, 40, 22),
            ["slide_left_back"] = (440, 65, 95),
            ["slide_up_elastic"] = (760, 75, 85),
            // PCL 那条列表进场的原数值就是"位移很小"（16px），组内卡片照搬
            ["pcl_recipe"] = (300, 22, 14),
        };

    // ============================================================
    // 速度档位
    // ============================================================
    //
    // 时长和错峰是**乘**上去的，不是各自写死一份 —— 这样加档位不用改每个预设，
    // 也不会出现"改了一档忘了改另一档"。
    //
    // 换算一下"整批大概多久铺完"（一批 12 张）：
    //     铺完时间 ≈ 错峰 × (张数 - 1) + 单张时长
    // 以左侧滑入为例（原始 420ms / 95ms）：
    //     标准 1.0× → 95×11 + 420  ≈ 1.47s   ← 默认档就差不多是"1.5 秒铺完"
    //     慢   1.5× → 143×11 + 630 ≈ 2.2s
    //     夸张 2.2× → 209×11 + 924 ≈ 3.2s
    //
    // 注意"太快"多半不是单张滑得太快，而是**错峰太小**：卡片几乎同时出现，
    // 眼睛就来不及捕捉"依次滑入"的过程。要放慢就调错峰，这个倍率是两者一起放大的。

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

    /// <summary>速度档位 → 界面文案（中文原文，翻不翻由界面层决定）。</summary>
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

    /// <summary>
    /// 按 key 取预设；认不出来就退回默认 —— **不抛异常**。
    /// 配置是被手改过的（`appearance.py` 那边也只做白名单纠正），
    /// 为了一个拼错的 key 让启动器起不来是不划算的。
    /// </summary>
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
    /// 把预设施加一个速度倍率。
    ///
    /// 错峰也跟着一起缩放：只放慢单张、不放慢错峰的话，卡片会一张张慢慢出现，
    /// 整体节奏反而变得断断续续。
    ///
    /// ⚠️ 取整必须跟 Python 的 `int(round(...))` 对齐：那是**银行家舍入**（.5 进偶数），
    /// 跟"四舍五入"在 0.5 这类值上差 1。所以这里显式写 <see cref="MidpointRounding.ToEven"/>，
    /// 不要图省事用默认重载 —— 默认值哪天变了不会有人告诉你。
    /// 下限 40ms / 8ms 是防呆：倍率被配置写成 0 时，动画不该变成"瞬移"。
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

    /// <summary>
    /// 形态 → 配置里那种字符串。给日志 / 自检看的，
    /// 跟 <see cref="AnimationStyle"/> 的四个值一一对应。
    /// </summary>
    public static string StyleName(AnimationStyle style) => style switch
    {
        AnimationStyle.Bounce => AnimBounce,
        AnimationStyle.Spring => AnimSpring,
        AnimationStyle.Pcl => AnimPcl,
        _ => AnimSimple,
    };
}
