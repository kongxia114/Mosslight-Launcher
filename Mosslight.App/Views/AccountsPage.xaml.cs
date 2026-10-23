using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using Mosslight.App.Infrastructure;
using Mosslight.Core.Localization;
using Mosslight.Core.Services;

namespace Mosslight.App.Views;

public partial class AccountsPage : UserControl, IMosslightPage
{
    private const double RowHeight = 62;

    // 头像刷新是后台任务，重复触发没意义（页面打开 + 用户点按钮可能撞上）。
    private bool _skinRefreshRunning;

    public AccountsPage()
    {
        InitializeComponent();
        Reload();

        // 第三方账户还没有本地皮肤就后台补一张（不联网的话安静失败，不影响列表）
        _ = RefreshThirdPartySkinsAsync();
    }

    /// <summary>这一页不做入场动画：账户页是普通卡片，直接进布局。</summary>
    public Panel? AnimationHost => null;

    // 当前选中的那一行（**不等于**"当前档案"，那要双击才切换）。
    private Account? Selected => AccountList.SelectedItem is ListBoxItem { Tag: Account a } ? a : null;

    // 重贴

    /// <summary>重新从服务读一遍账户，重建列表。</summary>
    public void Reload()
    {
        var accounts = App.Services.Accounts.All;
        var current = App.Services.Accounts.Current;

        AccountList.Items.Clear();
        foreach (var account in accounts)
        {
            var item = new ListBoxItem
            {
                Tag = account,
                Content = BuildRow(account, current?.Name == account.Name),
                Height = account.AvailableProfiles is { Count: > 1 } ? double.NaN : RowHeight,
            };
            AccountList.Items.Add(item);
        }

        // 选中"当前档案"那一行 —— 但**不**触发切换（没挂 SelectionChanged 的切换逻辑）
        var currentIndex = accounts.ToList().FindIndex(a => a.Name == current?.Name);
        if (currentIndex >= 0)
        {
            AccountList.SelectedIndex = currentIndex;
        }

        var empty = accounts.Count == 0;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.IsEnabled = !empty && Selected is not null;

        HideError();
        UpdateSidebarChip();
    }

