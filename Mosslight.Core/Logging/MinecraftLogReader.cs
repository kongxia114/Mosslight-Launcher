using System.Diagnostics;
using System.Xml;

namespace Mosslight.Core.Logging;

/// <summary>把游戏控制台的输出还原成人看的一行行（兼容 log4j XML 和旧式纯文本）。</summary>
public sealed class MinecraftLogReader
{
    // legacy XML 布局输出的 <log4j:Event> **不带 xmlns 声明**，得自己补。
    private const string Log4jNamespace = "http://logging.apache.org/log4j/";

    private const string EventOpen = "<log4j:Event";
    private const string EventClose = "</log4j:Event>";

    private readonly List<string> _pending = new();
    private bool _inEvent;

    /// <summary>
    /// 喂一行原始输出，吐出**零到多行**可读文本；⚠️ 必须做成有状态的：一条日志事件在控制台里是
    /// **3 行**（带堆栈时更多），得攒齐了才能还原 —— 所以这个类不是线程安全的，一个进程一个实例。
    /// </summary>
    public IReadOnlyList<string> Feed(string? line)
    {
        if (line is null) return Array.Empty<string>();

        if (!_inEvent)
        {
            if (line.Contains(EventOpen, StringComparison.Ordinal))
            {
                _pending.Clear();
                _pending.Add(line);
                _inEvent = true;
                // 理论上不会一行就是一条完整事件，但真碰上别卡住
                if (line.Contains(EventClose, StringComparison.Ordinal)) return Finish();
                return Array.Empty<string>();
            }

            // XML 声明 / DOCTYPE 是布局自己加的壳，不是日志内容
            if (line.StartsWith("<?xml", StringComparison.Ordinal)
                || line.StartsWith("<!DOCTYPE", StringComparison.Ordinal))
            {
                return Array.Empty<string>();
            }

            // 老版本：控制台本来就是可读文本
            return new[] { line };
        }
        _pending.Add(line);
        return line.Contains(EventClose, StringComparison.Ordinal)
            ? Finish()
            : Array.Empty<string>();
    }

    /// <summary>流断了（进程退出）。</summary>
    public IReadOnlyList<string> Flush()
    {
        if (_pending.Count == 0) return Array.Empty<string>();
        var leftover = _pending.ToArray();
        _pending.Clear();
        _inEvent = false;
        return leftover;
    }

    private IReadOnlyList<string> Finish()
    {
        var fragment = string.Join("\n", _pending);
        _pending.Clear();
        _inEvent = false;
        return Render(fragment);
    }

    /// <summary>一个完整的 &lt;log4j:Event&gt; → 可读的若干行。</summary>
    public static IReadOnlyList<string> Render(string fragment)
    {
        // ⚠️ 先确认这真是 log4j 事件。不确认的话，拿一段普通文本进来会被
        // XmlReader 的 Fragment 模式当成"根级文本节点"**解析成功**，
        // 结果是吐出 `[--:--:--]: this is not xml at all` 这种假日志 ——
        // 内容还在，但多了一层看不懂的壳（自检里那条"原样返回"就是抓这个的）。
        if (!fragment.Contains(EventOpen, StringComparison.Ordinal))
        {
            return fragment.Replace("\r\n", "\n").Split('\n');
        }

        if (!fragment.Contains("xmlns:log4j", StringComparison.Ordinal))
        {
            var at = fragment.IndexOf(EventOpen, StringComparison.Ordinal);
            if (at >= 0)
            {
                fragment = fragment[..(at + EventOpen.Length)]
                           + $" xmlns:log4j=\"{Log4jNamespace}\""
                           + fragment[(at + EventOpen.Length)..];
            }
        }

        var settings = new XmlReaderSettings
        {
            // ⚠️ 游戏输出是外部输入，必须显式禁掉 DTD / 外部实体解析（.NET 默认值会变，不能依赖默认值）。
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = false,
            ConformanceLevel = ConformanceLevel.Fragment,
        };

        try
        {
            using var reader = XmlReader.Create(new StringReader(fragment), settings);
            string? level = null, thread = null, when = null, message = null, throwable = null;
            var inMessage = false;
            var inThrowable = false;

            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        switch (reader.LocalName)
                        {
                            case "Event":
                                level = reader.GetAttribute("level");
                                thread = reader.GetAttribute("thread");
                                when = FormatTime(reader.GetAttribute("timestamp"));
                                break;
                            case "Message":
                                inMessage = true;
                                message = reader.IsEmptyElement ? string.Empty : null;
                                break;
                            case "Throwable":
                                inThrowable = true;
                                throwable = reader.IsEmptyElement ? string.Empty : null;
                                break;
                        }
                        break;

                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                        // ⚠️ 一条 Message 里可能有多个文本/CDATA 段，**要接起来**；
                        // 直接赋值的话只留最后一段（这个坑在 Python 那边是用
                        // ElementTree 的 .text 避开的，它天然只给一个字符串）
                        if (inMessage) message = (message ?? string.Empty) + reader.Value;
                        else if (inThrowable) throwable = (throwable ?? string.Empty) + reader.Value;
                        break;

                    case XmlNodeType.EndElement:
                        if (reader.LocalName == "Message") inMessage = false;
                        if (reader.LocalName == "Throwable") inThrowable = false;
                        break;
                }
            }

            var prefix = $"[{when ?? "--:--:--"}]";
            if (!string.IsNullOrEmpty(thread)) prefix += $" [{thread}/{level}]";
            else if (!string.IsNullOrEmpty(level)) prefix += $" [{level}]";

            var head = string.IsNullOrEmpty(message) ? $"{prefix}:" : $"{prefix}: {message}";
            var lines = new List<string> { head };

            // 每条堆栈行都缩进一个 tab，见类注释
            if (!string.IsNullOrEmpty(throwable))
            {
                foreach (var text in throwable.Replace("\r\n", "\n").Split('\n'))
                {
                    if (text.Trim().Length > 0) lines.Add("\t" + text);
                }
            }
            return lines;
        }
        catch (XmlException)
        {
            // 认不出来就原样放出去 —— 显示得难看，总比把内容吞掉强
            return fragment.Replace("\r\n", "\n").Split('\n');
        }
    }

    /// <summary>epoch 毫秒 → HH:MM:SS（本地时间）。</summary>
    public static string FormatTime(string? raw)
    {
        if (!long.TryParse(raw, out var ms)) return "--:--:--";
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("HH:mm:ss");
        }
        catch (ArgumentOutOfRangeException)
        {
            // 时间戳离谱（0 或者超出范围）时别抛 —— 那只是一行日志
            return "--:--:--";
        }
    }
}
