using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Mosslight.App.Infrastructure;

namespace Mosslight.App.Views;

/// <summary>
/// 版本行要显示的东西。
///
/// 三个列表（正式版 / 快照 / 本地版本）和安装选项页的加载器版本行**形状是一样的**
/// （图标 + 版本号 + 一行副标题），差别只在图标从哪来、副标题写什么。
/// 与其在页面里拼控件，不如统一成一个模型 + 一个行控件。
/// </summary>
/// <param name="Id">版本 id（选中判定、装哪个版本都用它）。</param>
/// <param name="DisplayName">显示名（一般就是 id）。</param>
/// <param name="Meta">第二行小字（"发布于 …" / "可用于 26.3" / "最新正式版，发布于 …"）。</param>
/// <param name="IconName">用哪张内置图（见 <see cref="VersionIcons.NameFor"/>）。</param>
/// <param name="Payload">原始对象（加载器行要拿它的安装器 hash 等字段）。</param>
public sealed record VersionRowItem(
    string Id,
    string DisplayName,
    string Meta,
    string IconName,
    object? Payload = null);

/// <summary>一行版本（图标 + 两行字，可点、可标选中）。</summary>
public partial class VersionRow : UserControl
{
    /// <summary>用户点了这一行。</summary>
    public event EventHandler<VersionRowItem>? Activated;

    /// <summary>
    /// 用户点了行尾那个动作按钮（只有 <see cref="SetAction"/> 挂过按钮的行才会有）。
    /// ⚠️ 跟 <see cref="Activated"/> 是**两件事**：点按钮只干按钮的事，不该顺带"选中这一行"。
    /// </summary>
    public event EventHandler<VersionRowItem>? ActionClicked;

    public VersionRow()
    {
        InitializeComponent();
    }

    /// <summary>这一行是不是"用户当前选中的那个"。</summary>
    public static readonly DependencyProperty IsPickedProperty = DependencyProperty.Register(
        nameof(IsPicked), typeof(bool), typeof(VersionRow), new PropertyMetadata(false));

    public bool IsPicked
    {
        get => (bool)GetValue(IsPickedProperty);
        set => SetValue(IsPickedProperty, value);
    }

    /// <summary>这一行对应的数据。</summary>
    public VersionRowItem? Item { get; private set; }

    /// <summary>
    /// 填内容。
    ///
    /// <paramref name="dpiScale"/> 由调用方给（页面从视觉树拿一次，几十行共用）——
    /// 每一行自己去问一次 DPI 会白白走一遍视觉树。
    /// </summary>
    public void Bind(VersionRowItem item, double dpiScale)
    {
        Item = item;
        NameText.Text = item.DisplayName;
        MetaText.Text = item.Meta;
        ToolTip = item.DisplayName;

        var source = VersionIcons.Load(item.IconName, VersionIcons.RowSize, dpiScale);
        IconImage.Source = source;
        // 认不出图标就整块收起来：留一个 28px 的空位会让文字对不齐
        IconImage.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 给这一行挂一个行尾动作按钮（模组详情页的绿色「下载」）。
    /// 不调它就一直是收起来的 —— 下载页那几列版本行不受影响。
    /// </summary>
    public void SetAction(string text)
    {
        ActionButton.Content = text;
        ActionButton.Visibility = Visibility.Visible;
    }

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (Item is not null)
        {
            ActionClicked?.Invoke(this, Item);
        }
    }

    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (Item is not null)
        {
            Activated?.Invoke(this, Item);
        }
    }
}
