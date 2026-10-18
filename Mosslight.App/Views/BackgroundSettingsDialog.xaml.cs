using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Mosslight.Core.Appearance;
using Mosslight.Core.Localization;
using Mosslight.Core.Settings;

namespace Mosslight.App.Views;

/// <summary>
/// 「背景设置」对话框：背景图 / 铺法 / 纯色 / 压暗 / 卡片透明度。
///
/// 移植自 1.0.0 的 <c>ui/dialogs/appearance_dialog.py</c>（只搬背景那一段 ——
/// 游戏目录和多线程在 2.0 里已经归「设置」页）。
///
/// ## 改完立刻生效
/// 每改一项就把值写进 <see cref="App.Settings"/>，然后让主窗口重画：
/// <c>MainWindow.ApplyBackgroundSettings()</c>（背景画布 + 卡片透明度）。
/// 这就是 1.0.0 的 `_save_and_apply`，不用点"确定"。
///
/// ## 为什么不用 SaveConfig()
/// `AppSettings` 里这几个字段的 setter **自带 <c>Save()</c>**（写一次 config.json 约 1KB），
/// 所以拖滑块时每一格都会落盘 —— 跟 1.0.0 的 `appearance.save_key()` 一个行为。
///
/// ## ⚠️ 初始化回填要挡住事件
/// 回填 <c>DimSlider.Value</c> / <c>ModeCombo.SelectedIndex</c> 会触发 ValueChanged /
/// SelectionChanged，不挡的话会把"刚读出来的值"又写回去一遍（还把铺法说明刷成空）。
/// </summary>
public partial class BackgroundSettingsDialog : Window
{
    /// <summary>全局配置。字段初始化器就地取（对话框总是在 App 起来之后才建）。</summary>
    private readonly AppSettings _settings = App.Settings;

    /// <summary>正在按配置回填界面（见类注释最后一段）。</summary>
    private bool _loading;

    public BackgroundSettingsDialog()
    {
        // ⚠️ 必须在 InitializeComponent **之前**置位。
        //    XAML 里 `Minimum` / `Maximum` 会立刻把 `Value` 夹到合法范围并触发
        //    ValueChanged（OpacitySlider 的 Minimum=10 → 0 被夹成 10），那时
        //    `_loading` 还是 false，处理器就会把这个"夹出来的默认值"写进配置 ——
        //    表现是"只是打开了一下背景设置，压暗和卡片透明度就被改了"。
        _loading = true;
        InitializeComponent();

        try
        {
            // 铺法下拉：顺序照 BackgroundModes.Order（对齐 Windows 的"选择适合度"）
            foreach (var key in BackgroundModes.Order)
            {
                ModeCombo.Items.Add(new ComboBoxItem
                {
                    Content = Localizer.Tr(BackgroundModes.Labels[key]),
                    Tag = key,
                });
            }

            PathBox.Text = _settings.BackgroundImage;
            ColorBox.Text = _settings.BackgroundColor;
            SelectMode(_settings.BackgroundMode);
            DimSlider.Value = Math.Clamp(_settings.BackgroundDim, 0, 200);
            OpacitySlider.Value = Math.Clamp(_settings.CardOpacity,
                BackgroundModes.MinCardOpacity, BackgroundModes.MaxCardOpacity);
        }
        finally
        {
            _loading = false;
        }

        RefreshModeHint();
        RefreshValueLabels();
        RefreshColorSwatch();

        // ⚠️ 这条文案里含 `=`，放进 XAML 的 loc:Tr 标记扩展会被当成"属性=值"（MC3045），
        //    所以只能在这里设。Tr() 的实参是**字面量**，check_lang_keys.py 照样能提取到。
        //    （注释里同样别写出花括号形态 —— 那个脚本是全文扫描的。）
        OpacityHint.Text = Localizer.Tr(
            "调小 → 卡片底色透出背景图（100% = 完全不透明）。文字和图标不受影响，只有卡片底色变透。");
    }

    // ---------- 回填 ----------

    private void SelectMode(string mode)
    {
        var index = BackgroundModes.Order.ToList().IndexOf(mode);
        ModeCombo.SelectedIndex = index >= 0 ? index : BackgroundModes.Order.ToList().IndexOf(BackgroundModes.Default);
    }

    /// <summary>当前选中的铺法 key（下拉项没建好时退回默认）。</summary>
    private string CurrentMode =>
        ModeCombo.SelectedItem is ComboBoxItem { Tag: string key } ? key : BackgroundModes.Default;

    private void RefreshModeHint()
        => ModeHint.Text = Localizer.Tr(BackgroundModes.Hints.TryGetValue(CurrentMode, out var hint) ? hint : "");

    private void RefreshValueLabels()
    {
        DimValue.Text = ((int)Math.Round(DimSlider.Value)).ToString();
        OpacityValue.Text = $"{(int)Math.Round(OpacitySlider.Value)}%";
    }

    /// <summary>纯色块：没填 / 填错就画成"空"（透明 + 描边），别画成黑色骗人。</summary>
    private void RefreshColorSwatch()
    {
        var hex = ColorBox.Text.Trim();
        ColorSwatch.Background = hex.Length > 0 && IsHex(hex)
            ? new SolidColorBrush(Mosslight.App.Infrastructure.ThemeApplier.ParseColor(hex))
            : Brushes.Transparent;
    }

