using System.Windows;
using System.Windows.Controls;
using Mosslight.Core.Localization;

namespace Mosslight.App.Infrastructure;

/// <summary>
/// 「检查更新」这一套流程（查 → 有新版就开更新窗）；⚠️ 点了按钮 = 同意更新：下载完直接重启进新版
/// 本，不再弹第二个确认框 （这个取舍见 SettingsPage 里原来的注释）。
/// </summary>
public static class UpdateCheck
{
    public static async Task RunAsync(Window? owner, Button? button = null)
    {
        var window = MainWindow.Current;
        if (App.IsDevelopmentBuild)
        {
            window?.ShowToast(Localizer.Tr("开发版不自动更新"), ToastKind.Warning);
            return;
        }

        if (button is not null)
        {
            button.IsEnabled = false;
        }
        try
        {
            var manager = new Core.Updates.UpdateManager(App.BaseDir, null, App.WriteLauncherLog);
            var check = await manager.CheckAsync();
            App.WriteLauncherLog(check.Message);
            if (!check.Available || check.Info is not { } info)
            {
                window?.ShowToast(check.Message, ToastKind.Info);
                return;
            }

            // 有新版就交给弹窗：下载 / 校验 / 替换都在它里面（强制更新也走同一个窗）
            new Updates.UpdateWindow(manager, info) { Owner = owner ?? window }.ShowDialog();
        }
        catch (Exception ex)
        {
            App.WriteLauncherLog($"检查更新出错：{ex.Message}");
            window?.ShowToast(Localizer.Tr("检查更新出错：{err}", ("err", ex.Message)), ToastKind.Error);
        }
        finally
        {
            if (button is not null)
            {
                button.IsEnabled = true;
            }
        }
    }
}
