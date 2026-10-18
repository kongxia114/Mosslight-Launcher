using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Mosslight.App.Infrastructure;
using Mosslight.Core.Localization;

namespace Mosslight.App.Views;

/// <summary>
/// 可折叠的版本分组（下载页第一页的四组、安装选项页的五家加载器）。
///
/// ## 为什么行是懒建的
/// 快照有 754 个。展开时全铺出来，WPF 要建两千多个元素 —— 一眼看上去是"点了没反应"。
/// 所以一次只建一页（<see cref="PageSize"/>），底下挂一个「加载更多」。
///
/// ## 为什么收起时不销毁行
/// 用户折叠多半只是想看看下面是什么，再展开还要重建一遍纯属浪费
/// （1.0.0 的 `collapsible_group.py` 也是这个取舍）。
///
/// ## 入场动画
/// 每铺一页，**新加的那几行**依次滑入（<see cref="AnimateNewRows"/>）——
/// 参数、错峰、总开关的语义全部照 1.0.0 的 `collapsible_group.py`，
/// 轨迹算法走 <see cref="PageAnimator"/>（= 2.0 的入场动画引擎）。
/// </summary>
public partial class CollapsibleGroup : UserControl
{
    /// <summary>一次建多少行（滚到底点「加载更多」再加一页）。</summary>
    private const int PageSize = 60;

    /// <summary>
    /// 行与行之间的缝。行自己有底色，挨着排会糊成一大坨。
    ///
    /// ⚠️ 这个数是量出来的，不是拍的：1.0.0 那张截图（1360×890，比本工程的
    ///    1280×820 大 6.25%）里行距 54.2 逻辑像素、行高 51，减出来就是 3~4。
    /// </summary>
    private const double RowGap = 4;

    private readonly List<VersionRowItem> _items = new();
    private readonly List<VersionRow> _rows = new();

    private string _title = string.Empty;
    private string _note = string.Empty;
    private string? _pickedId;
    private string? _actionText;
    private double _dpiScale = 1.0;
    private int _built;
    private bool _expanded;

    /// <summary>标题栏右边那颗「×」要不要出现（安装页用，见 <see cref="SetClearVisible"/>）。</summary>
    private bool _clearVisible;

    /// <summary>这一组是不是"用不了"（见 <see cref="SetUnavailable"/>）。</summary>
    private bool _unavailable;

    private Button? _moreButton;

    public CollapsibleGroup()
    {
        InitializeComponent();
    }

    /// <summary>点了一行（带数据）。</summary>
    public event EventHandler<VersionRowItem>? ItemActivated;

    /// <summary>点了某一行行尾的动作按钮（只有 <see cref="SetAction"/> 挂过才有）。</summary>
    public event EventHandler<VersionRowItem>? ItemActionClicked;

    /// <summary>展开 / 收起之后（页面可以按需拉数据）。</summary>
    public event EventHandler? ExpandedChanged;

    /// <summary>组里的条目数。</summary>
    public int Count => _items.Count;

    public bool IsExpanded => _expanded;

    /// <summary>设标题（分组名，例如「正式版」）。</summary>
    public void SetTitle(string title)
    {
        _title = title ?? string.Empty;
        RefreshHeader();
    }

