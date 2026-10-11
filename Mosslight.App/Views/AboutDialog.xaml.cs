using System.Diagnostics;
using System.Windows;
using Mosslight.Core.Localization;

namespace Mosslight.App.Views;

/// <summary>
/// 「关于」对话框。从设置页的「关于」卡片里弹出来。
///
/// 移植自 1.0.0 的 `ui/dialogs/about_dialog.py` + `ui/widgets/about_content.py`。
///
/// ## 跟 1.0.0 的差别（都是**内容**上的，不是版式上的）
///   1. 「开源许可」那一栏：1.0.0 列的是 PyQt6 / Python / PyInstaller（GPL 那一家）。
///      2.0 是 WPF + .NET，**一个第三方包都没引**（见 Mosslight.App.csproj 的说明），
///      所以这一栏只有 .NET / WPF 一行，整体许可也从 GPL-3.0 换成 MIT。
///      照抄 PyQt6 那一行才是错的 —— 那会对着用户说假话。
///   2. 「查看源代码」按钮：仓库地址跟 1.0.0 用同一个（用户 2026-09 定的）。
///      没填地址时**禁用**按钮并在提示里说明，而不是留个死链（1.0.0 的处理方式）。
/// </summary>
public partial class AboutDialog : Window
{
    /// <summary>仓库地址（跟 1.0.0 的 `about_content.PROJECT_URL` 同一个）。</summary>
    private const string ProjectUrl = "https://github.com/kongxia114/Mosslight-Launcher";

    private const string DotNetSiteUrl = "https://dotnet.microsoft.com/";

    private const string DotNetLicenseUrl = "https://github.com/dotnet/runtime/blob/main/LICENSE.TXT";

    private const string MitLicenseUrl = "https://opensource.org/license/mit";

    private const string CopyrightYear = "2026";

    private const string CopyrightHolder = "Redstone Studio";

    public AboutDialog()
    {
        InitializeComponent();

        AppNameText.Text = Core.AppInfo.AppName;
        VersionText.Text = Core.AppInfo.AppVersion;

        CopyrightText.Text = Localizer.Tr("Copyright © {year} {holder}",
            ("year", CopyrightYear), ("holder", CopyrightHolder));

        if (ProjectUrl.Length > 0)
        {
            // 把地址也写在悬停提示里：按钮点开会跳到浏览器，但用户可能想先看清是哪个仓库
            SourceButton.ToolTip = ProjectUrl;
        }
        else
        {
            SourceButton.IsEnabled = false;
            SourceButton.ToolTip = Localizer.Tr("仓库地址还没定");
        }
    }

    private void OnSourceClick(object sender, RoutedEventArgs e) => Open(ProjectUrl);

    private void OnDotNetSiteClick(object sender, RoutedEventArgs e) => Open(DotNetSiteUrl);

    private void OnDotNetLicenseClick(object sender, RoutedEventArgs e) => Open(DotNetLicenseUrl);

    private void OnMitLicenseClick(object sender, RoutedEventArgs e) => Open(MitLicenseUrl);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 用系统默认浏览器打开。
    ///
    /// ⚠️ `Process.Start(UseShellExecute = true)` 而不是 WPF 的 NavigationService：
    ///    这一页没有 NavigationWindow，用 NavigationService 会抛异常。
    /// </summary>
    private static void Open(string url)
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
            // 打不开浏览器（没有默认浏览器 / 被策略挡住）不该崩窗口
            App.WriteLauncherLog($"打开链接失败：{url} ({ex.Message})");
        }
    }
}
