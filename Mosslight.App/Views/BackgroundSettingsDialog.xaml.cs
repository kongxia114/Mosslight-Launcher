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

/// <summary>「背景设置」对话框：背景图 / 铺法 / 纯色 / 压暗 / 卡片透明度。</summary>
public partial class BackgroundSettingsDialog : Window
{
    private readonly AppSettings _settings = App.Settings;

    // 正在按配置回填界面（见类注释最后一段）。
    private bool _loading;

    public BackgroundSettingsDialog()
    {
        // ⚠️ `_loading` 必须在 InitializeComponent 之前置位，否则 XAML 夹取 Minimum / Maximum 时会把默认值写进配置。
        _loading = true;
        InitializeComponent();
        Infrastructure.FramelessWindow.Apply(this);

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
        // 所以只能在这里设。Tr() 的实参是**字面量**，check_lang_keys.py 照样能提取到。
        // （注释里同样别写出花括号形态 —— 那个脚本是全文扫描的。）
        OpacityHint.Text = Localizer.Tr(
            "调小 → 卡片底色透出背景图（100% = 完全不透明）。文字和图标不受影响，只有卡片底色变透。");
    }

    // 回填

    private void SelectMode(string mode)
    {
        var index = BackgroundModes.Order.ToList().IndexOf(mode);
        ModeCombo.SelectedIndex = index >= 0 ? index : BackgroundModes.Order.ToList().IndexOf(BackgroundModes.Default);
    }

    // 当前选中的铺法 key（下拉项没建好时退回默认）。
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

    /// <summary>形如 #rrggbb / #aarrggbb 才算数（跟 core 的判断保持一致）。</summary>
    private static bool IsHex(string text) => Mosslight.Core.Theme.ColorMath.IsHex(text);

    // 事件

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        // ⚠️ WPF 的 `Filter` 语法是 `描述|通配符|描述|通配符`（**竖线**分隔）。
        // 照抄过来会在 `new OpenFileDialog { Filter = ... }` 直接抛 ArgumentException，
        // 而且是**构造时**抛：程序当场崩，连"点浏览没反应"都看不到。
        var images = Localizer.Tr("图片");
        var allFiles = Localizer.Tr("所有文件");
        var filter = $"{images}|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|{allFiles}|*.*";

        // ⚠️ 整段兜住：选背景图这种事**不能**把启动器带崩。
        // 真出事就写一条崩溃日志，然后当用户取消处理（对话框什么都不变）。
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

    // 生效

    private void Apply() => (Owner as MainWindow)?.ApplyBackgroundSettings();
}
