using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using Mosslight.App.Infrastructure;
using Mosslight.Core.Localization;
using Mosslight.Core.Services;

namespace Mosslight.App.Views;

/// <summary>
/// 「账户」页。移植自 1.0.0 的 `ui/pages/accounts_page.py`。
///
/// ## 跟 1.0.0 一致的三条
///
/// 1. **单击只选中，双击才真的切换**。1.0.0 的注释写得很清楚：旧代码连了
///    `currentItemChanged`，于是每次 refresh() 里的 setCurrentItem 都会触发
///    set_current + 写盘，刷新时还会重复发信号。所以这里只挂
///    <c>MouseDoubleClick</c>，不挂 <c>SelectionChanged</c> 去改当前档案。
/// 2. **「当前档案」用左侧竖条表示**，不用右侧徽章 —— 徽章飘在行尾离内容很远。
///    而且**不是当前档案也占位**（只是颜色不同），这样两种行的文字起点能对齐。
/// 3. 列表行的高度、头像尺寸、分隔符 `"    ·    "` 都照抄。
///
/// ## 有意不同的两处
///
/// 1. **新建档案件内联**，不弹对话框（见 XAML 注释）。
/// 2. 没有「设为当前」按钮 —— 和双击是同一件事。
/// </summary>
public partial class AccountsPage : UserControl, IMosslightPage
{
    /// <summary>行高（1.0.0 的 `ROW_HEIGHT = 62`）。</summary>
    private const double RowHeight = 62;

    public AccountsPage()
    {
        InitializeComponent();
        Reload();

        // 没配 LittleSkin 应用就把按钮灰掉并说明原因：点了没反应是最糟的体验
        if (!Mosslight.Core.Services.Auth.LittleSkinSecrets.IsConfigured)
        {
            LittleSkinButton.IsEnabled = false;
            LittleSkinStatus.Text = Localizer.Tr("还没有配置 LittleSkin 应用（Client ID / Secret 为空）。");
        }
    }

    /// <summary>
    /// 这一页**不做入场动画**：1.0.0 的入场动画只用在版本列表和模组列表上，
    /// 账户页就是普通卡片直接进布局。
    /// </summary>
    public Panel? AnimationHost => null;

    /// <summary>当前选中的那一行（**不等于**"当前档案"，那要双击才切换）。</summary>
    private Account? Selected => AccountList.SelectedItem is ListBoxItem { Tag: Account a } ? a : null;

    // ---------- 重贴 ----------

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
                Height = RowHeight,
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
    private static Grid BuildRow(Account account, bool isCurrent)
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
            //    （它是给"名字首字母"用的，靠文字大小撑开）。给图片用的时候
            //    不写死就会被图片的自身尺寸撑开，行高、左边距全跟着跑。
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
            //    （= 一缩一放，像素画立刻糊）。正圆的边缘让位给透明像素更好看。
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

        // uuid 取前 8 位 + 省略号（1.0.0 的 `f"{uuid[:8]}…"`），空的话给一个破折号
        var shortUuid = account.Uuid.Length >= 8 ? account.Uuid[..8] + "…" : "—";
        text.Children.Add(new TextBlock
        {
            // 分隔符是**四个空格 + 间隔号 + 四个空格**，跟 1.0.0 一致
            Text = $"{shortUuid}    ·    {RealAccountService.TypeLabel(account.Kind)}",
            Style = (Style)Application.Current.FindResource("AccountRowMeta"),
        });

