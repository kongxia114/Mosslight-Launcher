using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Mosslight.App.Infrastructure;
using Mosslight.Core.Localization;
using Mosslight.Core.Theme;

// ⚠️ 「主题模式」这个名字同时被 Core 和 WPF 占着（System.Windows.ThemeMode 是 .NET 9 之后
//    才有的，指"应用/系统主题"）。这里全程只认启动器自己那套，所以给 Core 的取个别名。
using CoreThemeMode = Mosslight.Core.Theme.ThemeMode;

namespace Mosslight.App.Views;

/// <summary>
/// 「个性化」页：主题 / 主题色 / 背景与动效 / 界面语言 四张卡片。
///
/// 照搬 1.0.0 的 <c>ui/pages/personalize_page.py</c>。那边的做法是「改配置 → 重新生成
/// 整份样式表 → setStyleSheet()」，这边一一对应成「改配置 → ThemeApplier.Apply() →
/// 48 个 token 全部重刷」，都是**不重建页面**就立刻变色。
///
/// ## 为什么改完颜色不用重建页面
/// 页面上所有颜色都是 <c>{DynamicResource XxxBrush}</c>，ThemeApplier 把新刷子写进
/// <c>Application.Current.Resources</c> 之后 WPF 自己会通知到每个引用点。
///
/// ## 语言是唯一的例外
/// ⚠️ 已经建好的控件不会自己变文字（1.0.0 也一样，那边靠 TranslatableWidget 重新贴一遍）。
/// 这里按 2.0 的原型取舍：切完语言把本页整个重建（<see cref="RebuildPage"/>）。
/// </summary>
public partial class PersonalizePage : UserControl, IMosslightPage
{
    /// <summary>
    /// 深浅模式：(配置里存的值, 中文源文案)。
    ///
    /// ⚠️ 顺序和真实主题的 ThemeMode 枚举**不一样**（Python 那边也是这个顺序）：
    ///    界面里「跟随系统 / 深色 / 浅色」是给人读的顺序，不是枚举定义顺序。
    /// </summary>
    private static readonly (string Key, string Text, CoreThemeMode Mode)[] ThemeModes =
    {
        ("system", "跟随系统", CoreThemeMode.System),
        ("dark", "深色", CoreThemeMode.Dark),
        ("light", "浅色", CoreThemeMode.Light),
    };

    /// <summary>
    /// 预设主题色：(十六进制, 中文源文案)。**一字不差**照抄 Python 的 PRESET_COLORS。
    /// 第一个是空串 = 用主题自带的那套手挑颜色（不是「没有颜色」）。
    /// </summary>
    private static readonly (string Value, string Name)[] PresetColors =
    {
        ("", "默认"),
        ("#3b6ea5", "青金石"),
        ("#3fbfb0", "钻石"),
        ("#4caf50", "仙人掌"),
        ("#d9a03a", "金锭"),
        ("#96603a", "泥土"),
        ("#c94f4f", "红石"),
    };

    /// <summary>预设色块的选中环颜色取自调色板的 text_dim（Python 里就是 `pal["text_dim"]`）。</summary>
    private static readonly Brush SelectionRing = new SolidColorBrush(Color.FromRgb(0xA0, 0xA1, 0xA7));

    private readonly List<ToggleButton> _swatches = new();
    private readonly Slider[] _sliders;
    private readonly TextBlock[] _valueLabels;

    /// <summary>
    /// 「正在按配置回填界面」。
    /// ⚠️ 必须有：回填滑块时会给 Slider.Value 赋值，那会触发 ValueChanged，
    ///    不挡一下就会把「预设色的选中」误取消、还会把配置反复写盘（1.0.0 的 `_loading` 同理）。
    /// </summary>
    private bool _syncing;

    /// <summary>
    /// 「这一页马上要被重建」。
    /// ⚠️ 切语言会重建页面，重建过程中下拉框还会再抛一次 SelectionChanged；
    ///    不挡住就会递归重建（1.0.0 那边切换语言只重贴文字，没这个问题）。
    /// </summary>
    private bool _rebuildPending;

    /// <summary>
    /// 主窗口。**每次现找**，不要在构造时抓。
    ///
    /// ⚠️ 2026-10 用户实测「切 English 没反应」就是这里：MainWindow 是**先 new 页面、
    ///    再塞进 `PageHost.Content`**，构造那一刻这一页还没挂进可视树，
    ///    `Window.GetWindow(this)` 返回 null；抓下来存成 readonly 字段就永远是 null，
    ///    于是 `RebuildPage()` 每次都"拿不到主窗口"直接返回 —— 下拉框显示 English，
    ///    界面还是中文。切页时页面会重建，所以这里每次用的时候现找就行。
    /// </summary>
    private MainWindow? Host
        => Window.GetWindow(this) as MainWindow
           ?? (Application.Current?.MainWindow as MainWindow);

