using System;
using System.ComponentModel;
using System.Windows;
using Mosslight.App.Infrastructure;
using Mosslight.Core;
using Mosslight.Core.Updates;

namespace Mosslight.App.Updates;

/// <summary>
/// 「发现新版本」弹窗。固定文案来自 `Assets\update\lang.json`（<see cref="UpdateLang"/>），
/// 更新日志正文是 Release 里的原文（中文），不翻译。
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateManager _manager;
    private readonly UpdateInfo _info;
    private bool _installStarted;

    public UpdateWindow(UpdateManager manager, UpdateInfo info)
    {
        InitializeComponent();
        FramelessWindow.Apply(this);

        _manager = manager;
        _info = info;
        ApplyTexts();

        if (_info.IsForced)
        {
            // 强制更新：不给"稍后"，关窗就是退出（见 OnClosing）
            LaterButton.Visibility = Visibility.Collapsed;
            StatusText.Text = UpdateLang.Get("forced_hint");
        }
    }

    private void ApplyTexts()
    {
        TitleText.Text = UpdateLang.Get("title");
        VersionText.Text = UpdateLang.Get("version",
            ("current", AppInfo.AppVersion), ("latest", _info.Tag));
        NotesLabel.Text = UpdateLang.Get("notes");
        LaterButton.Content = UpdateLang.Get("later");
        InstallButton.Content = UpdateLang.Get("install");
        BodyText.Text = string.IsNullOrWhiteSpace(_info.Notes)
            ? "（这一版没有写更新说明）"
            : _info.Notes.Trim();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_info.IsForced && !_installStarted)
        {
            // 强制更新：用户直接把窗口关掉 → 退出启动器
            Environment.Exit(0);
        }
    }

    private void OnLaterClick(object sender, RoutedEventArgs e) => Close();

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
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

            // 记下"要装成哪个版本"：脚本替换完，下次启动就能认出"刚更新过"
            RegistryStore.WriteString("Update", "InstalledVersion", _info.Version.ToString());

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
            StatusText.Text = UpdateLang.Get("failed") + "：" + ex.Message;
            InstallButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
        }
    }
}
