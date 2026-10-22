using System.Diagnostics;

namespace Mosslight.App.Infrastructure;

/// <summary>
/// 用系统默认浏览器打开一个链接。
///
/// ⚠️ `Process.Start(UseShellExecute = true)` 而不是 WPF 的 NavigationService：
///    这些页面没有 NavigationWindow，用 NavigationService 会抛异常。
/// ⚠️ 打不开（没有默认浏览器 / 被组策略挡住）**不该崩窗口** —— 只记一行日志。
///    关于对话框、更多页、以后任何"点一下去浏览器"的入口都走这里，别各写一份。
/// </summary>
public static class ShellOpen
{
    public static void Url(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            App.WriteLauncherLog($"打开链接失败：{url} ({ex.Message})");
        }
    }
}
