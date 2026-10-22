using System.Windows;
using System.Windows.Controls;
using Mosslight.App.Infrastructure;

namespace Mosslight.App.Views;

/// <summary>
/// 「更多」页：关于与鸣谢 + 帮助与反馈。
///
/// 版式照 PCL2 的「更多」那一栏，但内容只做我们已经有的两块（帮助 / 关于与鸣谢）；
/// 「百宝箱」「新功能投票」这种等真有东西了再加，别先摆一排点不动的按钮。
/// </summary>
public partial class MorePage : UserControl, IMosslightPage
{
    public MorePage()
    {
        InitializeComponent();

        SponsorButton.ToolTip = Credits.SponsorUrl;

        foreach (var entry in Credits.About)
        {
            AboutRows.Children.Add(CreditRows.Build(this, entry, OnCreditAction));
        }
        foreach (var entry in Credits.Thanks)
        {
            ThanksRows.Children.Add(CreditRows.Build(this, entry, OnCreditAction));
        }
        foreach (var entry in Credits.Links)
        {
            LinkRows.Children.Add(CreditRows.Build(this, entry, OnCreditAction));
        }

        // 名单空着的时候说一句，别留一张空白卡片（填了之后这行自动消失）
        ThanksEmptyText.Visibility = Credits.Thanks.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public Panel? AnimationHost => null;

    private void OnSponsorClick(object sender, RoutedEventArgs e) => ShellOpen.Url(Credits.SponsorUrl);

    /// <summary>
    /// 一条数据的按钮：有 Url 就开浏览器，没有就是内部动作（目前只有「检查更新」）。
    /// </summary>
    private async void OnCreditAction(CreditEntry entry, Button button)
    {
        if (entry.Url is { Length: > 0 } url)
        {
            ShellOpen.Url(url);
            return;
        }
        await UpdateCheck.RunAsync(Window.GetWindow(this), button);
    }
}
