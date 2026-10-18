using System.Diagnostics;
using System.Windows;
using Mosslight.Core.Localization;

namespace Mosslight.App.Views;

/// <summary>设计说明见 docs/design-notes.md#mosslight-app-views-aboutdialog-xaml-cs</summary>
public partial class AboutDialog : Window
{
    /// <summary>仓库地址（跟 1.0.0 的 `about_content.PROJECT_URL` 同一个）。</summary>
    private const string ProjectUrl = "https://github.com/kongxia114/Mosslight-Launcher";

    private const string DotNetSiteUrl = "https://dotnet.microsoft.com/";

    private const string DotNetLicenseUrl = "https://github.com/dotnet/runtime/blob/main/LICENSE.TXT";

    private const string MitLicenseUrl = "https://opensource.org/license/mit";

    /// <summary>
    /// 导航图标的出处（Lucide，ISC 许可）。
    ///
    /// ⚠️ 这一栏说的是**图标数据**：图形是从 PCL2-CE 仓库里带的那份 lucide svg
    ///    转过来的，但 lucide 图标本身是 ISC 许可的独立作品，跟 PCL2-CE 的
    ///    GPL-3.0 代码是两回事 —— 所以这里致谢 Lucide，不写 PCL2-CE。
    ///    （生成器和转换细节见 `tools\gen_nav_icons.py` 的文档注释。）
    /// </summary>
    private const string LucideSiteUrl = "https://lucide.dev/";

    private const string LucideLicenseUrl = "https://lucide.dev/license";

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

    private void OnLucideSiteClick(object sender, RoutedEventArgs e) => Open(LucideSiteUrl);

    private void OnLucideLicenseClick(object sender, RoutedEventArgs e) => Open(LucideLicenseUrl);

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
