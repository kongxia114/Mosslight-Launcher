using System.Windows.Controls;
using Mosslight.Core.Localization;

namespace Mosslight.App.Views;

/// <summary>「还没做」的页面（下载 / 设置 / 账户）。</summary>
public partial class PlaceholderPage : UserControl, IMosslightPage
{
    public PlaceholderPage()
        : this("这一页还没做", "2.0 的界面在逐页搬。", string.Empty)
    {
    }

    public PlaceholderPage(string title, string body, string hint)
    {
        InitializeComponent();
        TitleText.Text = Localizer.Tr(title);
        SubtitleText.Text = Localizer.Tr("Mosslight Launcher 2.0 · 界面原型");
        BodyText.Text = Localizer.Tr(body);
        HintText.Text = Localizer.Tr(hint);
        // ⚠️ 必须写全 `System.Windows.Visibility`：UserControl 自己有个 Visibility 属性，
        // 在类内部光写 Visibility 会被解析成那个属性（CS0176）
        HintText.Visibility = string.IsNullOrEmpty(hint)
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;
    }

    /// <summary>这一页只有两块（标题栏 + 卡片），一起入场的意义不大，交给主窗口决定。</summary>
    public Panel? AnimationHost => null;
}
