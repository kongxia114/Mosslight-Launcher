using System;
using System.Windows;
using System.Windows.Media;
using Mosslight.App.Infrastructure;
using Mosslight.Core.Localization;
using Mosslight.Core.Services;
using Mosslight.Core.Services.Auth;

namespace Mosslight.App.Views;

/// <summary>
/// 新建档案的**向导**：一个窗口五个视图，前进/后退只切 Visibility。
///
/// 为什么不用五个窗口：切窗口会闪、会丢焦点，每个都得管位置和关闭逻辑；
/// 这里窗口尺寸固定（ResizeMode=NoResize），内容超了靠视图自己滚，不会跳。
/// </summary>
public partial class NewAccountWindow : Window
{
    private enum Step
    {
        Type,
        Microsoft,
        Server,
        Offline,
    }

    private const string LittleSkinServer = "https://littleskin.cn/api/yggdrasil";

    private Step _step = Step.Type;

    /// <summary>第三方那一支选中的认证服务器（预设或手输）。</summary>
    private string _serverUrl = LittleSkinServer;

    private bool _busy;

    /// <summary>截图用：第一页要不要顺手选中一项。</summary>
    private bool _screenshotSelect;

    public NewAccountWindow()
    {
        InitializeComponent();
        FramelessWindow.Apply(this);

        ServerCombo.SelectedIndex = 0;
        ApplyStep();
        Loaded += (_, _) => PlayerIdBox.Focus();
    }

    /// <summary>截图用：直接跳到某一页。</summary>
    public void GoToStepForScreenshot(string key)
    {
        // `type-selected` 只是截图用的：默认不选中，截出来看不出选中态
        if (key == "type-selected")
        {
            key = "type";
            _screenshotSelect = true;
        }
        _step = key switch
        {
            "microsoft" => Step.Microsoft,
            "server" => Step.Server,
            "server-web" => Step.Server,
            "server-custom" => Step.Server,
            "offline" => Step.Offline,
            "offline-custom" => Step.Offline,
            _ => Step.Type,
        };
        ApplyStep();
        if (_screenshotSelect)
        {
            TypeList.SelectedIndex = 1;
        }
        if (key == "server-web")
        {
            SetMode(web: true);
        }
        else if (key == "offline-custom")
        {
            UuidCustomRadio.IsChecked = true;
        }
        else if (key == "server-custom")
        {
            ServerCombo.SelectedIndex = 1;      // 触发 OnServerChanged：网页授权会被禁掉
            CustomServerBox.Text = "https://example.com/api/yggdrasil";
        }
    }

    /// <summary>创建成功的那一条（取消/关闭时为 null）。</summary>
    public Account? CreatedAccount { get; private set; }

    // ============================================================
    //  状态机
    // ============================================================

    private void ApplyStep()
    {
        TypeView.Visibility = _step == Step.Type ? Visibility.Visible : Visibility.Collapsed;
        MicrosoftView.Visibility = _step == Step.Microsoft ? Visibility.Visible : Visibility.Collapsed;
        ServerView.Visibility = _step == Step.Server ? Visibility.Visible : Visibility.Collapsed;
        OfflineView.Visibility = _step == Step.Offline ? Visibility.Visible : Visibility.Collapsed;

        BackButton.Visibility = _step == Step.Type ? Visibility.Collapsed : Visibility.Visible;

        switch (_step)
        {
            case Step.Type:
                TitleText.Text = Localizer.Tr("新建档案");
                SubtitleText.Text = Localizer.Tr("你想用哪种方式登录？");
                NextButton.Content = Localizer.Tr("继续");
                NextButton.Visibility = Visibility.Visible;
                break;

            case Step.Microsoft:
                TitleText.Text = Localizer.Tr("正版验证");
                SubtitleText.Text = Localizer.Tr("用微软账号登录（需要先购买正版）");
                NextButton.Visibility = Visibility.Collapsed;
                break;

            case Step.Server:
                TitleText.Text = Localizer.Tr("选择认证服务器");
                SubtitleText.Text = Localizer.Tr("第三方皮肤站一般基于 Yggdrasil API");
                NextButton.Content = Localizer.Tr("登录");
                NextButton.Visibility = Visibility.Visible;
                break;

            case Step.Offline:
                TitleText.Text = Localizer.Tr("离线验证");
                SubtitleText.Text = Localizer.Tr("只在本机生效，进不了正版服务器");
                NextButton.Content = Localizer.Tr("创建");
                NextButton.Visibility = Visibility.Visible;
                break;
        }

        if (_step == Step.Offline)
        {
            PlayerIdBox.Focus();
        }
        SetStatus(null);
    }

