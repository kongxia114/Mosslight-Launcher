using System;
using System.Diagnostics;
using System.Windows;

namespace Mosslight.App.Views;

/// <summary>开发版（Debug）提示窗口。</summary>
public partial class DebugBuildNoticeDialog : Window
{
    // 「打开最新版下载页」跳哪儿：仓库的 releases 页。
    private static string ReleasesUrl => Core.AppInfo.RepoUrl + "/releases";

    public DebugBuildNoticeDialog()
        : this(null)
    {
    }

    public DebugBuildNoticeDialog(string? customBody)
    {
        InitializeComponent();
        Infrastructure.FramelessWindow.Apply(this);

        // 把实际跑的那个输出目录亮出来：一眼能看出是 bin\Debug 还是发布目录
        PathText.Text = AppContext.BaseDirectory.TrimEnd('\\');

        if (!string.IsNullOrWhiteSpace(customBody))
        {
            DefaultBody.Visibility = Visibility.Collapsed;
            CustomBody.Text = customBody;
            CustomBody.Visibility = Visibility.Visible;
        }
    }

    /// <summary>「我清楚我在做什么」：关掉提示，继续用（开发版该有的样子）。</summary>
    private void OnContinueClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>「打开最新版下载页并退出」：开浏览器 → 关启动器。</summary>
    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        OpenReleasesPage(ReleasesUrl);
        Application.Current.Shutdown();
    }

    private static void OpenReleasesPage(string url)
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
            // 打不开浏览器（没有默认浏览器 / 被策略挡住）不该崩在这儿
            App.WriteLauncherLog($"打开链接失败：{url} ({ex.Message})");
        }
    }
}
