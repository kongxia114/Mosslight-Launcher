namespace Mosslight.Core.Logging;

/// <summary>
/// 一条日志的级别。
///
/// ⚠️ 顺序**就是严重程度从高到低**，也**就是日志窗口里那排计数按钮的顺序**
///    （照 1.0.0 `ui/dialogs/log_window.py` 的 <c>LEVELS</c>）。
///    加一级只要往这里加一个值、再在下面的 <see cref="LogLine.LevelNames"/> 里补个词，
///    窗口那边是照着枚举画的，不用改。
/// </summary>
public enum LogLevel
{
    /// <summary>FATAL —— 致命</summary>
    Fatal,

    /// <summary>ERROR —— 错误</summary>
    Error,

    /// <summary>WARN —— 警告</summary>
    Warn,

    /// <summary>INFO —— 信息</summary>
    Info,

    /// <summary>DEBUG —— 调试</summary>
    Debug,

    /// <summary>
    /// 认不出级别的行（启动器自己的消息、游戏的裸输出等），**永远显示、没有开关可点**
    /// （1.0.0 的 <c>owner = ""</c> 就是这个意思）。
    /// </summary>
    None,
}

/// <summary>
/// 一行日志的**归属**：谁发的、哪一级、什么内容。
/// </summary>
/// <param name="Text">原文（不加工 —— 显示、导出都用它）</param>
/// <param name="Level">
/// 这一行的级别。⚠️ 它有两种来源，别混：
///   · 行里**自带**级别标记（`[12:00:00] [Client thread/INFO]: ...`）→ 就是它；
///   · 行里没有标记，但它是**上一条的续行**（堆栈）→ 跟上一条同级别
///     （见 <see cref="LogLine.IsContinuation"/>）；
///   · 两者都不是 → <see cref="LogLevel.None"/>（永远显示）。
/// </param>
/// <param name="IsContinuation">这一行是不是上一条的续行（堆栈）。过滤和上色都要跟着上一条走。</param>
/// <param name="IsLauncher">是不是启动器自己发的（`[启动器] ` 前缀，1.0.0 的 <c>LOG_PREFIX</c>）。</param>
/// <param name="At">收到它的时刻（本地时间）。窗口靠它显示"这条是什么时候来的"。</param>
public sealed record LogEntry(
    string Text,
    LogLevel Level,
    bool IsContinuation,
    bool IsLauncher,
    DateTimeOffset At)
{
    /// <summary>这一行现在**归哪一级管**（续行归上一条；<see cref="LogLevel.None"/> = 不属于任何一级）。</summary>
    public LogLevel Owner => Level;
}

/// <summary>
/// 日志文本 → 级别 / 续行 的判定，外加噪音过滤。
///
/// 移植自 1.0.0 <c>ui/dialogs/log_window.py</c> 的 <c>_detect_level</c> / <c>_is_continuation</c>，
/// 外加 <c>experiments/MC_LuncherTEST-4.0/core/config.py</c> 里那份 <c>LOG_NOISE_PATTERNS</c>
/// （见 <see cref="NoisePatterns"/> 的说明：**1.0.0 正式版其实没有这个功能**）。
///
/// ## 为什么要按"几种写法"匹配，而不是简单找子串
/// 1.0.0 的注释写得很清楚：直接找 `"ERROR"` 子串的话，`no errors found`
/// 这种正常输出也会被算成错误行。所以只认这四种**带定界符**的写法：
///     <c>[12:00:00] [Client thread/INFO]: ...</c>   ← 现代 MC 控制台（log4j2）
///     <c>... /INFO] ...</c>                          ← 1.12 之前那种
///     <c>level="INFO"</c>                            ← log4j2 原始 XML（未解析时）
///     <c>INFO: ...</c>                               ← 老式 log4j 纯文本
/// </summary>
public static class LogLine
{
    /// <summary>
    /// 级别枚举 → 日志里出现的**字面量**（`FATAL` 这种大写）。
    /// ⚠️ 这是"日志里的写法"，不是界面文案 —— 界面文案走 <c>Localizer.Tr("致命")</c> 那一套，
    ///    两者别混（1.0.0 专门为这件事写过注释）。
    /// </summary>
    public static string Name(LogLevel level) => level switch
    {
        LogLevel.Fatal => "FATAL",
        LogLevel.Error => "ERROR",
        LogLevel.Warn => "WARN",
        LogLevel.Info => "INFO",
        LogLevel.Debug => "DEBUG",
        _ => string.Empty,
    };

    /// <summary>
    /// 按严重程度从高到低的所有级别 —— 日志窗口那排计数按钮就是照它画的。
    /// （不含 <see cref="LogLevel.None"/>：它没有开关。）
    /// </summary>
    public static IReadOnlyList<LogLevel> Filterable { get; } = new[]
    {
        LogLevel.Fatal, LogLevel.Error, LogLevel.Warn, LogLevel.Info, LogLevel.Debug,
    };

