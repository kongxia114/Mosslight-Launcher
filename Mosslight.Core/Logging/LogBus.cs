namespace Mosslight.Core.Logging;

/// <summary>
/// 进程级的**日志总线**：谁都能往里记，日志窗口订阅它显示。
///
/// ## 为什么是静态的，而不是塞进 <c>LauncherServices</c>
/// 1.0.0 那边是"首页持有 LogWindow，`append_log()` 直接往它写"——简单，
/// 但结果是**只有首页会记日志**：下载页、版本页、版本设置页发生的事一行都进不去。
///
/// 2.0 这边反过来：日志是**进程级**的（谁都能记），窗口只是它的一个订阅者。
/// 这么定有三个直接好处：
///   · 接游戏输出不用改 <c>ILaunchService</c> 的签名（那会动到已有接口）；
///   · 窗口**后开也有历史** —— 游戏跑了 30 秒才点开日志窗口，前面 30 秒的内容
///     还在（1.0.0 因为窗口一直在后台活着，效果是一样的）；
///   · 关掉窗口再打开，历史还在（<see cref="Entries"/> 是唯一的真相，
///     窗口只是把它渲染出来）。
///
/// ## 线程
/// 游戏输出是从 <c>Process.OutputDataReceived</c> 的回调里来的 —— **不是 UI 线程**。
/// 所以这里只负责"存下来 + 喊一声"，**绝不在 <see cref="Appended"/> 里碰控件**；
/// 订阅方（日志窗口）自己 <c>Dispatcher.Invoke</c>。理由：这个类住在 Core 里，
/// Core 不许 using System.Windows（见 Mosslight.Core.csproj 的注释），
/// 它没法帮任何人回到 UI 线程。
///
/// ⚠️ <see cref="Appended"/> 是在**调用方的线程**上触发的。订阅者要么自己切线程，
///    要么保证处理器是线程安全的。
/// </summary>
public static class LogBus
{
    /// <summary>最多留多少条（超了从最老的丢）。20000 ≈ 1.0.0 日志窗口那个上限档。</summary>
    public const int Capacity = 20000;

    private static readonly object Gate = new();
    private static readonly List<LogEntry> Buffer = new(Capacity);

    /// <summary>上一条**自带级别标记**的行的级别 —— 续行（堆栈）靠它继承级别。</summary>
    private static LogLevel _lastLevel = LogLevel.None;

    /// <summary>新来一条。</summary>
    /// <remarks>
    /// ⚠️ 在**触发事件的线程**上调用（游戏输出那条路是后台线程）。
    /// </remarks>
    public static event Action<LogEntry>? Appended;

    /// <summary>被清空过（窗口据此清屏）。</summary>
    public static event Action? Cleared;

    /// <summary>当前留着的全部条目（老 → 新）。窗口开的时候先整批读它。</summary>
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

    /// <summary>
    /// 记一行。<paramref name="level"/> 传 null 表示"自己判"（走
    /// <see cref="LogLine.DetectLevel"/>）；启动器自己的消息请带
    /// <see cref="LogLine.LauncherPrefix"/> 前缀，那样噪音过滤不会吃掉它。
    /// </summary>
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

    /// <summary>
    /// 导出成纯文本（全部条目，**不管窗口上藏了哪几级**）。
    ///
    /// 1.0.0 的注释值得抄一遍：导出的是**全部**日志，不管当前藏了哪几级 ——
    /// 发问题报告时漏掉被藏起来的 DEBUG，往往就查不出来了。
    /// </summary>
    public static string Export(IEnumerable<LogEntry>? entries = null)
    {
        var list = entries?.ToList() ?? Entries.ToList();
        if (list.Count == 0) return string.Empty;
        return string.Join("\n", list.Select(e => e.Text)) + "\n";
    }
}