    public PersonalizePage()
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

    /// <summary>
    /// 这一页**不做**入场动画。
    ///
    /// ⚠️ 核对过 1.0.0：入场动画只挂在**列表行**上（`collapsible_group.py` 的
    ///    `_stagger_for_rows`、`mods_page.py` 的 `_stagger_for`）。`personalize_page.py`
    ///    的四张卡片是普通 QFrame 直接进 QVBoxLayout，一张都没有挂过 SlideIn ——
    ///    给它们加「依次滑入」是照搬里最容易犯的那种"顺手加戏"。
    ///    所以这里返回 null（返回 Root 就会让主窗口把四张卡片错峰滑进来）。
    /// </summary>
    public Panel? AnimationHost => null;

    // ---------- 建控件 ----------

    /// <summary>
    /// 预设色块 + 名字。位置照抄 Python：`grid.addWidget(cell, index // 4, index % 4)`
    /// —— 一行四个，每个格子里是「色块 + 间距 7 + 名字」。
    /// </summary>
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
                //    环的颜色不需要跟着强调色变，跟着变反而会在浅色块上看不清。
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

    /// <summary>深浅模式下拉框。所有文案过 tr()（Python 的 `_fill_theme_combo` 同样处理）。</summary>
    private void FillThemeCombo()
    {
        foreach (var (_, text, _) in ThemeModes)
        {
            ThemeCombo.Items.Add(new ComboBoxItem { Content = Localizer.Tr(text) });
        }

        var index = Array.FindIndex(ThemeModes, m => m.Mode == App.Settings.Theme);
        ThemeCombo.SelectedIndex = index >= 0 ? index : 0;
    }

    /// <summary>
    /// 语言下拉框。语言名按**该语言自己**写，所以不过 tr()（1.0.0 的 i18n.language_name 也是）。
    ///
    /// ⚠️ 这里**不能**改成"枚举 exe 旁边的 Assets\lang 目录"。发布产物里只有一个
    ///    exe、那个目录不存在，枚举出来会只剩兜底的中文 —— 而"能切 5 种语言"正是
    ///    要保住的功能。<see cref="Localizer.AvailableLanguages"/> 现在返回的是
    ///    **内嵌语言 ∪ 磁盘上多出来的**：内嵌那份保证 5 种都在，磁盘上多出来的
    ///    是用户自己加的（覆盖或新增），一样要列出来让用户选得到。
    /// </summary>
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

    // ---------- 回填 ----------

    /// <summary>
    /// 把界面同步成配置里的样子（Python 的 `_refresh_all`）。
    /// </summary>
    private void SyncFromSettings()
    {
        _syncing = true;
        try
        {
            SyncSwatches();
            SyncSliders();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// 色块的勾选状态 + 显示色。
    ///
    /// ⚠️ 「默认」那一块显示的是**主题自带的强调色**（配置里是空串，没有颜色可显示），
    ///    所以显示色得从当前刷子里现取 —— 换主题时这里要重跑一遍，否则切到浅色主题后
    ///    那个圆点还是深色主题的绿。
    /// </summary>
    private void SyncSwatches()
    {
        var accent = App.Settings.AccentColor;
        var fallback = ThemeApplier.ColorOfBrush("AccentBrush");

        for (var i = 0; i < _swatches.Count; i++)
        {
            var value = PresetColors[i].Value;
            var shown = value.Length > 0 ? ThemeApplier.ParseColor(value) : fallback;
            _swatches[i].IsChecked = string.Equals(value, accent, StringComparison.OrdinalIgnoreCase);
            _swatches[i].Background = new SolidColorBrush(shown);
        }
    }

    /// <summary>
    /// 三条滑块 + 自定义预览。
    /// 有自定义色就用它，否则显示当前主题的默认强调色（Python 的 `_refresh_all` 后半段）。
    /// </summary>
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

    // ---------- 事件：主题 / 主题色 ----------

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
    }

    /// <summary>
    /// 点预设色块：写配置 → 重刷。
    /// 空串 = 回到主题自带的那套手挑颜色（不是「取消颜色」）。
    /// </summary>
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
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// 拖 RGB 滑块。
    ///
    /// 1.0.0 在这里加了 120ms 防抖（拖一次能刷上百遍 Qt 样式表）。
    /// WPF 这边**不需要**：改的是资源里的刷子，代价远小于重新生成整份 QSS；
    /// 而且按需求要「跟着滑块动」，所以每一格都直接生效。
    /// </summary>
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
    }

    /// <summary>三条滑块当前合成的颜色。</summary>
    private Color CurrentSliderColor() => Color.FromRgb(
        (byte)Math.Round(_sliders[0].Value),
        (byte)Math.Round(_sliders[1].Value),
        (byte)Math.Round(_sliders[2].Value));

