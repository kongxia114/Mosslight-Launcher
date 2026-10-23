using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Mosslight.App.Infrastructure;
using Mosslight.Core.Localization;
using Mosslight.Core.Theme;

// ⚠️ 「主题模式」这个名字同时被 Core 和 WPF 占着（System.Windows.ThemeMode 是 .NET 9 之后
// 才有的，指"应用/系统主题"）。这里全程只认启动器自己那套，所以给 Core 的取个别名。
using CoreThemeMode = Mosslight.Core.Theme.ThemeMode;

namespace Mosslight.App.Views;

/// <summary>「个性化」页：主题 / 主题色 / 背景与动效 / 界面语言四张卡片。</summary>
public partial class PersonalizeSection : UserControl
{
    /// <summary>
    /// 深浅模式：(配置里存的值, 中文源文案)；⚠️ 顺序和真实主题的 ThemeMode 枚举**不一样**：界面里
    /// 「跟随系统 / 深色 / 浅色」是给人读的顺序，不是枚举定义顺序。
    /// </summary>
    private static readonly (string Key, string Text, CoreThemeMode Mode)[] ThemeModes =
    {
        ("system", "跟随系统", CoreThemeMode.System),
        ("dark", "深色", CoreThemeMode.Dark),
        ("light", "浅色", CoreThemeMode.Light),
    };

    // 预设主题色：(十六进制, 中文源文案)。
    private static readonly (string Value, string Name)[] PresetColors =
    {
        ("", "默认"),
        ("#3b6ea5", "青金石"),
        ("#3fbfb0", "钻石"),
        ("#4caf50", "仙人掌"),
        ("#d9a03a", "金锭"),
        ("#a06bd6", "紫水晶"),
        ("#c94f4f", "红石"),
    };

    // 预设色块的选中环颜色取自调色板的 text_dim。
    private static readonly Brush SelectionRing = new SolidColorBrush(Color.FromRgb(0xA0, 0xA1, 0xA7));

    private readonly List<ToggleButton> _swatches = new();
    private readonly Slider[] _sliders;
    private readonly TextBlock[] _valueLabels;

    // 「正在按配置回填界面」。
    private bool _syncing;

    // 「这一页马上要被重建」。
    private bool _rebuildPending;

    private MainWindow? Host
        => Window.GetWindow(this) as MainWindow
           ?? (Application.Current?.MainWindow as MainWindow);

    public PersonalizeSection()
    {
        InitializeComponent();

        _sliders = new[] { RedSlider, GreenSlider, BlueSlider };
        _valueLabels = new[] { RedValue, GreenValue, BlueValue };

        BuildSwatches();
        FillThemeCombo();
        FillLanguageCombo();

        // 事件在码后面接：上面几次回填（构造期）不该触发任何写配置的动作
        ThemeCombo.SelectionChanged += OnThemeSelected;
        LanguageCombo.SelectionChanged += OnLanguageSelected;

        SyncFromSettings();


    }




    // 建控件

    /// <summary>预设色块 + 名字。</summary>
    private void BuildSwatches()
    {
        for (var i = 0; i < PresetColors.Length; i++)
        {
            var (value, name) = PresetColors[i];

            var swatch = new ToggleButton
            {
                Style = (Style)FindResource("PersonalizeSwatch"),
                // Tag 兼两个用途：模板里取它当选中环的颜色；这里再放配置值给点击事件用。
                // ⚠️ 所以环色用**固定**的手挑灰（深色主题的 text_dim）—— 跟 Python 一样：
                // 环的颜色不需要跟着强调色变，跟着变反而会在浅色块上看不清。
                Tag = SelectionRing,
            };
            swatch.Click += (_, _) => ApplyAccent(value);
            _swatches.Add(swatch);

            var cell = new StackPanel { Orientation = Orientation.Horizontal };
            cell.Children.Add(swatch);
            cell.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("FieldLabel"),
                Text = Localizer.Tr(name),
                VerticalAlignment = VerticalAlignment.Center,
                // QHBoxLayout 的 spacing: 7
                Margin = new Thickness(7, 0, 0, 0),
                ToolTip = Localizer.Tr(name),
            });