    private void GoTo(Step step)
    {
        if (_busy)
        {
            return;
        }
        _step = step;
        ApplyStep();
    }

    private void SetStatus(string? text, bool isError = false)
    {
        StatusText.Text = text ?? string.Empty;
        StatusText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Foreground = (Brush)FindResource(isError ? "DangerBrush" : "AccentTextBrush");
    }

    // ============================================================
    //  按钮
    // ============================================================

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        GoTo(_step switch
        {
            _ => Step.Type,
        });
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        DialogResult = false;
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        switch (_step)
        {
            case Step.Type:
                switch ((TypeList.SelectedItem as System.Windows.Controls.ListBoxItem)?.Tag as string)
                {
                    case "microsoft":
                        GoTo(Step.Microsoft);
                        break;
                    case "thirdparty":
                        GoTo(Step.Server);
                        break;
                    case "offline":
                        GoTo(Step.Offline);
                        break;
                    default:
                        SetStatus(Localizer.Tr("先选一种验证方式"), isError: true);
                        break;
                }
                break;

            case Step.Server:
                _serverUrl = SelectedServer();
                if (_serverUrl.Length == 0)
                {
                    SetStatus(Localizer.Tr("请填写认证服务器地址"), isError: true);
                    return;
                }
                if (_useWebLogin)
                {
                    OnOAuthLoginClick(sender, e);
                }
                else
                {
                    _ = LoginWithPasswordAsync();
                }
                break;

            case Step.Offline:
                CreateOfflineAccount();
                break;
        }
    }

    private void OnTypeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TypeList.SelectedIndex >= 0)
        {
            SetStatus(null);
        }
    }

    private void OnServerChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var isCustom = (ServerCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string == "custom";
        CustomServerBox.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        if (isCustom)
        {
            CustomServerBox.Focus();
        }
        _serverUrl = SelectedServer();
        SetMode(web: _useWebLogin && !isCustom);
    }

    private string SelectedServer()
    {
        var tag = (ServerCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string;
        if (tag == "custom")
        {
            return CustomServerBox.Text.Trim();
        }
        return LittleSkinServer;
    }

    // ============================================================
    //  正版
    // ============================================================

    private void OnMicrosoftLoginClick(object sender, RoutedEventArgs e)
    {
        // 微软登录要 Xbox Live 授权，个人开发者拿不到；说清楚比让用户干等强
        SetStatus(Localizer.Tr("微软登录还没有接入（需要 Xbox Live 授权）。"), isError: true);
    }

    private void OnBuyGameClick(object sender, RoutedEventArgs e) => OpenUrl("https://www.minecraft.net/store");

    private void OnOfficialSiteClick(object sender, RoutedEventArgs e)
    {
        OpenUrl(_step == Step.Microsoft ? "https://www.minecraft.net" : "https://littleskin.cn");
    }

    private void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetStatus(Localizer.Tr("打不开浏览器，请手动访问：{url}", ("url", url)), isError: true);
        }
    }

    // ============================================================
    //  第三方
    // ============================================================

    private void OnPasswordLoginClick(object sender, RoutedEventArgs e) => GoTo(Step.Server);

    private void OnPasswordKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            OnNextClick(sender, e);
        }
    }

    /// <summary>当前是不是「网页授权」子模式（只有 LittleSkin 能选）。</summary>
    private bool _useWebLogin;

    private void OnPasswordModeClick(object sender, RoutedEventArgs e) => SetMode(web: false);

    private void OnWebModeClick(object sender, RoutedEventArgs e)
    {
        if (!IsLittleSkinSelected())
        {
            // 自定义服务器没有 OAuth：不给切，提示一句就回密码模式
            SetMode(web: false);
            SetStatus(Localizer.Tr("自定义服务器不支持网页授权，请使用密码登录。"), isError: true);
            return;
        }
        SetMode(web: true);
    }

    private void SetMode(bool web)
    {
        _useWebLogin = web && IsLittleSkinSelected();
        PasswordModeTab.IsChecked = !_useWebLogin;
        WebModeTab.IsChecked = _useWebLogin;
        PasswordPanel.Visibility = _useWebLogin ? Visibility.Collapsed : Visibility.Visible;
        WebPanel.Visibility = _useWebLogin ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _useWebLogin ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = Localizer.Tr("登录");
        WebModeDisabledHint.Visibility = IsLittleSkinSelected() ? Visibility.Collapsed : Visibility.Visible;
        WebModeTab.IsEnabled = IsLittleSkinSelected();
        if (!_useWebLogin)
        {
            UserNameBox.Focus();
        }
    }

    private bool IsLittleSkinSelected()
        => _serverUrl.Contains("littleskin.cn", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 服务器怎么描述"要验证码"的都有：有的说 TOTP，有的说两步验证，有的直接返回
    /// "Invalid credentials"（其实是 2FA 没填）。这些关键词命中就当 2FA 处理。
    /// </summary>
    private static bool LooksLikeTwoFactor(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }
        string[] keywords =
        {
            "2fa", "two-factor", "two factor", "totp", "otp", "authenticator",
            "两步验证", "双因素", "二次验证", "验证码", "动态码",
        };
        return keywords.Any(k => error.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 标准 Yggdrasil 密码登录：LittleSkin 和自建皮肤站都能走这条路。
    /// 成功就把 availableProfiles 全部写成第三方账户。
    /// </summary>
    private async Task LoginWithPasswordAsync()
    {
        if (_busy)
        {
            return;
        }
        var user = UserNameBox.Text.Trim();
        var password = PasswordBoxControl.Password;
        if (user.Length == 0)
        {
            SetStatus(Localizer.Tr("请填写用户名（邮箱）"), isError: true);
            return;
        }
        if (password.Length == 0)
        {
            SetStatus(Localizer.Tr("请填写密码"), isError: true);
            return;
        }
        if (App.Services.Accounts is not RealAccountService accounts)
        {
            SetStatus(Localizer.Tr("这一版还不支持把第三方角色写进账户表。"), isError: true);
            return;
        }

        _busy = true;
        NextButton.IsEnabled = false;
        SetStatus(Localizer.Tr("正在登录…"));
        try
        {
            var service = new CustomYggdrasilService();
            var result = await service.AuthenticateWithPasswordAsync(_serverUrl, user, password);
            if (!result.Ok)
            {
                SetStatus(LooksLikeTwoFactor(result.Error)
                    ? Localizer.Tr("这个账号开启了两步验证，请切换到「网页授权」登录。")
                    : Localizer.Tr("登录失败：{err}", ("err", result.Error)), isError: true);
                return;
            }
            if (result.AvailableProfiles.Count == 0)
            {
                SetStatus(Localizer.Tr("这个账号还没有角色，请先在皮肤站里创建一个。"), isError: true);
                return;
            }

            Account? last = null;
            foreach (var profile in result.AvailableProfiles)
            {
                last = accounts.AddThirdParty(profile.Name, profile.Uuid, result.AccessToken,
                    string.Empty, _serverUrl, result.ClientToken);
            }
            accounts.Select(last);
            CreatedAccount = last;
            PasswordBoxControl.Clear();
            DialogResult = last is not null;
        }
        catch (Exception ex)
        {
            SetStatus(Localizer.Tr("登录失败：{err}", ("err", ex.Message)), isError: true);
        }
        finally
        {
            _busy = false;
            NextButton.IsEnabled = true;
        }
    }

    private async void OnOAuthLoginClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        if (!LittleSkinSecrets.IsConfigured)
        {
            SetStatus(Localizer.Tr("还没有配置 LittleSkin 应用（Client ID / Secret 为空）。"), isError: true);
            return;
        }

        _busy = true;
        WebLoginButton.IsEnabled = false;
        SetStatus(Localizer.Tr("正在准备登录…"));
        try
        {
            var flow = new LittleSkinLoginFlow(LittleSkinSecrets.CreateOptions());
            var result = await flow.RunAsync(status => Dispatcher.Invoke(() => SetStatus(status)));

            if (!result.Ok)
            {
                SetStatus(result.Error, isError: true);
                return;
            }
            if (result.Profiles.Count == 0)
            {
                SetStatus(Localizer.Tr("这个账号还没有创建角色，请先在 LittleSkin 网站建一个。"), isError: true);
                return;
            }

            if (App.Services.Accounts is not RealAccountService accounts)
            {
                SetStatus(Localizer.Tr("这一版还不支持把第三方角色写进账户表。"), isError: true);
                return;
            }

            Account? last = null;
            foreach (var profile in result.Profiles)
            {
                last = accounts.AddThirdParty(profile.Name, profile.Uuid,
                    result.Token?.AccessToken ?? string.Empty,
                    result.Token?.RefreshToken ?? string.Empty, "littleskin");
            }
            accounts.Select(last);
            CreatedAccount = last;
            DialogResult = last is not null;
        }
        catch (Exception ex)
        {
            SetStatus(Localizer.Tr("登录失败：{err}", ("err", ex.Message)), isError: true);
        }
        finally
        {
            _busy = false;
            WebLoginButton.IsEnabled = true;
        }
    }

    // ============================================================
    //  离线
    // ============================================================

    /// <summary>
    /// 三个 UUID 标准共用：只有选中「自定义」才显示输入框（选了别的就收起来，
    /// 不然会留下一个空的输入框让人以为要填）。
    /// </summary>
    private void OnUuidStandardChecked(object sender, RoutedEventArgs e)
    {
        if (CustomUuidPanel is null)
        {
            return;
        }
        var custom = UuidCustomRadio.IsChecked == true;
        CustomUuidPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        if (custom)
        {
            CustomUuidBox.Focus();
        }
    }

    private void CreateOfflineAccount()
    {
        var name = PlayerIdBox.Text.Trim();
        if (name.Length == 0)
        {
            SetStatus(Localizer.Tr("请先填玩家名"), isError: true);
            return;
        }
        if (name.Length > 16)
        {
            SetStatus(Localizer.Tr("玩家名最长 16 个字符"), isError: true);
            return;
        }
        if (name.Any(char.IsControl) || name.Contains('"'))
        {
            SetStatus(Localizer.Tr("玩家名里有不能用的字符"), isError: true);
            return;
        }

        string uuid;
        if (UuidCustomRadio.IsChecked == true)
        {
            uuid = NormalizeUuid(CustomUuidBox.Text);
            if (uuid.Length == 0)
            {
                SetStatus(Localizer.Tr("UUID 格式不对（32 位十六进制，可带连字符）"), isError: true);
                return;
            }
        }
        else if (UuidLegacyRadio.IsChecked == true)
        {
            uuid = Mosslight.Core.Services.Engine.LaunchRules.OfflineUuidLegacy(name);
        }
        else
        {
            uuid = Mosslight.Core.Services.Engine.LaunchRules.OfflineUuid(name);
        }

        try
        {
            CreatedAccount = App.Services.Accounts is RealAccountService real
                ? real.AddOffline(name, uuid)
                : App.Services.Accounts.AddOffline(name);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            SetStatus(Localizer.Tr("建档案失败：{err}", ("err", ex.Message)), isError: true);
        }
    }

    private static string NormalizeUuid(string text)
    {
        var clean = new string(text.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        if (clean.Length != 32)
        {
            return string.Empty;
        }
        return $"{clean[..8]}-{clean[8..12]}-{clean[12..16]}-{clean[16..20]}-{clean[20..]}";
    }
}