    /// <summary>`#rrggbb`（小写，跟 Python 的 `QColor.name()` 一致，配置里存的就是这个形状）。</summary>
    private static string ColorText(Color color) => $"#{color.R:x2}{color.G:x2}{color.B:x2}";

    // ---------- 事件：语言 ----------

    /// <summary>
    /// 切语言：写配置 → 换词典 → 重建本页。
    ///
    /// ⚠️ 重建是**必须**的：XAML 里的 `{loc:Tr …}` 只在建控件那一刻求值一次，
    ///    已建好的控件不会自己变（1.0.0 靠 TranslatableWidget 重新贴一遍文字，2.0 没那套）。
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

        // 侧边栏 / 档案区是**构造时贴一次**的文字，只重建本页会让它们停在旧语言
        // （用户报的"语言系统坏了"有一半是这里）。ApplyLanguage 一次做完：
        // 写配置 → 换词典 → 重贴主窗口 chrome → 重建当前页。
        host.ApplyLanguage(code);
    }

    /// <summary>
    /// 让主窗口把本页重新建一遍。
    /// 拿不到主窗口时（比如本页被单独塞进别的宿主）退化成只改语言不改文字 ——
    /// 页面不该因为「重建不了」整个崩掉。
    /// </summary>
    private void RebuildPage()
    {
        var host = Host;
        if (host is null)
        {
            _rebuildPending = false;
            return;
        }
        host.ShowPage("personalize");
    }

    // ---------- 事件：背景 / 动效 ----------

    /// <summary>
    /// 「背景设置…」。1.0.0 那边会弹 AppearanceSettingsDialog —— 2.0 还没搬。
    /// ⚠️ 所以这里**明说没做**，不要做成一个看着能弹窗、点下去毫无反应的按钮。
    /// </summary>
    private void OnBackgroundSettings(object sender, RoutedEventArgs e)
        => ShowNotMigrated("「背景设置」对话框还没搬过来（1.0.0 里是背景图 / 铺法 / 压暗 / 卡片透明度那一整页）。");

    /// <summary>
    /// 「动效设置…」：弹 1.0.0 的 AnimSettingsDialog（7 档入场预设 + 5 档速度 + 实时预览）。
    /// 对话框自己负责落盘（选中即生效），关掉就完了，这里不用再回写什么。
    /// </summary>
    private void OnAnimationSettings(object sender, RoutedEventArgs e)
        => OpenAnimSettings(null, null, keepForScreenshot: false);

    /// <summary>
    /// 离屏截图时打开的那个对话框实例（`--page personalize:anim` 建的）。
    /// ⚠️ 只有在 <see cref="App.ScreenshotMode"/> 下才有值：那种模式下对话框**不 Show**
    ///    （见 <see cref="OpenAnimSettings"/>），得留个引用让 App 去渲染它。
    /// </summary>
    public Window? ActiveDialog { get; private set; }

    /// <summary>
    /// 切到本页的某个子段。**公开**是为了自动化截图能直接截到"页面里点出来的东西" ——
    /// `--page personalize:anim` 就是"打开动效设置对话框再截"，离屏截图点不了鼠标，
    /// 不给个入口的话对话框永远没人验过（跟下载页 `download:loader:forge` 一个思路）。
    ///
    /// 认的 key：
    /// · `anim`                         打开对话框，用配置里存的那一档
    /// · `anim:&lt;预设 key&gt;`              打开对话框并先切到那一档
    /// · `anim:&lt;预设 key&gt;:&lt;速度 key&gt;`   再顺带切速度档
    ///   （"换一档之后长什么样"是点出来的，所以这两个子段是必需的）
    /// </summary>
    public void ShowSection(string key)
    {
        var parts = key.Split(':');
        if (parts.Length == 0 || !string.Equals(parts[0], "anim", StringComparison.Ordinal))
        {
            return;
        }

        var preset = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
        var speed = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null;
        OpenAnimSettings(preset, speed, keepForScreenshot: App.ScreenshotMode);
    }

    /// <summary>
    /// 建并打开动效设置对话框。
    ///
    /// ⚠️ 截图模式下**不 Show**：
    ///   1. 离屏截图的前提就是"不闪窗"（见 App.SaveWindowPng 的注释），Show 到用户桌面上
    ///      就违背了；
    ///   2. `Window.Owner` 只能指向**已经显示过**的窗口，那种模式下主窗口也没 Show 过，
    ///      设了会直接抛 InvalidOperationException。
    ///    所以这条路上只是把实例建好（XAML 的排版由 App 渲染时 Measure/Arrange 补上）。
    /// </summary>
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

    private void ShowNotMigrated(string text)
    {
        NotMigratedText.Text = Localizer.Tr(text);
        NotMigratedText.Visibility = Visibility.Visible;
    }
}
