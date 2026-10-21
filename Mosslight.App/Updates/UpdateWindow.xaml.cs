using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Mosslight.App.Infrastructure;
using Mosslight.Core;
using Mosslight.Core.Updates;

namespace Mosslight.App.Updates;

/// <summary>
/// 更新相关弹窗，两种形态共用一套外观：
///   · <b>发现新版本</b>：显示更新日志，「稍后」/「立即更新」；强制更新时没有「稍后」，
///     关窗直接退出（`Environment.Exit(0)`）。
///   · <b>更新成功</b>：刚替换完的第一次启动，只有「确定」；确认后才把 LastRunVersion 写掉。
///
/// 固定文案来自 `Assets\update\lang.json`（<see cref="UpdateLang"/>）；更新日志正文是
/// Release 里的原文（中文），不翻译。
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateManager? _manager;
    private readonly UpdateInfo? _info;
    private readonly Action? _onSuccessConfirmed;
    private readonly bool _successMode;

    private bool _installStarted;

    /// <summary>「发现新版本」。</summary>
    public UpdateWindow(UpdateManager manager, UpdateInfo info)
    {
        InitializeComponent();
        FramelessWindow.Apply(this);

        _manager = manager;
        _info = info;
        _successMode = false;

        TitleText.Text = UpdateLang.Get("title");
        VersionText.Text = UpdateLang.Get("version",
            ("current", AppInfo.AppVersion), ("latest", info.Tag));
        NotesLabel.Text = UpdateLang.Get("notes");
        LaterButton.Content = UpdateLang.Get("later");
        InstallButton.Content = UpdateLang.Get("install");
        ShowNotes(NotesText(info.Notes));

        if (info.IsForced)
        {
            // 强制更新：不给「稍后」，关窗就是退出（见 OnClosing）
            LaterButton.Visibility = Visibility.Collapsed;
            StatusText.Text = UpdateLang.Get("forced_hint");
        }
        else
        {
            StatusText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>「更新成功」：只有一个按钮，点完才算"这次更新结束"。</summary>
    public UpdateWindow(string version, string notes, Action onConfirmed)
    {
        InitializeComponent();
        FramelessWindow.Apply(this);

        _successMode = true;
        _onSuccessConfirmed = onConfirmed;

        TitleText.Text = UpdateLang.Get("success_title");
        VersionText.Text = UpdateLang.Get("success_version", ("version", version));
        NotesLabel.Text = UpdateLang.Get("notes");
        ShowNotes(NotesText(notes));
        StatusText.Visibility = Visibility.Collapsed;

        // 只有一个「确定」：成功提示没有"稍后"这个选项
        LaterButton.Visibility = Visibility.Collapsed;
        InstallButton.Content = UpdateLang.Get("ok");
    }

    private static string NotesText(string notes)
        => string.IsNullOrWhiteSpace(notes) ? "（这一版没有写更新说明）" : notes.Trim();

    /// <summary>把更新日志按 Markdown 摊进富文本框（加粗 / 列表 / 代码都是真样式）。</summary>
    private void ShowNotes(string markdown)
    {
        var foreground = (Brush)FindResource("TextBrush");
        BodyViewer.Document = MarkdownRenderer.Build(markdown, foreground, FontFamily, 12);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 强制更新：用户直接把窗口关掉 → 退出启动器（点「立即更新」走过的不算）
        if (!_successMode && _info is { IsForced: true } && !_installStarted)
        {
            Environment.Exit(0);
        }
    }

    private void OnLaterClick(object sender, RoutedEventArgs e) => Close();

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (_successMode)
        {
            _onSuccessConfirmed?.Invoke();      // 写 LastRunVersion
            Close();
            return;
        }
        if (_manager is null || _info is null)
        {
            Close();
            return;
        }

        InstallButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Text = UpdateLang.Get("installing");
        try
        {
            var download = await _manager.DownloadAndVerifyAsync(_info);
            App.WriteLauncherLog(download.Message);
            if (!download.Ok)
            {
                StatusText.Text = UpdateLang.Get("failed") + "：" + download.Message;
                InstallButton.IsEnabled = true;
                LaterButton.IsEnabled = true;
                return;
            }

            if (!_manager.TryStartApply(download.ZipPath, out var message))
            {
                StatusText.Text = UpdateLang.Get("failed") + "：" + message;
                InstallButton.IsEnabled = true;
                LaterButton.IsEnabled = true;
                return;
            }

            _installStarted = true;
            StatusText.Text = message;
            await Task.Delay(700);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            App.WriteLauncherLog($"更新失败：{ex.Message}");
            StatusText.Visibility = Visibility.Visible;
            StatusText.Text = UpdateLang.Get("failed") + "：" + ex.Message;
            InstallButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
        }
    }
}
