using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Mosslight.App.Infrastructure;

namespace Mosslight.App.Views;

/// <summary>版本行要显示的东西。</summary>
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

    /// <summary>用户点了行尾那个动作按钮（只有 SetAction 挂过按钮的行才会有）。</summary>
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

    /// <summary>给这一行挂一个行尾动作按钮（模组详情页的绿色「下载」）。</summary>
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