        Grid.SetColumn(text, 2);
        row.Children.Add(text);
        return row;
    }

    // ---------- 新建 ----------

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

    // ---------- 选中 / 切换 / 删除 ----------

    /// <summary>
    /// LittleSkin 登录：开浏览器 → 本机回调 → token → 角色 → 写进账户表。
    /// 失败只更新状态行，不弹对话框（这一页的调子就是不弹窗）。
    /// </summary>
    private async void OnLittleSkinClick(object sender, RoutedEventArgs e)
    {
        if (!Mosslight.Core.Services.Auth.LittleSkinSecrets.IsConfigured)
        {
            ShowError(Localizer.Tr("还没有配置 LittleSkin 应用（Client ID / Secret 为空）。"));
            return;
        }
        if (App.Services.Accounts is not Mosslight.Core.Services.RealAccountService accounts)
        {
            ShowError(Localizer.Tr("这一版还不支持把第三方角色写进账户表。"));
            return;
        }

        LittleSkinButton.IsEnabled = false;
        HideError();
        LittleSkinStatus.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
        LittleSkinStatus.Text = Localizer.Tr("正在准备登录…");
        try
        {
            var options = Mosslight.Core.Services.Auth.LittleSkinSecrets.CreateOptions();
            var flow = new Mosslight.Core.Services.Auth.LittleSkinLoginFlow(options);
            var result = await flow.RunAsync(status =>
                Dispatcher.Invoke(() => LittleSkinStatus.Text = status));

            if (!result.Ok)
            {
                LittleSkinStatus.Text = string.Empty;
                ShowError(result.Error);
                return;
            }
            if (result.Profiles.Count == 0)
            {
                LittleSkinStatus.Text = string.Empty;
                ShowError(Localizer.Tr("这个账号还没有创建角色，请先在 LittleSkin 网站建一个。"));
                return;
            }

            var token = result.Token;
            var server = "littleskin";
            var last = result.Profiles[^1];
            foreach (var profile in result.Profiles)
            {
                accounts.AddThirdParty(profile.Name, profile.Uuid,
                    token?.AccessToken ?? string.Empty, token?.RefreshToken ?? string.Empty, server);
            }
            accounts.Select(accounts.All.FirstOrDefault(a => a.Name == last.Name));

            Reload();
            LittleSkinStatus.Text = Localizer.Tr("登录成功，已添加 {count} 个角色",
                ("count", result.Profiles.Count));
        }
        catch (Exception ex)
        {
            LittleSkinStatus.Text = string.Empty;
            ShowError(Localizer.Tr("登录失败：{err}", ("err", ex.Message)));
        }
        finally
        {
            LittleSkinButton.IsEnabled = true;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // ⚠️ 这里**只**更新按钮状态，**不**切换当前档案 ——
        //    1.0.0 的教训：连了选中变化就写盘，刷新时会重复触发。
        //    切换是双击的事。
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

    /// <summary>只补缺的：已有 `skins/&lt;名字&gt;.png` 的账户不再请求。</summary>
    private async Task RefreshThirdPartySkinsAsync()
    {
        var configDir = Core.Settings.AppSettings.ResolveConfigDir(App.BaseDir);
        var pending = App.Services.Accounts.All
            .Where(a => a.Kind == Core.Services.AccountKind.ThirdParty
                        && a.AuthServer.Length > 0
                        && !File.Exists(Mosslight.Core.Services.Auth.SkinAvatarService.SkinPathFor(configDir, a.Name)))
            .ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var changed = false;
        foreach (var account in pending)
        {
            var result = await Mosslight.Core.Services.Auth.SkinAvatarService
                .TryFetchAndSaveAsync(account, configDir);
            changed |= result.Ok;
        }
        if (changed)
        {
            Dispatcher.Invoke(Reload);
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var account = Selected;
        if (account is null)
        {
            return;
        }

        // ⚠️ 1.0.0 在这里弹确认对话框。这一页不弹窗（见类注释），
        //    改成"删了之后列表里还能看见它没了"——但**必须说清楚删的是什么**，
        //    所以把那句确认文案放进错误行当提示用。
        App.Services.Accounts.Remove(account.Name);
        Reload();
        ShowError(Localizer.Tr("这只删除本地记录，不影响游戏内的数据。"));
    }

    // ---------- 通知别处刷新 ----------

    /// <summary>
    /// 账户变了，主窗口底部那张卡片和启动页的「当前档案」都要跟着变。
    /// 页面刚建好时还没挂进视觉树，<c>Window.GetWindow</c> 会是 null，所以必须判空。
    /// </summary>
    private void UpdateSidebarChip()
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.RefreshAccountChip();
        }
    }

    /// <summary>
    /// 屏幕缩放比。头像按像素画，125% 缩放下不跟着放大就会糊成一团。
    ///
    /// ⚠️ <c>BuildRow</c> 是静态方法（refresh 里也静态调），拿不到 <c>this</c>，
    ///    所以只能借主窗口当参照物；没有主窗口时（理论上不会）退回 1.0。
    /// </summary>
    private static double DpiScale()
    {
        return Application.Current?.MainWindow is Visual visual
            ? VisualTreeHelper.GetDpi(visual).DpiScaleX
            : 1.0;
    }
}
