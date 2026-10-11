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
/// <param name="FadeRatio">
/// 淡入**占整条轨迹时长的比例**：透明度在第 <c>Duration × FadeRatio</c> 毫秒时到 1。
///
/// ## 为什么需要它（这一轮调手感新加的）
/// 以前非 PCL 形态的淡入是"时长 = 整条轨迹、曲线 = 该档自己的缓动"，也就是
/// 透明度跟位移**共用一条曲线**。两个后果，都在这一轮的采样表里量出来了：
///   1. 位移是缓出曲线时，前 10% 的时间里透明度就冲到 0.27~0.65 —— 卡片几乎是一出现
///      就已经看得见了，淡入形同虚设；
///   2. 反过来，在 <c>elastic</c> / <c>back</c> 这类**会过冲**的曲线上，透明度会跟着冲到
///      1.25 / 1.18（渲染时被夹到 1），于是"淡入"在曲线第一个峰就结束了 ——
///      后面三分之二的时间卡片是全亮地在飞。
/// 所以淡入得有一条**属于自己**的时间轴。这里用"占整条轨迹的比例"而不是绝对毫秒：
/// 速度档是乘在 DurationMs 上的，比例写法能让淡入跟着速度档一起缩放
/// （写死毫秒的话，调慢速度就会变成"淡入早就完了、卡片还在慢慢挪"）。
///
/// ⚠️ 默认值 <c>1.0</c> = 时长铺满整条轨迹 —— 那是**改动之前的行为**，
///    自检里那些"拿金标准的预设造出来"的对照用例靠它保持逐值可复现。
///    PCL 形态不吃这个值（它那边是 1.0.0 的 <c>PCL_FADE_MS = 100</c>，
///    见 <see cref="SlideInTrajectory"/>）。
/// </param>
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
    private const string EaseBack = "back:0.10";       // 冲过头 10% 再回来
    private const string EaseElastic = "elastic";      // 弹性：一下冲出去、一下弹回来
    // ⚠️ 这一轮试过用 `spring:0.12` 当「弹性」那档的主段缓动，**量出来发现走不通**：
    //    Simple 分支里的 `spring:` 走的是 AnimationCurves.DampedSine，它前 1/8 会用
    //    `1-(1-t)^2.2` 重整时间轴、scale 却是按重整前的包络峰值归一化的 ——
    //    实际过冲只有设定值的 13%（填 12% 量出来 1.6%），等于没有回弹。
    //    `elastic` 没有这层重整，过冲是实打实的（约 1/3，比例写死在曲线里）。
    //    这条注释留着，免得下一个人再试一遍。
    // 同理没有 `spring:` 常量：弹簧那档的"弹"来自 Spring 形态自己的阻尼正弦
    // （SlideInTrajectory.SpringBump，过冲严格等于 OvershootPx），主段用的是 cubic。

    /// <summary>
    /// 默认那档（<c>slide_left</c>）的 key。7 档的顺序见 <see cref="PresetOrder"/>。
    /// </summary>
    public const string DefaultPreset = "slide_left";
    public const string DefaultSpeed = "normal";

    /// <summary>
    /// 所有预设。**插入顺序就是 Python 里 PRESETS 的顺序**（.NET 的 Dictionary
    /// 不保证顺序，但实测按插入序枚举；要顺序请一律走 <see cref="PresetOrder"/>）。
    ///
    /// ============================================================
    /// 这一轮的参数是**重新调的**（1.0.0 那套手感用户评价是"太鸡肋"）
    /// ============================================================
    ///
    /// 轨迹算法一个字没动（<see cref="SlideInTrajectory"/> 仍然跟 1.0.0 逐值对齐，
    /// `tests/slide_anim_golden.json` 那一节照旧全绿），改的只是这七行数值。
    /// 判据、量法、扫描过程写在实验目录的 `HandProbe` 探针里，结论落在这张表上，
    /// 七条硬指标（每一条都是"能算出数来"的，不是形容词）：
    ///
    ///   ① **位移/时长** 落在 0.22~0.55 px/ms —— 低了显拖沓，高了"啪"一下；
    ///   ② **起步速度** ≥ 位移的 1.1%/ms（缓出曲线的起步速度正比于位移，
    ///      所以只能按自己的位移比，不能用一个绝对像素数去卡）；
    ///   ③ **视觉落位**（位移第一次 ≤2px）在 80% 时长之内；
    ///   ④ **亚像素尾巴**（最后一次 >0.5px 之后剩下的时间）≤25% 时长
    ///      （25%~35% 算"绵长"，七档里只允许一档走这条）；
    ///   ⑤ **淡入配合**：透明度到 0.9（看着已经清楚了）之后，剩下的位移要在
    ///      180ms 内走完 —— 否则就是"淡完了卡片还在飞"；
    ///   ⑥ 回弹档：过冲 ≤25px（按**绝对像素**判，不按百分比：百分比对小位移的档不公平）、
    ///      且可见摆动 ≥1 次（0.5px 以下的不算"看得见"）；
    ///   ⑦ 整列 12 张铺完 0.6~1.4 秒 —— 短了眼睛来不及捕捉"依次"，长了难受。
    ///
    /// 调完的七档（"为什么它跟别的不一样"都写在每一档的注释里）：
    ///   · slide_left      — 120px / 330ms / cubic ／ 基准档：一步到位，收尾不黏
    ///   · slide_up        —  84px / 380ms / quint ／ 唯一"绵长"的一档（亚像素尾巴 130ms）
    ///   · slide_left_bounce — 120px / 460ms / 阻尼正弦 ／ 过冲 18px 后自己收住，**一个峰**
    ///   · pcl_like        —  26px / 300ms / quint ／ 位移最小、单张最慢、整列最快
    ///   · slide_up_elastic — 52px / 560ms / 弹性 ／ 过冲 19.4px 后还**回摆**，七档里唯一两个峰
    ///   · slide_left_back — 120px / 360ms / back:0.10 ／ 冲过头 12px 再退回来，收得最干脆
    ///   · pcl_recipe      —  24px / 300ms / PCL 双轨 + 100ms 淡入 ／ 先快后慢"软着陆"
    ///
    /// ⚠️ 七档的**名字和性格**是用户点名要保住的，所以 Key / Name 一个没改；
    ///    改的只有数值（外加新增的 FadeRatio）。对话框里的"时长 / 位移 / 错峰 / 有无回弹"
    ///    一律**从这张表现算**，不写死 —— 以后再调参，界面自己会对上。
    /// </summary>
    public static IReadOnlyDictionary<string, AnimationPreset> Presets { get; } =
        new Dictionary<string, AnimationPreset>(StringComparer.Ordinal)
        {
            // ---------- 1/7 基准档 ----------
            // 1.0.0：420ms / 120px / 95ms / cubic。
            // 改：时长 420→330、错峰 95→60。位移不动（120px 是"从屏幕外滑进来"的合理距离，
            //     再短就看不出是"滑入"了）。
            // 三条量出来的毛病：起步速度只有 0.82px/ms、半程在 21% 处（冲完干等）、
            //     尾巴 108ms = 26%（拖）。缩短时长直接把三条一起压下来；
            //     cubic 不动 —— 这条曲线的性格就是"干脆"，跟名字对得上。
            // 错峰 60ms（时长的 18%）：12 张 990ms 铺完，是"依次"和"利落"的分界点；
            //     1.0.0 的 95ms 要 1.47 秒，第一张早就停稳了后面的还在等。
            // 淡入 0.75（扫描结果）：透明度到 0.9 是 133ms，那一刻卡片还剩 28px、
            //     正在明显往终点收 —— "一边淡、一边落位"，不是"淡完还悬着"。
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

            // ---------- 2/7 唯一"绵长"的一档 ----------
            // 1.0.0：460ms / 80px / 100ms / quint。
            // 改：位移 80→84、时长 460→380、错峰 100→70。
            // 这一档**故意保留 quint 的长尾巴**：七档里得有且只有一档是"慢慢收"的
            // （quint 自带的长尾巴正好是那个性格，实测亚像素尾巴 130ms，是七档里最长的）。
            // 但 460ms 配 80px 是 0.17px/ms —— 太拖，所以时长收到 380、位移提到 84
            // （0.22px/ms，刚好压在下限上；再慢一档就真成拖沓了）。
            // 跟 cubic 那档的分开点有两处：尾巴长度（130ms vs 53ms）+ **淡入最晚**（0.65：
            // 透明度到 0.9 是 120ms，那一刻位移只剩 8.4px —— "淡着淡着就到位了"）。
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

            // ---------- 3/7 弹簧 ----------
            // 1.0.0：760ms / 120px / 115ms / 阻尼正弦，OvershootPx=28、BounceRatio=0.5。
            // 改：时长 760→460、过冲 28→18、BounceRatio 0.5→0.6、错峰 115→85。
            // 1.0.0 这一档的两个毛病都在采样表上：
            //   · 760ms 里前 380ms 走 120px（0.32px/ms，偏慢），而且**前段是缓出曲线**
            //     —— 它是在"慢慢蹭到 0"的，快到位那一刻速度几乎是 0，紧接着才弹出去，
            //     看着像"停了一下又弹"；
            //   · 过冲 28px = 位移的 23.3%，超过了"看得出来但不夸张"的上限。
            // 改成 460ms / 276ms 滑到位（0.43px/ms，前段终于有速度了）+ 184ms 回弹，
            // 过冲 18px = 15%：**峰值出现在滑到位之后 28ms**，一眼能看出是弹出去的。
            // BounceRatio 0.6 是"滑到位"和"回弹"的配比：再大（0.7）回弹只剩 138ms，
            // 阻尼正弦来不及收敛（尾巴会被 `DisplacementAt` 的终点归零硬切断，
            // 看着像"弹到一半被掐了"）；再小（0.5）前段又开始拖。
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
            // ---------- 4/7 最克制的一档 ----------
            // 1.0.0：340ms / 28px / 55ms / quint。
            // 改：位移 28→26、时长 340→300、错峰 55→45、缓动 quint→**cubic**、淡入 0.75。
            // 缓动为什么换掉：quint 的指数尾巴配在这么短的位移上**量出来很难看** ——
            //   26px 里最后 1px 要走 156ms（占时长 52%），也就是"卡片 120ms 就看着到位了，
            //   然后还要飘半秒"。1.0.0 那一档正是 quint，七档里属它最"黏"。
            //   换成 cubic 之后 1px 尾巴缩到 33ms（11%），而且它仍然是唯一的
            //   "垂直 + 极小位移 + 不回弹"档，跟别的档分得开（见下面的分工表）。
            // 这一档跟 pcl_recipe 的区别靠三点：
            //   · 缓动 —— cubic 单曲线 vs PCL 双轨（快 + 慢两条叠加，收尾是软的）；
            //   · 淡入 —— 0.75（透明度和落位一起收）vs PCL 写死的 100ms/0.33；
            //   · 错峰 —— 45ms vs 32ms。
            // ⚠️ 它跟「左侧滑入」（同样 cubic）不会撞车：位移 26 vs 120（差 4.6 倍）、
            //    方向垂直 vs 水平、时长 300 vs 330、错峰 45 vs 60。量出来的轨迹形状差
            //    主要是因为位移量级差太远，肉眼一眼就分得出"轻轻一张张出现"和"从左边飞进来"。
            // 名字里的"克制"靠的是**位移**，不是把时长拉到很长（那是拖沓，不是克制）：
            // 26px / 300ms = 0.087px/ms，是全表单张最慢的一档，但错峰只有 45ms，
            // 整列 795ms 铺完 —— 单张慢、整列快，看着就是"轻轻地一张张出现"。
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

            // ---------- 5/7 弹性 ----------
            // 1.0.0：900ms / 110px / 95ms / `elastic` 缓动（走 Simple 分支）。
            // 改：时长 900→560、位移 110→52、错峰 95→55。
            // **缓动曲线没换**（还是那条 `elastic`：amplitude=1.0、period=0.3）——
            // 中途试过换成 `spring:0.12`，量出来才发现走不通，记录在这儿免得下次再试：
            //   `spring:` 那条在 Simple 分支里走的是 `AnimationCurves.DampedSine`，
            //   而它**前 1/8 用 `1-(1-t)^2.2` 重整了时间轴**、scale 又是按重整前的包络峰值
            //   归一化的 —— 实测算出来的过冲只有设定值的 **13%**（填 12% 只有 1.6%），
            //   等于没有回弹。`elastic` 那条没有这层重整，过冲是实打实的。
            // 1.0.0 真正的问题是**时长和位移**，不是曲线：
            //   · 900ms 是七档里最长的一档，位移 110px 又最大 → 0.12px/ms，拖；
            //   · 过冲 41px 绝对值太大。这条曲线的过冲**比例**写死在公式里
            //     （amplitude 夹了 ≥1 的下限，调小会让曲线反相），
            //     所以只能靠**缩小位移**来压过冲的绝对像素 —— 110→52 之后
            //     过冲从 41px 掉到 19.4px，屏幕上从"跳出去了"变成"弹了一下"。
            // 这一档跟另外两档回弹的**分界**（探针按 0.5px 阈值数出来的，不是猜的）：
            //   · 弹簧（slide_left_bounce）：冲过头 **1** 次 + 回摆 **1** 次；
            //   · 这一档：冲过头 **2** 次 + 回摆 **2** 次 —— `elastic` 的 2^(-10t) 在前
            //     三分之一里就走完两次摆动，是七档里唯一"弹完还荡"的一档；
            //   · 过冲（slide_left_back）：冲过头 1 次、**没有回摆**（退回来就停）。
            // 位移 52px 也是七档里最小的（比两档 PCL 还小），配上最大的过冲比例 ——
            // 看着就是"没挪多远但弹得很欢"，跟别的档一眼分得开。
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

            // ---------- 6/7 过冲 ----------
            // 1.0.0：520ms / 120px / 85ms / `back:0.18`。
            // 改：时长 520→360、过冲 0.18→0.10、错峰 85→65。
            // 1.0.0 的采样表上这一档最难看的一项不是过冲，是**尾巴 373ms = 时长的 72%**：
            // back 曲线在 58% 处就到峰值了，之后 218ms 全花在"退回 21.6px"上，
            // 慢吞吞地蹭回终点。过冲比例 18% 本身也偏大（位移的 18% = 21.6px，
            // 跟"弹簧"那档的 23% 几乎一样，两档就分不出来了）。
            // 改成 10%（12px）+ 360ms：峰值出现在 52% 处，剩下 173ms 收干净 ——
            // 跟弹簧那档的区别因此变成"**收得干脆** vs **弹一下再收**"，肉眼分得开。
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
            // ---------- 7/7 PCL 原味 ----------
            // 1.0.0：350ms / 16px / 25ms / Pcl 形态。
            // 改：位移 16→24、时长 350→300、错峰 25→32。
            // 形态（双轨 + 淡入）一个字没动 —— 那是 1.0.0 里唯一"配方正确"的一档，
            // 问题只在数值：16px 位移配 25ms 错峰，12 张卡 0.6 秒就铺完了，
            // 快得像整列同时闪了一下（"没有层次"就是错峰太小）。
            // 位移提到 24px（双轨合成后过冲约 2.9px，刚过可见线）、错峰提到 32ms
            // （12 张 0.65 秒 → 现在 0.65 秒铺完但仍能数出顺序），
            // 时长 300ms 让快轨落在 213ms、慢轨 300ms —— 照 1.0.0 自己的比例
            // （250/350）等比缩下来的，所以"先快后慢"的手感没变。
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
    /// <summary>
    /// 下拉框 / 设置界面里的顺序（照 Python 的 PRESET_ORDER，**不是**字典序）。
    /// ⚠️ 它跟上面 <see cref="Presets"/> 的书写顺序**不一样**：
    /// 字典里 slide_left_back 写在 slide_up_elastic 前面，这里必须反过来 ——
    /// 照字典顺序抄，下拉框的排序就跟 1.0.0 分叉了。
    ///
    /// 这一轮的调参**没有动顺序**：七档的性格分工是
    /// 「干脆（left）→ 绵长（up）→ 弹一下（spring）→ 最克制（pcl_like）→ 荡两下（elastic）
    ///   → 过冲干脆（back）→ 软着陆（pcl_recipe）」，
    /// 顺着列表看下来正好是从"动静大"到"动静小"，不用重排。
    /// </summary>
    public static IReadOnlyList<string> PresetOrder { get; } = new[]
    {
        "slide_left", "slide_up", "slide_left_bounce", "pcl_like",
        "slide_up_elastic", "slide_left_back", "pcl_recipe",
    };

    /// <summary>
    /// 给版本列表用的更短参数：分组展开后组内卡片一下出来很多，
    /// 用搜索页那套参数会显得拖沓。只覆盖 duration / interval / offset 三项，
    /// 方向、形态、缓动、淡入全跟着主表走。
    ///
    /// 这一轮跟着主表**等比缩了约 15%**（主表整体缩短之后，变体不缩就会反过来
    /// 比主表还长 —— 那是"展开分组比搜索页还慢"，正好反了）。
    /// 硬指标不变：位移/时长仍在 0.22~0.55、整列铺完仍在 0.6~1.4 秒。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (int DurationMs, int IntervalMs, int OffsetPx)> VersionVariant =
        new Dictionary<string, (int, int, int)>(StringComparer.Ordinal)
        {
            ["slide_left"] = (280, 55, 80),
            ["slide_up"] = (340, 65, 60),
            ["slide_left_bounce"] = (390, 65, 90),
            ["pcl_like"] = (255, 35, 22),
            ["slide_left_back"] = (305, 50, 80),
            ["slide_up_elastic"] = (440, 48, 75),
            // PCL 那条列表进场的原数值就是"位移很小"（16px → 这一轮 24px），组内卡片照搬
            ["pcl_recipe"] = (255, 26, 20),
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
