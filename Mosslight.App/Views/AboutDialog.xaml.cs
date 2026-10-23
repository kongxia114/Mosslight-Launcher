using System.Windows;
using Mosslight.Core.Localization;

namespace Mosslight.App.Views;

public partial class AboutDialog : Window
{
    private const string ProjectUrl = "https://github.com/kongxia114/Mosslight-Launcher-2";

    private const string DotNetSiteUrl = "https://dotnet.microsoft.com/";

    private const string DotNetLicenseUrl = "https://github.com/dotnet/runtime/blob/main/LICENSE.TXT";

    // 完整许可证（GPL-3.0）；⚠️ 必须与仓库 LICENSE 一致，不能指向 MIT。
    private const string LicenseUrl =
        "https://github.com/kongxia114/Mosslight-Launcher-2/blob/master/LICENSE";

    // 导航图标的出处（Lucide，ISC 许可）。
    private const string LucideSiteUrl = "https://lucide.dev/";

    private const string LucideLicenseUrl = "https://lucide.dev/license";

    private const string CopyrightYear = "2026";

    // 版权持有人：写的是**仓库名**，换人名只改这一行。
    private const string CopyrightHolder = "Mosslight-Launcher-2";

    public AboutDialog()
    {
        InitializeComponent();
        Infrastructure.FramelessWindow.Apply(this);

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

    private void OnLicenseClick(object sender, RoutedEventArgs e) => Open(LicenseUrl);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>用系统默认浏览器打开；⚠️ 只保留这一份实现，不要在两处各写一份。</summary>
    private static void Open(string url) => Infrastructure.ShellOpen.Url(url);
}
