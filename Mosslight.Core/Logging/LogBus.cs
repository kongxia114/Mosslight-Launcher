namespace Mosslight.Core.Logging;

/// <summary>
/// 进程级的**日志总线**：谁都能往里记，日志窗口订阅它显示；⚠️ Appended 是在**调用方的线程**上触发
/// 的。
/// </summary>
public static class LogBus
{
    /// <summary>最多留多少条（超了从最老的丢）。</summary>
    public const int Capacity = 20000;

    private static readonly object Gate = new();
    private static readonly List<LogEntry> Buffer = new(Capacity);

    // 上一条**自带级别标记**的行的级别 —— 续行（堆栈）靠它继承级别。
    private static LogLevel _lastLevel = LogLevel.None;

    public static event Action<LogEntry>? Appended;

    /// <summary>被清空过（窗口据此清屏）。</summary>
    public static event Action? Cleared;

    /// <summary>当前留着的全部条目（老 → 新）。</summary>
    public static IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (Gate)
            {
                return Buffer.ToArray();
            }
        }
    }

    public static LogEntry Write(string? text, LogLevel? level = null)
    {
        var raw = text ?? string.Empty;
        var continuation = false;
        LogLevel owner;

        var detected = level ?? LogLine.DetectLevel(raw);
        if (detected != LogLevel.None)
        {
            owner = detected;
        }
        else if (LogLine.IsContinuation(raw))
        {
            // 堆栈续行跟着上一条走 —— 但**只跟上一条自带级别的**，
            // 不然启动器自己的一句说明后面跟个缩进，也会被算成 INFO
            owner = _lastLevel;
            continuation = owner != LogLevel.None;
        }
        else
        {
            owner = LogLevel.None;
        }

        var entry = new LogEntry(
            raw,
            owner,
            continuation,
            raw.StartsWith(LogLine.LauncherPrefix, StringComparison.Ordinal),
            DateTimeOffset.Now);

        lock (Gate)
        {
            // 只有**自带级别**的行才更新"上一条的级别" —— 续行不该把级别接着往下传，
            // 否则一段 20 行的堆栈之后，缩进的说明文字也会被算成 ERROR
            if (detected != LogLevel.None) _lastLevel = detected;
            else if (owner == LogLevel.None) _lastLevel = LogLevel.None;

            Buffer.Add(entry);
            if (Buffer.Count > Capacity) Buffer.RemoveRange(0, Buffer.Count - Capacity);
        }

        Appended?.Invoke(entry);
        return entry;
    }

    /// <summary>记一行**启动器自己的**消息（自动补前缀）。</summary>
    public static LogEntry WriteLauncher(string text) => Write(LogLine.LauncherPrefix + text);

    /// <summary>整批记（一次触发事件，几百行时比逐条快得多）。</summary>
    public static void WriteRange(IEnumerable<string> lines, LogLevel? level = null)
    {
        foreach (var line in lines) Write(line, level);
    }

    /// <summary>清空（窗口上的「清空」按钮走这里，这样多个窗口会一起清）。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Buffer.Clear();
            _lastLevel = LogLevel.None;
        }
        Cleared?.Invoke();
    }

    /// <summary>导出成纯文本（全部条目，**不管窗口上藏了哪几级**）。</summary>
    public static string Export(IEnumerable<LogEntry>? entries = null)
    {
        var list = entries?.ToList() ?? Entries.ToList();
        if (list.Count == 0) return string.Empty;
        return string.Join("\n", list.Select(e => e.Text)) + "\n";
    }
}
