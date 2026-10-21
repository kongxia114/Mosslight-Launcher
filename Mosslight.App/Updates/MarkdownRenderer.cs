using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace Mosslight.App.Updates;

/// <summary>
/// 极简 Markdown → <see cref="FlowDocument"/>。
///
/// 为什么不引 Markdig：这个工程**一个 NuGet 包都不引**（CI 的注释写着），
/// 发布包又只有一个 exe —— 为了几行更新日志引一个第三方库不划算。
/// 这里只认 GitHub Release 里真正会出现的那几种语法：
///   `## 标题`、`- 列表`、`1. 列表`、`**加粗**`、`` `代码` ``、`[文字](链接)`、空行分段。
/// 认不出来的写法**原样显示**（宁可不好看，也不要吞内容）。
/// </summary>
internal static class MarkdownRenderer
{
    private static readonly Regex Heading = new(@"^(?<hash>#{1,4})\s+(?<text>.+)$", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^\s*[-*+]\s+(?<text>.+)$", RegexOptions.Compiled);
    private static readonly Regex Ordered = new(@"^\s*(?<num>\d+)[.)]\s+(?<text>.+)$", RegexOptions.Compiled);
    private static readonly Regex Inline = new(
        @"(\*\*(?<bold>.+?)\*\*)|(`(?<code>[^`]+)`)|(\[(?<link>[^\]]+)\]\((?<url>[^)]+)\))",
        RegexOptions.Compiled);

    public static FlowDocument Build(string markdown, Brush foreground, FontFamily font, double fontSize)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity,     // 不分栏
            FontFamily = font,
            FontSize = fontSize,
            Foreground = foreground,
            LineHeight = fontSize * 1.55,
        };

        var codeBackground = Application.Current?.TryFindResource("BgControlBrush") as Brush ?? Brushes.Transparent;
        var accent = Application.Current?.TryFindResource("AccentTextBrush") as Brush ?? foreground;

        List? list = null;            // 正在填的那个列表
        Paragraph? paragraph = null;  // 正在攒的段落

        void FlushParagraph()
        {
            if (paragraph is not null && paragraph.Inlines.Count == 0)
            {
                doc.Blocks.Remove(paragraph);
            }
            paragraph = null;
        }

        void FlushList()
        {
            list = null;
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0)
            {
                FlushParagraph();
                FlushList();
                continue;
            }

            var heading = Heading.Match(line);
            if (heading.Success)
            {
                FlushParagraph();
                FlushList();
                var level = heading.Groups["hash"].Length;
                var title = new Paragraph
                {
                    FontSize = fontSize + Math.Max(1, 5 - level),
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, level == 1 ? 2 : 10, 0, 4),
                };
                AddInlines(title, heading.Groups["text"].Value, codeBackground, accent);
                doc.Blocks.Add(title);
                continue;
            }

            var bullet = Bullet.Match(line);
            if (bullet.Success)
            {
                FlushParagraph();
                list ??= new List
                {
                    MarkerStyle = TextMarkerStyle.Disc,
                    Margin = new Thickness(18, 2, 0, 2),
                    Padding = new Thickness(0),
                };
                if (!doc.Blocks.Contains(list))
                {
                    doc.Blocks.Add(list);
                }
                var item = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                AddInlines(item, bullet.Groups["text"].Value, codeBackground, accent);
                list.ListItems.Add(new ListItem(item));
                continue;
            }

            var ordered = Ordered.Match(line);
            if (ordered.Success)
            {
                FlushParagraph();
                if (list is null || list.MarkerStyle != TextMarkerStyle.Decimal)
                {
                    list = new List
                    {
                        MarkerStyle = TextMarkerStyle.Decimal,
                        StartIndex = int.TryParse(ordered.Groups["num"].Value, out var start) ? start : 1,
                        Margin = new Thickness(18, 2, 0, 2),
                        Padding = new Thickness(0),
                    };
                    doc.Blocks.Add(list);
                }
                var item = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                AddInlines(item, ordered.Groups["text"].Value, codeBackground, accent);
                list.ListItems.Add(new ListItem(item));
                continue;
            }

            // 普通文字：连着几行算一段（软换行拼成空格）
            FlushList();
            if (paragraph is null)
            {
                paragraph = new Paragraph { Margin = new Thickness(0, 2, 0, 6) };
                doc.Blocks.Add(paragraph);
            }
            else
            {
                paragraph.Inlines.Add(new Run(" "));
            }
            AddInlines(paragraph, line.Trim(), codeBackground, accent);
        }

        FlushParagraph();
        return doc;
    }

    /// <summary>把一行里的 `**加粗**` / `` `代码` `` / `[文字](链接)` 变成真正的 Run。</summary>
    private static void AddInlines(Paragraph paragraph, string text, Brush codeBackground, Brush accent)
    {
        var position = 0;
        foreach (Match match in Inline.Matches(text))
        {
            if (match.Index > position)
            {
                paragraph.Inlines.Add(new Run(text[position..match.Index]));
            }

            if (match.Groups["bold"].Success)
            {
                paragraph.Inlines.Add(new Run(match.Groups["bold"].Value) { FontWeight = FontWeights.Bold });
            }
            else if (match.Groups["code"].Success)
            {
                paragraph.Inlines.Add(new Run(match.Groups["code"].Value)
                {
                    FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
                    Background = codeBackground,
                });
            }
            else
            {
                var link = new Hyperlink(new Run(match.Groups["link"].Value))
                {
                    Foreground = accent,
                    ToolTip = match.Groups["url"].Value,
                };
                var url = match.Groups["url"].Value;
                link.RequestNavigate += (_, e) =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                                   or InvalidOperationException)
                    {
                        App.WriteLauncherLog($"打开链接失败：{url} ({ex.Message})");
                    }
                    e.Handled = true;
                };
                paragraph.Inlines.Add(link);
            }

            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            paragraph.Inlines.Add(new Run(text[position..]));
        }
    }
}