    /// <summary>一行：竖条 + 头像 + 「名字 / uuid 短 · 类型」。</summary>
    private Grid BuildRow(Account account, bool isCurrent)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 竖条
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 头像
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 左侧竖条。**不是当前档案也占位**，这样两种行的文字起点能对齐。
        var accent = new Border
        {
            Style = (Style)Application.Current.FindResource(
                isCurrent ? "AccountRowAccent" : "AccountRowAccentOff"),
            Width = 4,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Grid.SetColumn(accent, 0);
        row.Children.Add(accent);

        // 头像：配置目录里自己放的 `<档案名>.png` 优先，否则按 UUID 挑内置默认皮肤。
        // 读不出来就退回名字首字母（跟主窗口底部那张卡片一个处理）。
        var avatar = new Border
        {
            Style = (Style)Application.Current.FindResource("AccountRowAvatar"),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            // ⚠️ 尺寸**必须写死**：`AccountRowAvatar` 那个样式没有 Width/Height
            // （它是给"名字首字母"用的，靠文字大小撑开）。给图片用的时候
            // 不写死就会被图片的自身尺寸撑开，行高、左边距全跟着跑。
            Width = 32,
            Height = 32,
        };
        var face = PlayerAvatars.Avatar(
            account, Core.Settings.AppSettings.ResolveConfigDir(App.BaseDir),
            size: 32, dpr: DpiScale());
        if (face is not null)
        {
            // 头像本身已经是正圆（见 PlayerAvatars.ComposeFace），所以把容器的底色
            // 让出来，不然圆形头像外面会露出一圈方形的底色。
            // ⚠️ 描边也要去掉：32×32 的框再扣掉 1px 描边只剩 30，头像会被压小再放大
            // （= 一缩一放，像素画立刻糊）。正圆的边缘让位给透明像素更好看。
            avatar.Background = Brushes.Transparent;
            avatar.BorderThickness = new Thickness(0);
            var image = new Image { Source = face, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            avatar.Child = image;
        }
        else
        {
            avatar.Child = new TextBlock
            {
                Text = account.Name.Length > 0 ? account.Name[..1].ToUpperInvariant() : "?",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
            };
        }
        Grid.SetColumn(avatar, 1);
        row.Children.Add(avatar);

        var text = new StackPanel
        {
            Margin = new Thickness(12, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        text.Children.Add(new TextBlock
        {
            Text = account.Name,
            Style = (Style)Application.Current.FindResource(
                isCurrent ? "AccountRowNameCurrent" : "AccountRowName"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var shortUuid = account.Uuid.Length >= 8 ? account.Uuid[..8] + "…" : "—";
        text.Children.Add(new TextBlock
        {
            Text = $"{shortUuid}    ·    {RealAccountService.TypeLabel(account.Kind)}",
            Style = (Style)Application.Current.FindResource("AccountRowMeta"),
        });

        // 一个第三方账号可能有好几个角色：给个下拉，随手切。
        // 条目直接放**名字字符串** —— 放对象的话要配模板，主题里的 ComboBox
        // 样式一忽略 DisplayMemberPath 就会露出 `MinecraftProfile { ... }` 的 ToString。
        if (account.AvailableProfiles is { Count: > 1 } profiles)
        {
            var index = Math.Max(0, profiles.ToList().FindIndex(p =>
                string.Equals(p.Id, account.SelectedProfileUuid, StringComparison.OrdinalIgnoreCase)));
            var picker = new ComboBox
            {
                ItemsSource = profiles.Select(p => p.Name).ToList(),
                SelectedIndex = index,
                Margin = new Thickness(0, 4, 0, 0),
                Height = 24,
                Width = 160,
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = account,
            };
            picker.SelectionChanged += OnProfileSelectionChanged;
            text.Children.Add(picker);
        }

        Grid.SetColumn(text, 2);
        row.Children.Add(text);
        return row;
    }

    // 新建

    private void OnNewAccountClick(object sender, RoutedEventArgs e)
    {
        var wizard = new NewAccountWindow { Owner = Window.GetWindow(this) };
        if (wizard.ShowDialog() == true)
        {
            Reload();
        }
    }

    private void ShowError(string text)
    {
        AddErrorText.Text = text;
        AddErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => AddErrorText.Visibility = Visibility.Collapsed;

    // 选中 / 切换 / 删除

    /// <summary>下拉切角色：立刻存盘 + 刷新列表（启动参数读的就是这里存下的名字/uuid）。</summary>
    private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: Account account } combo || combo.SelectedIndex < 0)
        {
            return;
        }
        if (account.AvailableProfiles is not { } profiles || combo.SelectedIndex >= profiles.Count)
        {
            return;
        }
        var profile = profiles[combo.SelectedIndex];
        if (string.Equals(profile.Id, account.SelectedProfileUuid, StringComparison.OrdinalIgnoreCase)
            || App.Services.Accounts is not RealAccountService accounts)
        {
            return;
        }
        accounts.SelectProfile(account.Name, profile.Id);
        Reload();
        UpdateSidebarChip();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // ⚠️ 这里**只**更新按钮状态，**不**切换当前档案 ——
        // 切换是双击的事。
        DeleteButton.IsEnabled = Selected is not null;
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var account = Selected;
        if (account is null)
        {
            return;
        }

        App.Services.Accounts.Select(account);
        Reload();

        // 第三方账户还没有本地皮肤就后台补一张（不联网的话安静失败，不影响列表）
        _ = RefreshThirdPartySkinsAsync();
    }

    /// <summary>
    /// 打开页面时按 TTL 补/刷新第三方头像：本地皮肤超过
    /// Mosslight.Core.Services.Auth.SkinAvatarService.RefreshAfter 就重新拉。
    /// </summary>
    private Task RefreshThirdPartySkinsAsync(bool force = false)
    {
        if (_skinRefreshRunning)
        {
            return Task.CompletedTask;
        }
        _skinRefreshRunning = true;
        SkinStatusText.Text = Localizer.Tr("正在刷新头像…");
        return RefreshThirdPartySkinsCoreAsync(force);
    }

    private async Task RefreshThirdPartySkinsCoreAsync(bool force)
    {
        try
        {
            var configDir = Core.Settings.AppSettings.ResolveConfigDir(App.BaseDir);
            var updated = await Mosslight.Core.Services.Auth.SkinAvatarService.RefreshAllAsync(
                App.Services.Accounts.All, configDir, force);
            if (updated > 0)
            {
                Dispatcher.Invoke(Reload);
            }
            Dispatcher.Invoke(() => SkinStatusText.Text = updated > 0
                ? Localizer.Tr("头像已刷新")
                : string.Empty);
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => SkinStatusText.Text =
                Localizer.Tr("头像刷新失败：{err}", ("err", ex.Message)));
        }
        finally
        {
            _skinRefreshRunning = false;
        }
    }

    private async void OnRefreshSkinsClick(object sender, RoutedEventArgs e)
    {
        RefreshSkinsButton.IsEnabled = false;
        try
        {
            await RefreshThirdPartySkinsAsync(force: true);
        }
        finally
        {
            RefreshSkinsButton.IsEnabled = true;
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var account = Selected;
        if (account is null)
        {
            return;
        }

        // ⚠️ 这一页不弹窗，删除后用错误行提示“只删本地记录，不影响游戏内数据”。
        App.Services.Accounts.Remove(account.Name);
        Reload();
        ShowError(Localizer.Tr("这只删除本地记录，不影响游戏内的数据。"));
    }

    // 通知别处刷新

    /// <summary>账户变了，主窗口底部那张卡片和启动页的「当前档案」都要跟着变。</summary>
    private void UpdateSidebarChip()
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.RefreshAccountChip();
        }
    }

    /// <summary>
    /// 屏幕缩放比；⚠️ BuildRow 是静态方法（refresh 里也静态调），拿不到 this，所以只能借主窗口当
    /// 参照物；没有主窗口时（理论上不会）退回 1.0。
    /// </summary>
    private static double DpiScale()
    {
        return Application.Current?.MainWindow is Visual visual
            ? VisualTreeHelper.GetDpi(visual).DpiScaleX
            : 1.0;
    }
}