    /// <summary>形如 <c>#rrggbb</c> / <c>#aarrggbb</c> 才算数（跟 core 的判断保持一致）。</summary>
    private static bool IsHex(string text) => Mosslight.Core.Theme.ColorMath.IsHex(text);

    // ---------- 事件 ----------

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        // ⚠️ WPF 的 `Filter` 语法是 `描述|通配符|描述|通配符`（**竖线**分隔）。
        // 设计说明见 docs/design-notes.md#mosslight-app-views-backgroundsettingsdialog-xaml-cs
        var images = Localizer.Tr("图片");
        var allFiles = Localizer.Tr("所有文件");
        var filter = $"{images}|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|{allFiles}|*.*";

        // ⚠️ 整段兜住：选背景图这种事**不能**把启动器带崩。
        //    真出事就写一条崩溃日志，然后当用户取消处理（对话框什么都不变）。
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = Localizer.Tr("选一张背景图"),
                Filter = filter,
                CheckFileExists = true,
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            PathBox.Text = dialog.FileName;
            CommitPath();
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("BackgroundSettingsDialog.OnBrowse", ex);
        }
    }

    private void OnPathCommitted(object sender, RoutedEventArgs e) => CommitPath();

    private void CommitPath()
    {
        if (_loading)
        {
            return;
        }
        _settings.BackgroundImage = PathBox.Text.Trim();
        Apply();
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        RefreshModeHint();
        _settings.BackgroundMode = CurrentMode;
        Apply();
    }

    private void OnColorCommitted(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        // 认不出来的值就当没填（核心那边 ColorMath.IsHex 也这么判），不要写进配置
        var hex = ColorBox.Text.Trim();
        _settings.BackgroundColor = IsHex(hex) ? hex : string.Empty;
        RefreshColorSwatch();
        Apply();
    }

    /// <summary>设计说明见 docs/design-notes.md#mosslight-app-views-backgroundsettingsdialog-xaml-cs</summary>
    private void OnPickColor(object sender, RoutedEventArgs e)
    {
        // 打开前把滑块对到当前值（没有当前值就给主题页面色）
        var start = IsHex(ColorBox.Text.Trim())
            ? Mosslight.App.Infrastructure.ThemeApplier.ParseColor(ColorBox.Text.Trim())
            : Mosslight.App.Infrastructure.ThemeApplier.ColorOfBrush("BgPageBrush");

        _loading = true;
        try
        {
            PickRSlider.Value = start.R;
            PickGSlider.Value = start.G;
            PickBSlider.Value = start.B;
        }
        finally
        {
            _loading = false;
        }

        RefreshPickedColor();
        ColorPopup.IsOpen = true;
    }

    /// <summary>取色层里任一滑块动了：更新数字 / 预览 / 输入框，并立刻生效。</summary>
    private void OnPickedColorChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }
        RefreshPickedColor();
        Apply();
    }

    private void RefreshPickedColor()
    {
        var color = Color.FromRgb(
            (byte)Math.Round(PickRSlider.Value),
            (byte)Math.Round(PickGSlider.Value),
            (byte)Math.Round(PickBSlider.Value));

        PickRValue.Text = color.R.ToString();
        PickGValue.Text = color.G.ToString();
        PickBValue.Text = color.B.ToString();
        PickPreview.Background = new SolidColorBrush(color);

        ColorBox.Text = $"#{color.R:x2}{color.G:x2}{color.B:x2}";
        _settings.BackgroundColor = ColorBox.Text;
        RefreshColorSwatch();
    }

    private void OnDimChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }
        DimValue.Text = ((int)Math.Round(e.NewValue)).ToString();
        _settings.BackgroundDim = (int)Math.Round(e.NewValue);
        Apply();
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }
        OpacityValue.Text = $"{(int)Math.Round(e.NewValue)}%";
        _settings.CardOpacity = (int)Math.Round(e.NewValue);
        Apply();
    }

    /// <summary>
    /// 恢复默认。
    /// ⚠️ 铺法取 <see cref="BackgroundModes.Default"/>，别写死 "fill"：
    ///    核心的默认是 center（1.0.0 那边 Python 注释里专门记过这个坑 ——
    ///    写死 fill 的话"恢复默认"恢复出来的根本不是默认值）。
    /// </summary>
    private void OnReset(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            PathBox.Text = string.Empty;
            ColorBox.Text = string.Empty;
            SelectMode(BackgroundModes.Default);
            DimSlider.Value = BackgroundModes.DefaultDim;
            OpacitySlider.Value = BackgroundModes.MaxCardOpacity;
        }
        finally
        {
            _loading = false;
        }

        _settings.BackgroundImage = string.Empty;
        _settings.BackgroundColor = string.Empty;
        _settings.BackgroundMode = BackgroundModes.Default;
        _settings.BackgroundDim = BackgroundModes.DefaultDim;
        _settings.CardOpacity = BackgroundModes.MaxCardOpacity;

        RefreshModeHint();
        RefreshValueLabels();
        RefreshColorSwatch();
        Apply();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ---------- 生效 ----------

    /// <summary>设计说明见 docs/design-notes.md#mosslight-app-views-backgroundsettingsdialog-xaml-cs</summary>
    private void Apply() => (Owner as MainWindow)?.ApplyBackgroundSettings();
}
