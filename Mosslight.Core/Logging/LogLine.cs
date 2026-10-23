namespace Mosslight.Core.Logging;

/// <summary>
/// 一条日志的级别；⚠️ 顺序**就是严重程度从高到低**，也**就是日志窗口里那排计数按钮的顺序**。
/// </summary>
public enum LogLevel
{
    /// <summary>FATAL —— 致命。</summary>
    Fatal,

    /// <summary>ERROR —— 错误。</summary>
    Error,

    /// <summary>WARN —— 警告。</summary>
    Warn,

    /// <summary>INFO —— 信息。</summary>
    Info,

    /// <summary>DEBUG —— 调试。</summary>
    Debug,

    /// <summary>
    /// 认不出级别的行（启动器自己的消息、游戏的裸输出等），**永远显示、没有开关可点**。
    /// </summary>
    None,
}

/// <summary>一行日志的**归属**：谁发的、哪一级、什么内容。</summary>
public sealed record LogEntry(
    string Text,
    LogLevel Level,
    bool IsContinuation,
    bool IsLauncher,
    DateTimeOffset At)
{
    /// <summary>
    /// 这一行现在**归哪一级管**（续行归上一条；LogLevel.None = 不属于任何一级）。
    /// </summary>
    public LogLevel Owner => Level;
}

/// <summary>日志文本 → 级别 / 续行的判定，外加噪音过滤。</summary>
public static class LogLine
{
    /// <summary>级别枚举 → 日志里出现的**字面量**（`FATAL` 这种大写）。</summary>
    public static string Name(LogLevel level) => level switch
    {
        LogLevel.Fatal => "FATAL",
        LogLevel.Error => "ERROR",
        LogLevel.Warn => "WARN",
        LogLevel.Info => "INFO",
        LogLevel.Debug => "DEBUG",
        _ => string.Empty,
    };

    /// <summary>按严重程度从高到低的所有级别 —— 日志窗口那排计数按钮就是照它画的。</summary>
    public static IReadOnlyList<LogLevel> Filterable { get; } = new[]
    {
        LogLevel.Fatal, LogLevel.Error, LogLevel.Warn, LogLevel.Info, LogLevel.Debug,
    };

    private static readonly string[] ContinuationPrefixes =
    {
        "at ", "Caused by:", "Suppressed:", "...",
    };

    /// <summary>
    /// 这条日志是哪一级的（认不出来返回 LogLevel.None）；⚠️ 匹配顺序是 FATAL → ERROR → WARN →
    /// INFO → DEBUG，**先匹配到的赢**。
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

    /// <summary>这一行是不是**上一条的续行**（堆栈），而不是一条新日志。</summary>
    public static bool IsContinuation(string? line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        if (line[0] is ' ' or '\t') return line.Trim().Length > 0;
        return ContinuationPrefixes.Any(p => line.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>启动器自己的消息前缀。</summary>
    public const string LauncherPrefix = "[启动器] ";

    public static IReadOnlyList<string> NoisePatterns { get; } = new[]
    {
        "Saving chunks for level",      // 自动保存，几秒一条
        "Time elapsed:",                // 每条日志后面跟的耗时统计
        "Loaded 1 advancements",        // 进世界时的一次性统计
    };

    /// <summary>这一行是不是噪音（带 LauncherPrefix 的行**永远不算**）。</summary>
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