    /// <summary>
    /// 标题右边的副标题。
    ///
    /// ⚠️ 传空串就整块收起来 —— 不这样的话「Forge」后面会永远挂着一块空白，
    ///    在标题栏里看着像排版错了。
    /// </summary>
    public void SetNote(string text)
    {
        _note = (text ?? string.Empty).Trim();
        NoteText.Text = _note;
        NoteText.Visibility = _note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 给组里每一行挂一个行尾动作按钮（模组详情页的绿色「下载」）。
    ///
    /// ⚠️ 行是懒建的（见类注释），所以文字要**记下来**：建到第 2 页的那些行
    ///    也得带上按钮，不然"展开更多"之后按钮就没了。
    /// </summary>
    public void SetAction(string text)
    {
        _actionText = text;
        foreach (var row in _rows)
        {
            row.SetAction(text);
        }
    }

    /// <summary>换一批数据（会清掉已经建好的行）。</summary>
    public void SetItems(IReadOnlyList<VersionRowItem> items, double dpiScale)
    {
        _dpiScale = dpiScale;
        _items.Clear();
        _items.AddRange(items);
        ClearRows();
        _built = 0;
        RefreshHeader();
        if (_expanded)
        {
            BuildPage();
        }
    }

    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        BodyHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (expanded && _built == 0)
        {
            BuildPage();
        }
        RefreshHeader();
        ExpandedChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Toggle() => SetExpanded(!_expanded);

    /// <summary>把某一行标成"已选"，其余取消（null = 都不选）。</summary>
    public void SetPicked(string? id)
    {
        // ⚠️ 记下来：行是懒建的，标选中时后面几页的行还不存在 ——
        //    等它们建出来时（BuildPage）要按这个值补上标记，否则
        //    "展开一个已经选中了版本的分组"会看到里面一行都没选中。
        _pickedId = id;
        foreach (var row in _rows)
        {
            row.IsPicked = row.Item is not null && row.Item.Id == id;
        }
        // 取消选择之后那颗叉也该跟着消失
        RefreshClearVisibility();
    }

    /// <summary>当前标着"已选"的那一行的数据（没有就是 null）。</summary>
    public VersionRowItem? PickedItem        => _rows.FirstOrDefault(r => r.IsPicked)?.Item;

    /// <summary>
    /// 按 id 找一条数据（**不用它已经被建出来**）。
    ///
    /// ⚠️ 不能用 `Rows` 找：行是懒建的（`SetExpanded` 之后才建第一页），
    ///    在"远古版本"那种默认收起的分组里，`Rows` 是空的 ——
    ///    拿它找 `1.8` 永远找不到，于是 `--mc 1.8` 会静默退回最新正式版
    ///    （2026-10-18 实测踩到）。
    /// </summary>
    public VersionRowItem? Find(string id)
        => _items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// 标题栏右边那颗「×」显示吗（安装页的"取消选择这一项"）。
    ///
    /// ⚠️ 只在**这一组能取消**时显示：版本列表那几组（正式版 / 快照）不给叉，
    ///    选了就是选了，取消的方式是不选/换一个。
    /// </summary>
    public void SetClearVisible(bool visible)
    {
        _clearVisible = visible;
        RefreshClearVisibility();
    }

    /// <summary>点了那颗「×」（安装页据此把这一项从选择里去掉）。</summary>
    public event EventHandler? ClearRequested;

    /// <summary>
    /// 把这一组显示成"这一项现在用不了"（PCL 的"与 Fabric 不兼容"那种行）。
    ///
    /// ⚠️ 只**看起来**不能用（变灰、不展开、不响应点击），不是 `IsEnabled=false`：
    ///    禁用的控件里那行灰色文字对比度会掉到看不清，而这里恰恰要把
    ///    "为什么不兼容"读清楚。
    /// </summary>
    public void SetUnavailable(bool unavailable)
    {
        _unavailable = unavailable;
        Header.Opacity = unavailable ? 0.62 : 1.0;
        Header.Cursor = unavailable ? Cursors.Arrow : Cursors.Hand;
        if (unavailable && _expanded)
        {
            SetExpanded(false);
        }
    }

    /// <summary>这一组是不是"用不了"的状态（截图 / 自检用）。</summary>
    public bool IsUnavailable => _unavailable;

    // ---------- 内部 ----------

    private void OnHeaderClick(object sender, MouseButtonEventArgs e)
    {
        // ⚠️ 点在「×」上时**别**展开/收起：那颗按钮在标题栏里面，
        //    鼠标事件会先到按钮、再冒泡到这里（不挡的话点一次叉会顺带把分组收起来）
        if (_clearVisible && IsInsideClearButton(e.OriginalSource as DependencyObject))
        {
            return;
        }
        if (_unavailable)
        {
            return;
        }
        Toggle();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ClearRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool IsInsideClearButton(DependencyObject? node)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ClearButton))
            {
                return true;
            }
        }
        return false;
    }

    private void RefreshClearVisibility()
        => ClearButton.Visibility = _clearVisible && _pickedId is not null
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void RefreshHeader()
    {
        var arrow = _expanded ? "\u25be" : "\u25b8";      // ▾ / ▸
        // ⚠️ 没有条目时**别显示 0** ——「OptiFine 0」看着像"查到了 0 个"，
        //    而真实情况由右边那句副标题说（"这个版本没有可用的 OptiFine"）
        var count = _items.Count > 0 ? $" {_items.Count}" : string.Empty;
        TitleText.Text = $"{arrow}   {_title}{count}";
    }

    private void BuildPage()
    {
        var end = Math.Min(_items.Count, _built + PageSize);
        VersionRow? firstNewRow = null;
        for (var i = _built; i < end; i++)
        {
            var row = new VersionRow { Margin = new Thickness(0, 0, 0, RowGap) };
            row.Bind(_items[i], _dpiScale);
            // 新建出来的行也要认得"当前选中的是哪一个"（见 SetPicked 的注释）
            row.IsPicked = _pickedId is not null && _items[i].Id == _pickedId;
            row.Activated += (_, item) => ItemActivated?.Invoke(this, item);
            // 动作按钮同样是"建行时才知道"的东西 —— 后建的行照样要带上
            if (_actionText is not null)
            {
                row.SetAction(_actionText);
                row.ActionClicked += (_, item) => ItemActionClicked?.Invoke(this, item);
            }
            firstNewRow ??= row;
            _rows.Add(row);
            // 插在「加载更多」之前，顺序才跟数据一致
            BodyHost.Children.Insert(MoreButtonIndex(), row);
            _built++;
        }
        RefreshMoreButton();
        AnimateNewRows(firstNewRow);
    }

    /// <summary>
    /// 给**这一页新加的行**挂入场动画（= 1.0.0 `collapsible_group.py` 的
    /// `_build_page()` + `_stagger_for_rows()`）。
    ///
    /// 三条跟 1.0.0 对齐的取舍：
    ///   · 用的参数是**版本列表那一套更短的**（`AnimationPresets.ForVersions`）——
    ///     组展开后一次出来几十行，用搜索页那套参数会显得拖沓；
    ///   · 每次铺新页都**重新读一次配置**（用户刚在设置里换了风格就该生效），
    ///     所以这里不缓存 preset，交给 <see cref="PageAnimator"/> 现取；
    ///   · 总开关关掉时**连那一层都不包** —— `PageAnimator.BuildPlan` 直接返回 null，
    ///     一行元素都不会挂 RenderTransform / 透明度动画（不是"时长为 0"）。
    ///
    /// ⚠️ 只给**新加的那几行**挂（<c>firstIndex</c>）：整容器重播的话，上面几页
    ///    已经入过场的行会跟着再滑一次，看着像整组被刷新了。
    /// </summary>
    private void AnimateNewRows(VersionRow? firstNewRow)
    {
        if (firstNewRow is null) return;
        var firstIndex = BodyHost.Children.IndexOf(firstNewRow);
        if (firstIndex < 0) return;
        PageAnimator.AnimateInFrom(BodyHost, App.Settings, firstIndex, forVersions: true);
    }

    private void ClearRows()
    {
        BodyHost.Children.Clear();
        _rows.Clear();
        _moreButton = null;
    }

    private int MoreButtonIndex()
        => _moreButton is null ? BodyHost.Children.Count : BodyHost.Children.IndexOf(_moreButton);

    private void RefreshMoreButton()
    {
        if (_built >= _items.Count)
        {
            if (_moreButton is not null)
            {
                BodyHost.Children.Remove(_moreButton);
                _moreButton = null;
            }
            return;
        }

        if (_moreButton is null)
        {
            _moreButton = new Button
            {
                Style = (Style)FindResource("LinkButton"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 0, 2),
            };
            _moreButton.Click += (_, _) => BuildPage();
            BodyHost.Children.Add(_moreButton);
        }
        _moreButton.Content = Localizer.Tr("加载更多（还有 {n} 个）",
            ("n", _items.Count - _built));
    }

    /// <summary>这一页里所有行（自检 / 调试用）。</summary>
    internal IReadOnlyList<VersionRow> Rows => _rows;
}