            Grid.SetRow(cell, i / 4);
            Grid.SetColumn(cell, i % 4);
            // QGridLayout 的 horizontalSpacing: 14 —— 列间距靠给格子右边留白来等价实现
            // （WPF 的 Grid 没有 spacing 属性，Auto 列上留 Margin 效果一样）
            if (i % 4 != 3)
            {
                cell.Margin = new Thickness(0, 0, 14, 0);
            }
            if (i / 4 == 0)
            {
                // QGridLayout 的 verticalSpacing: 8
                cell.Margin = new Thickness(cell.Margin.Left, 0, cell.Margin.Right, 8);
            }
            SwatchGrid.Children.Add(cell);
        }
    }

    /// <summary>深浅模式下拉框。</summary>
    private void FillThemeCombo()
    {
        foreach (var (_, text, _) in ThemeModes)
        {
            ThemeCombo.Items.Add(new ComboBoxItem { Content = Localizer.Tr(text) });
        }

        var index = Array.FindIndex(ThemeModes, m => m.Mode == App.Settings.Theme);
        ThemeCombo.SelectedIndex = index >= 0 ? index : 0;
    }

    /// <summary>语言下拉框；⚠️ 这里**不能**改成"枚举 exe 旁边的 Assets\lang 目录"。</summary>
    private void FillLanguageCombo()
    {
        var languages = Localizer.AvailableLanguages();
        foreach (var code in languages)
        {
            LanguageCombo.Items.Add(new ComboBoxItem { Content = Localizer.LanguageName(code), Tag = code });
        }

        var index = languages.ToList().IndexOf(Localizer.CurrentLanguage);
        LanguageCombo.SelectedIndex = index >= 0 ? index : 0;
    }

    // 回填

    /// <summary>把界面同步成配置里的样子。</summary>
    private void SyncFromSettings()
    {
        _syncing = true;
        try
        {
            SyncSwatches();
            SyncSliders();
            RefreshCurrentAccent();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// 色块的勾选状态 + 显示色；⚠️ 「默认」那一块显示的是**主题自带的强调色**（配置里是空串，没有
    /// 颜色可显示），所以显示色得从当前刷子里现取 —— 换主题时这里要重跑一遍，否则切到浅色主题后那
    /// 个圆点还是深色主题的绿。
    /// </summary>
    private void SyncSwatches()
    {
        var accent = App.Settings.AccentColor;
        var fallback = ThemeDefaultAccent();

        for (var i = 0; i < _swatches.Count; i++)
        {
            var value = PresetColors[i].Value;
            var shown = value.Length > 0 ? ThemeApplier.ParseColor(value) : fallback;
            _swatches[i].IsChecked = string.Equals(value, accent, StringComparison.OrdinalIgnoreCase);
            _swatches[i].Background = new SolidColorBrush(shown);
        }
    }

    private static Color ThemeDefaultAccent()
    {
        var palette = ThemeService.Palette(ThemeApplier.CurrentMode);
        return palette.TryGetValue("accent", out var hex) && hex.Length > 0
            ? ThemeApplier.ParseColor(hex)
            // 兜底：调色板里居然没有 accent 这一项（不该发生）。退回当前刷子，
            // 至少颜色是个能看的，不会抛异常。
            : ThemeApplier.ColorOfBrush("AccentBrush");
    }

    /// <summary>
    /// 卡片顶部那块「当前主题色」：一个圆点 + 名字，**不是按钮**（用户明确要求只显示）。
    /// </summary>
    private void RefreshCurrentAccent()
    {
        var accent = App.Settings.AccentColor;
        Color color;
        string name;

        if (accent.Length > 0)
        {
            color = ThemeApplier.ParseColor(accent);
            var hit = Array.Find(PresetColors,
                p => string.Equals(p.Value, accent, StringComparison.OrdinalIgnoreCase));
            // 命中了预设就报预设名（青金石 / 钻石 …），否则就是用户自己拖出来的颜色
            name = hit.Name is null ? Localizer.Tr("自定义") : Localizer.Tr(hit.Name);
        }
        else
        {
            color = ThemeDefaultAccent();
            name = Localizer.Tr("默认");
        }

        CurrentAccentDot.Background = new SolidColorBrush(color);
        CurrentAccentName.Text = name;
        CurrentAccentName.ToolTip = $"#{color.R:x2}{color.G:x2}{color.B:x2}";
    }

    /// <summary>三条滑块 + 自定义预览。</summary>
    private void SyncSliders()
    {
        var accent = App.Settings.AccentColor;
        // ⚠️ 必须判空串：ParseColor("") 会在这里炸掉（配置里 accent_color 的「默认」就是空串）
        var themeAccent = ThemeApplier.ColorOfBrush("AccentBrush");
        var baseColor = accent.Length > 0 ? ThemeApplier.ParseColor(accent) : themeAccent;

        _sliders[0].Value = baseColor.R;
        _sliders[1].Value = baseColor.G;
        _sliders[2].Value = baseColor.B;
        for (var i = 0; i < _sliders.Length; i++)
        {
            _valueLabels[i].Text = ((int)_sliders[i].Value).ToString();
        }

        // 预览圆跟着当前主题的强调色走，但描边固定用主题那圈手挑值
        CustomPreview.Background = new SolidColorBrush(CurrentSliderColor());
        CustomPreview.BorderBrush = (Brush)FindResource("BorderHoverBrush");
    }

    // 事件：主题 / 主题色

    /// <summary>换深浅模式：写配置 → 重刷全部 token（界面立刻变色，不重建页面）。</summary>
    private void OnThemeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _rebuildPending)
        {
            return;
        }
        var index = ThemeCombo.SelectedIndex;
        if (index < 0 || index >= ThemeModes.Length)
        {
            return;
        }

        var mode = ThemeModes[index].Mode;
        if (mode == App.Settings.Theme)
        {
            return;
        }

        App.Settings.Theme = mode;
        ThemeApplier.Apply(App.Settings);
        // 主题换了 = 界面底色换了，「默认」色块显示的颜色也得跟着换
        SyncSwatches();
        RefreshCurrentAccent();

        // ⚠️ 背景蒙层的颜色是**运行时**从页面色取的（深色主题压暗、浅色主题提亮），
        // 不重跑一遍的话，切到浅色之后蒙层还是黑的 —— 那正是"浅色模式下
        // 图上的字看着不舒服"的根源。
        Host?.ApplyBackgroundSettings();
    }

    /// <summary>点预设色块：写配置 → 重刷。</summary>
    private void ApplyAccent(string value)
    {
        if (_rebuildPending)
        {
            return;
        }

        App.Settings.AccentColor = value;
        ThemeApplier.Apply(App.Settings);

        _syncing = true;
        try
        {
            SyncSwatches();
            SyncSliders();
            RefreshCurrentAccent();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>拖 RGB 滑块。</summary>
    private void OnRgbChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || _rebuildPending)
        {
            return;
        }

        var color = CurrentSliderColor();
        for (var i = 0; i < _sliders.Length; i++)
        {
            _valueLabels[i].Text = ((int)_sliders[i].Value).ToString();
        }
        CustomPreview.Background = new SolidColorBrush(color);

        // 拖动时先取消预设的选中，手感上更符合直觉（Python 的 `_on_rgb_changed` 同款）
        foreach (var swatch in _swatches)
        {
            swatch.IsChecked = false;
        }

        App.Settings.AccentColor = ColorText(color);
        ThemeApplier.Apply(App.Settings);
        RefreshCurrentAccent();
    }

    /// <summary>三条滑块当前合成的颜色。</summary>
    private Color CurrentSliderColor() => Color.FromRgb(
        (byte)Math.Round(_sliders[0].Value),
        (byte)Math.Round(_sliders[1].Value),
        (byte)Math.Round(_sliders[2].Value));

    /// <summary>`#rrggbb`。</summary>
    private static string ColorText(Color color) => $"#{color.R:x2}{color.G:x2}{color.B:x2}";

    // 事件：语言

    /// <summary>
    /// 切语言：写配置 → 换词典 → 重建本页；⚠️ 重建是**必须**的：XAML 里的 `{loc:Tr …}` 只在建控件
    /// 那一刻求值一次，已建好的控件不会自己变。
    /// </summary>
    private void OnLanguageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _rebuildPending)
        {
            return;
        }
        if (LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string code })
        {
            return;
        }
        if (string.Equals(code, Localizer.CurrentLanguage, StringComparison.Ordinal))
        {
            return;
        }

        _rebuildPending = true;
        var host = Host;
        if (host is null)
        {
            // 这一页没挂在主窗口上（被单独塞进别的宿主 / 自动化里）：只换词典，
            // 文字等下次建页面时自然就是新语言
            App.Settings.Language = code;
            Localizer.SetLanguage(code);
            _rebuildPending = false;
            return;
        }

        host.ApplyLanguage(code);
    }

    // 事件：背景 / 动效

    /// <summary>
    /// 「背景设置…」：弹 BackgroundSettingsDialog（背景图 / 铺法 / 纯色 / 压暗 / 卡片透明度，改完
    /// 立刻生效）。
    /// </summary>
    private void OnBackgroundSettings(object sender, RoutedEventArgs e)
        => OpenBackgroundSettings(keepForScreenshot: false);

    /// <summary>「动效设置…」：弹 AnimSettingsDialog（7 档预设 + 5 档速度 + 实时预览）。</summary>
    private void OnAnimationSettings(object sender, RoutedEventArgs e)
        => OpenAnimSettings(null, null, keepForScreenshot: false);

    /// <summary>离屏截图时打开的那个对话框实例（`--page personalize:anim` 建的）。</summary>
    public Window? ActiveDialog { get; private set; }

    /// <summary>切到本页的某个子段。</summary>
    public void ShowSection(string key)
    {
        var parts = key.Split(':');
        if (parts.Length == 0)
        {
            return;
        }

        // `bg` = 打开背景设置对话框再截（离屏截图点不了鼠标，没这个入口的话
        // 那个对话框永远没人验过 —— 跟 `anim`、`download:loader:forge` 一个思路）
        if (string.Equals(parts[0], "bg", StringComparison.Ordinal))
        {
            OpenBackgroundSettings(keepForScreenshot: App.ScreenshotMode);
            return;
        }

        if (!string.Equals(parts[0], "anim", StringComparison.Ordinal))
        {
            return;
        }

        var preset = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
        var speed = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null;
        OpenAnimSettings(preset, speed, keepForScreenshot: App.ScreenshotMode);
    }

    /// <summary>
    /// 建并打开背景设置对话框；⚠️ 截图模式下**不 Show**（理由同 OpenAnimSettings：离屏截图不闪窗，
    /// 而且 `Owner` 只能指向已经显示过的窗口）。
    /// </summary>
    private void OpenBackgroundSettings(bool keepForScreenshot)
    {
        var dialog = new BackgroundSettingsDialog();

        if (keepForScreenshot)
        {
            ActiveDialog = dialog;
            return;
        }

        var owner = Window.GetWindow(this);
        if (owner is not null && owner.IsLoaded)
        {
            // ⚠️ Owner 必须给：对话框改完靠 `Owner as MainWindow` 回调主窗口重画背景
            dialog.Owner = owner;
        }
        dialog.ShowDialog();
    }

    /// <summary>建并打开动效设置对话框。</summary>
    private void OpenAnimSettings(string? presetKey, string? speedKey, bool keepForScreenshot)
    {
        var dialog = new AnimSettingsDialog(presetKey, speedKey);

        if (keepForScreenshot)
        {
            ActiveDialog = dialog;
            return;
        }

        var owner = Window.GetWindow(this);
        if (owner is not null && owner.IsLoaded)
        {
            dialog.Owner = owner;
        }
        dialog.ShowDialog();
    }
}
