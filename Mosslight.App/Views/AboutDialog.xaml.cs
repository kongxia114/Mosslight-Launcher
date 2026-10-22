using System.Windows;
using Mosslight.Core.Localization;

namespace Mosslight.App.Views;

/// <summary>设计说明见 docs/design-notes.md#mosslight-app-views-aboutdialog-xaml-cs</summary>
public partial class AboutDialog : Window
{
    /// <summary>仓库地址（跟 1.0.0 的 `about_content.PROJECT_URL` 同一个）。</summary>
    private const string ProjectUrl = "https://github.com/kongxia114/Mosslight-Launcher-2";

    private const string DotNetSiteUrl = "https://dotnet.microsoft.com/";

    private const string DotNetLicenseUrl = "https://github.com/dotnet/runtime/blob/main/LICENSE.TXT";

    /// <summary>
    /// 完整许可证（GPL-3.0）。
    ///
    /// ⚠️ 以前这里指向 opensource.org 的 **MIT** 文本，跟仓库里的 `LICENSE`（GPL-3.0）
    ///    自相矛盾 —— 2026-10 对着 README 的徽章才发现。现在指向仓库里那份真文本。
    /// </summary>
    private const string LicenseUrl =
        "https://github.com/kongxia114/Mosslight-Launcher-2/blob/master/LICENSE";

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

    /// <summary>
    /// 用系统默认浏览器打开（具体实现见 <see cref="Infrastructure.ShellOpen"/>）。
    ///
    /// ⚠️ 以前这里是私有实现，「更多」页那边也有一份 —— 两处各写一份迟早不一样，
    ///    2026-10 抽成公用的了。
    /// </summary>
    private static void Open(string url) => Infrastructure.ShellOpen.Url(url);
}