    /// <summary>
    /// 续行的开头。Java 的堆栈是 `\tat com.foo…`、log4j 常有 `Caused by:` /
    /// `Suppressed:` / `...`，C# 的是 `   at Foo()`。
    /// </summary>
    private static readonly string[] ContinuationPrefixes =
    {
        "at ", "Caused by:", "Suppressed:", "...",
    };

    /// <summary>
    /// 这条日志是哪一级的（认不出来返回 <see cref="LogLevel.None"/>）。
    ///
    /// ⚠️ 匹配顺序是 FATAL → ERROR → WARN → INFO → DEBUG，**先匹配到的赢**。
    ///    这不是随便定的：一行里同时出现两个级别词时（比如 `[WARN]: ... ERROR ...`），
    ///    1.0.0 那边也是按这个顺序取第一个命中的，所以 `[WARN]` 会被认成 WARN ——
    ///    要改顺序得连着那边一起改，不然两边显示的级别就不一样了。
    /// </summary>
    public static LogLevel DetectLevel(string? line)
    {
        if (string.IsNullOrEmpty(line)) return LogLevel.None;

        foreach (var level in Filterable)
        {
            var name = Name(level);
            if (line.Contains($"/{name}]", StringComparison.Ordinal)
                || line.Contains($"level=\"{name}\"", StringComparison.Ordinal)
                || line.Contains($"{name}:", StringComparison.Ordinal)
                || line.Contains($"[{name}]", StringComparison.Ordinal))
            {
                return level;
            }
        }
        return LogLevel.None;
    }

    /// <summary>
    /// 这一行是不是**上一条的续行**（堆栈），而不是一条新日志。
    ///
    /// 两种判据（1.0.0 同款）：
    ///   · 以空白开头（Java 的堆栈 `\tat …`、C# 的 `   at …`）——
    ///     但**纯空白行不算**，否则空行会把上一条的级别一路继承下去；
    ///   · 以 <see cref="ContinuationPrefixes"/> 里的词开头。
    ///
    /// 启动器自己加的行（`[启动器] …`）不缩进、也不以堆栈前缀开头，
    /// 所以不会被算成续行 —— 藏了 INFO 也照样能看到「游戏已退出」这类消息。
    /// </summary>
    public static bool IsContinuation(string? line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        if (line[0] is ' ' or '\t') return line.Trim().Length > 0;
        return ContinuationPrefixes.Any(p => line.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>
    /// 启动器自己的消息前缀（1.0.0 <c>ui/tasks.py</c> 的 <c>LOG_PREFIX</c>）。
    /// 带这个前缀的行会同时显示在状态行上，而且**永远不会被噪音规则吃掉**。
    /// </summary>
    public const string LauncherPrefix = "[启动器] ";

    /// <summary>
    /// 游戏日志里的噪音片段。
    ///
    /// ## 这份清单的出处（先说清楚，免得以后被当成"抄错了"）
    /// 用户提到的是 1.0.0 <c>core/config.py</c> 里的 <c>LOG_NOISE_PATTERNS</c>。
    /// 实测那份代码**只存在于 <c>experiments/MC_LuncherTEST-4.0/core/config.py</c>**
    /// （以及更早的 <c>MC_LuncherTEST-3.0.py</c>），**1.0.0 正式版里没有**：
    ///   · <c>D:\开发工程\DHML-src\core\config.py</c> 里搜不到 <c>LOG_NOISE</c>；
    ///   · 1.0.0 的 <c>ui/dialogs/log_window.py</c> 也没有任何过滤逻辑。
    /// 所以这一份是**照那个实验版抄的**（用户指的就是它），并且做成了可关的
    /// （见 <c>AppSettings.IsLogNoise</c> 和窗口工具栏上的「过滤噪音」开关）——
    /// 1.0.0 那边根本没有这个开关，属于**新增能力**，不是"照抄"。
    ///
    /// ## 为什么只留了三条
    /// 实验版那份清单里还有 <c>"Time elapsed"</c> 之类；这里只保留**确实会刷屏、
    /// 且没有任何信息量**的三条。宁可少滤几条（用户还能自己关），
    /// 也不要滤掉一条有用的 —— 发问题报告时缺了关键行是查不出来的。
    /// </summary>
    public static IReadOnlyList<string> NoisePatterns { get; } = new[]
    {
        "Saving chunks for level",      // 自动保存，几秒一条
        "Time elapsed:",                // 每条日志后面跟的耗时统计
        "Loaded 1 advancements",        // 进世界时的一次性统计
    };

    /// <summary>这一行是不是噪音（带 <see cref="LauncherPrefix"/> 的行**永远不算**）。</summary>
    public static bool IsNoise(string? line) => MatchesNoise(line, NoisePatterns);

    /// <summary>按给定清单判噪音（清单可配置时用这个重载）。</summary>
    public static bool MatchesNoise(string? line, IEnumerable<string> patterns)
    {
        if (string.IsNullOrEmpty(line)) return false;
        // 启动器自己的话绝不滤 —— 那是用户最需要看的（"游戏已退出"这类）
        if (line.StartsWith(LauncherPrefix, StringComparison.Ordinal)) return false;
        return patterns.Any(p => p.Length > 0 && line.Contains(p, StringComparison.Ordinal));
    }
}
