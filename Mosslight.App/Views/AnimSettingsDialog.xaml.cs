using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Mosslight.App.Infrastructure;
using Mosslight.Core.Animation;
using Mosslight.Core.Localization;
using Mosslight.Core.Settings;

namespace Mosslight.App.Views;

/// <summary>「动效设置」对话框。</summary>
public partial class AnimSettingsDialog : Window
{
    // 预览区里放几张示意卡片。
    private const int PreviewCount = 3;

    // 切换设置后稍等一下再播：连点几档时，上一条动画还没播完就又来一条，看着像"抽风"。
    private const int PreviewDelayMs = 220;

    // 预览收尾的宽限：动画跑完后主动 PageAnimator.Reset 一次。
    private const int PreviewSettleMarginMs = 150;

    // 「正在按配置回填界面」。
    private bool _syncing;

    // 预览调度器（单次；每次重播都换一个，省得手动管"该不该跑"）。
    private DispatcherTimer? _previewTimer;

    // 预览收尾定时器（见 PreviewSettleMarginMs）。
    private DispatcherTimer? _settleTimer;

    // 示意卡片左边那三条强调色 —— 换预设时要跟着换色。
    private readonly List<Border> _cardBars = new();

    public AnimSettingsDialog() : this(null, null)
    {
    }

    /// <summary>
    /// presetKey / speedKey 是**自动化截图**用的初值 （`--page personalize:anim:pcl_recipe`）：给
    /// 了就在开演之前先切过去，截到的就是那一档。
    /// </summary>
    public AnimSettingsDialog(string? presetKey, string? speedKey)
    {
        InitializeComponent();
        Infrastructure.FramelessWindow.Apply(this);

        BuildPresetList();
        BuildSpeedOptions();
        BuildPreviewCards();

        // 事件在码后面接：上面几次构造期回填不该触发任何写配置的动作
        // （XAML 里挂的两个是 CheckBox 的，那个没有构造期回填问题）
        PresetList.SelectionChanged += OnPresetSelected;

        // ⚠️ 截图初值必须排在 SyncFromSettings 之前，并做 key 白名单校正，避免回填错位或写脏配置。
        if (presetKey is not null)
        {
            ApplyPreset(AnimationPresets.Preset(presetKey).Key, replay: false);
        }
        if (speedKey is not null)
        {
            ApplySpeed(
                AnimationPresets.Speeds.ContainsKey(speedKey) ? speedKey : AnimationPresets.DefaultSpeed,
                replay: false);
        }

        SyncFromSettings();

        // 打开就先演一遍当前这一档（Python 的 `_preview_timer.start(PREVIEW_DELAY_MS)`）
        SchedulePreview(PreviewDelayMs);
    }

    // 建控件

    private void BuildPresetList()
    {
        foreach (var key in AnimationPresets.PresetOrder)
        {
            var preset = AnimationPresets.Preset(key);

            // 14px 圆角小方块（Python 的 `_swatch(color, size=14)`，圆角 4）
            var swatch = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
            };
            swatch.SetResourceReference(BackgroundProperty, PresetColorToken(key));

            var label = new TextBlock
            {
                // ⚠️ 预设表里的名字是**中文源文案**（Core 那层刻意不翻，见 AnimationPreset 的注释），
                // 翻不翻是界面层的事
                Text = Localizer.Tr(preset.Name),
                VerticalAlignment = VerticalAlignment.Center,
                // 色块和文字之间留 8px（QListWidget 图标与文字的默认间距）
                Margin = new Thickness(8, 0, 0, 0),
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(swatch);
            row.Children.Add(label);

            PresetList.Items.Add(new ListBoxItem
            {
                Content = row,
                // Python 的 `item.setToolTip(p.description)`
                ToolTip = Localizer.Tr(preset.Description),
                // 容器样式由 AnimPresetList 的 ItemContainerStyle 给（= QListWidget::item）
                Tag = key,
            });
        }
    }

    /// <summary>五档速度，竖着排（横着放不下）。</summary>
    private void BuildSpeedOptions()
    {
        foreach (var key in AnimationPresets.SpeedOrder)
        {
            var radio = new RadioButton
            {
                Style = (Style)FindResource("SpeedOption"),
                Content = Localizer.Tr(AnimationPresets.SpeedText(key)),
                GroupName = "AnimSpeedOption",
                Tag = key,
                // 只包住自己的文字，别横向撑开（Qt 那边 QRadioButton 的 sizePolicy
                // 就是 (Minimum, Fixed)，见 AnimSettingsDialog.xaml 里的说明）
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            radio.Checked += OnSpeedChecked;
            SpeedList.Children.Add(radio);
        }
    }

    /// <summary>预览区的三张示意卡片。</summary>
    private void BuildPreviewCards()
    {
        for (var i = 0; i < PreviewCount; i++)
        {
            var bar = new Border { Style = (Style)FindResource("AnimPreviewCardBar") };
            _cardBars.Add(bar);

            var label = new TextBlock
            {
                Text = Localizer.Tr("示意卡片 {index}", ("index", i + 1)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 12, 0),
                // Python 的 `font-size: 12px`（卡片自绘，没有样式可继承）
                FontSize = 12,
            };
            // 颜色走 DynamicResource（换主题 / 换强调色都跟着走），字号是死值所以直接给
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

            var inner = new Grid();
            inner.Children.Add(bar);
            inner.Children.Add(label);

            var card = new Border
            {
                Style = (Style)FindResource("AnimPreviewCard"),
                // Python 的 `preview_layout.setSpacing(8)`：等分格子之后靠下边距把间距让出来
                Margin = new Thickness(0, 0, 0, i == PreviewCount - 1 ? 0 : 8),
                Child = inner,
            };

            PreviewHost.Children.Add(card);
        }
    }

    // 回填

    /// <summary>把界面同步成配置里的样子。</summary>
    private void SyncFromSettings()
    {
        // ⚠️ 存/还原而不是直接置 false：这个方法会被 OnRestoreDefaults 套在另一个
        // "正在批量改配置"的区间里调用，无条件清掉会把外层的保护一起拆了。
        var previous = _syncing;
        _syncing = true;
        try
        {
            // ⚠️ 勾选框是"关闭"，配置里是"启用" —— 取反
            EnableCheck.IsChecked = !App.Settings.AnimationsEnabled;
            SelectPresetRow(App.Settings.AnimationPreset);
            SelectSpeedRadio(App.Settings.AnimationSpeed);
        }
        finally
        {
            _syncing = previous;
        }

        SyncEnabledState();
    }

    /// <summary>选中配置里那一档预设（认不出来时退回第一档，跟 Core 的容错一致）。</summary>
    private void SelectPresetRow(string key)
    {
        var index = 0;
        for (var i = 0; i < PresetList.Items.Count; i++)
        {
            if (PresetList.Items[i] is ListBoxItem { Tag: string tag }
                && string.Equals(tag, key, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }
        PresetList.SelectedIndex = index;
    }

    private void SelectSpeedRadio(string key)
    {
        foreach (var child in SpeedList.Children)
        {
            if (child is RadioButton { Tag: string tag } radio)
            {
                radio.IsChecked = string.Equals(tag, key, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>按总开关把风格 / 速度 / 预览按钮置灰，并写明当前是什么状态。</summary>
    private void SyncEnabledState()
    {
        var on = App.Settings.AnimationsEnabled;

        PresetList.IsEnabled = on;
        ReplayButton.IsEnabled = on;
        foreach (var child in SpeedList.Children)
        {
            if (child is RadioButton radio)
            {
                radio.IsEnabled = on;
            }
        }

        EnableHint.Text = on
            ? Localizer.Tr("关掉可以省性能：列表直接出现，不再滑入 / 淡入。这里选过的风格和速度都留着，随时可以再打开。")
            : Localizer.Tr("动效已关闭：列表直接出现，不滑入也不淡入 —— 省掉每张卡片的动画和离屏合成。取消勾选就恢复。");

        UpdateDetail();
    }

    /// <summary>选中那一档的说明（名称 / 一句话 / 方向·时长·错峰·位移）。</summary>
    private void UpdateDetail()
    {
        var on = App.Settings.AnimationsEnabled;

        DetailPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        DetailOff.Visibility = on ? Visibility.Collapsed : Visibility.Visible;

        if (!on)
        {
            SetMarkup(DetailOff, Localizer.Tr("<b>动效已关闭</b><br>列表直接出现，不滑入也不淡入。"));
            return;
        }

        var preset = AnimationPresets.Preset(App.Settings.AnimationPreset);
        var scaled = ScaledPreset();

        DetailName.Text = Localizer.Tr(preset.Name);
        DetailDesc.Text = Localizer.Tr(preset.Description);

        // ⚠️ 这句文案含 `<br>` 和全角空格，必须整句取用，不能自己拼。
        // “有回弹 / 无回弹”按轨迹是否冲过终点实测，不要用样式名判断。
        var rebound = AnimationRebound.Measure(scaled);
        SetMarkup(DetailMeta, Localizer.Tr(
            "方向：{direction}　时长：{duration}ms（原始 {raw}ms）　错峰：{interval}ms<br>位移：{offset}px　{bounce}",
            ("direction", Localizer.Tr(AnimationPresets.DirectionText(preset.Direction))),
            ("duration", scaled.DurationMs),
            ("raw", preset.DurationMs),
            ("interval", scaled.IntervalMs),
            ("offset", scaled.OffsetPx),
            ("bounce", Localizer.Tr(rebound.HasRebound ? "有回弹" : "无回弹"))));

        DetailStats.Text = StatsLine(preset, scaled, rebound);
    }

    /// <summary>详情里那行"量出来的"数字：过冲 / 峰值时刻 / 可见摆动 / 淡入时间点。</summary>
    private static string StatsLine(
        AnimationPreset preset, AnimationPreset scaled, ReboundMeasurement rebound)
    {
        var parts = new List<string>();

        // 过冲：没到可见阈值就明说"没有"，不要显示 "0.00px"（那会让人以为量错了）
        if (rebound.HasRebound)
        {
            parts.Add(Localizer.Tr("过冲 {px}px（{percent}%，峰值在第 {ms}ms）",
                ("px", rebound.OvershootPx.ToString("0.0")),
                ("percent", (preset.OffsetPx > 0
                    ? rebound.OvershootPx / preset.OffsetPx * 100 : 0).ToString("0.#")),
                ("ms", rebound.PeakTimeMs.ToString("0"))));
        }
        else
        {
            parts.Add(Localizer.Tr("无过冲"));
        }

        var timeline = SlideInTrajectory.Timeline(scaled);
        var fadeMs = preset.Style == AnimationStyle.Pcl
            ? timeline.FadeMs
            : timeline.TotalMs * preset.FadeRatio;
        parts.Add(Localizer.Tr("淡入在 {ms}ms 走完", ("ms", fadeMs.ToString("0"))));

        // 整批铺完：错峰 × (张数-1) + 单张总时长 —— 用户最直观的"这一档要多久"
        var totalMs = scaled.IntervalMs * (PreviewCount - 1) + timeline.TotalMs;
        parts.Add(Localizer.Tr("3 张示意卡片 {ms}ms 铺完", ("ms", totalMs.ToString("0"))));

        return string.Join("　·　", parts);
    }

    /// <summary>当前选中档 + 当前速度倍率，合成实际生效的参数。</summary>
    private AnimationPreset ScaledPreset() => AnimationPresets.Scaled(
        AnimationPresets.Preset(App.Settings.AnimationPreset),
        AnimationPresets.SpeedFactor(App.Settings.AnimationSpeed));

    // 事件：总开关 / 预设 / 速度

    /// <summary>「关闭动效」勾上 / 取消：落盘 + 把下面的选项一起置灰 + 重播预览。</summary>
    private void OnEnableToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }
        App.Settings.AnimationsEnabled = EnableCheck.IsChecked != true;
        SyncEnabledState();
        SchedulePreview(PreviewDelayMs);
    }

    /// <summary>点某档预设：选中即生效并落盘，然后重播预览。</summary>
    private void OnPresetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || PresetList.SelectedItem is not ListBoxItem { Tag: string key })
        {
            return;
        }
        ApplyPreset(key, replay: true);
    }

    private void ApplyPreset(string key, bool replay)
    {
        App.Settings.AnimationPreset = key;
        UpdateDetail();
        if (replay)
        {
            SchedulePreview(PreviewDelayMs);
        }
    }

    /// <summary>换速度档：同样即生效即落盘，并用**新速度**重播一次。</summary>
    private void OnSpeedChecked(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton { Tag: string key })
        {
            return;
        }
        ApplySpeed(key, replay: true);
    }

    private void ApplySpeed(string key, bool replay)
    {
        App.Settings.AnimationSpeed = key;
        UpdateDetail();
        if (replay)
        {
            SchedulePreview(PreviewDelayMs);
        }
    }

    /// <summary>
    /// 「恢复默认」：回到 AnimationPresets.DefaultPreset / 默认速度 / 开启动效；⚠️
    /// **只动这个对话框管得着的那三个键**，不碰 `anim_fade`：淡入开关是 「背景设置」对话框那一栏
    /// 的事，顺手恢复会把用户在另一个对话框里的设置抹掉。
    /// </summary>
    private void OnRestoreDefaults(object sender, RoutedEventArgs e)
    {
        // 三个键一起改完之后再回填一次（SyncFromSettings 自带"别把回填当用户操作"的保护）
        App.Settings.AnimationsEnabled = true;
        App.Settings.AnimationPreset = AnimationPresets.DefaultPreset;
        App.Settings.AnimationSpeed = AnimationPresets.DefaultSpeed;
        SyncFromSettings();
        SchedulePreview(PreviewDelayMs);
    }

    private void OnReplay(object sender, RoutedEventArgs e) => SchedulePreview(0);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // 实时预览

    /// <summary>过 delayMs 毫秒重播一遍预览。</summary>
    private void SchedulePreview(int delayMs)
    {
        _previewTimer?.Stop();
        _settleTimer?.Stop();

        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(0, delayMs)),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ReplayPreview();
        };
        _previewTimer = timer;
        timer.Start();
    }

    /// <summary>用当前设置（风格 + 速度 + 总开关）把预览区重演一遍。</summary>
    private void ReplayPreview()
    {
        var on = App.Settings.AnimationsEnabled;
        var preset = ScaledPreset();

        PlayingText.Text = on
            ? Localizer.Tr("正在演示：{name}", ("name", Localizer.Tr(preset.Name)))
            : Localizer.Tr("动效已关闭 —— 示意卡片直接显示，不滑不淡");

        var token = PresetColorToken(App.Settings.AnimationPreset);
        foreach (var bar in _cardBars)
        {
            bar.SetResourceReference(BackgroundProperty, token);
        }

        PageAnimator.Reset(PreviewHost);
        PageAnimator.AnimateIn(PreviewHost, App.Settings);

        if (!on)
        {
            return;
        }

        // 整批铺完要多久：错峰 × (张数-1) + **单张轨迹的总时长**。
        // ⚠️ 用轨迹引擎算的总时长，不是预设表里那个 duration：PCL 那档的
        // 慢轨是 max(fast+20, duration)，短时长下会比 duration 长 ——
        // 照 duration 算会把收尾定时器定早了，预览会停在半路。
        var totalMs = preset.IntervalMs * (PreviewCount - 1)
            + SlideInTrajectory.TotalDurationMs(preset);
        ScheduleSettle((int)Math.Ceiling(totalMs) + PreviewSettleMarginMs);
    }

    /// <summary>预览跑完把动画状态收干净（理由见 PreviewSettleMarginMs）。</summary>
    private void ScheduleSettle(int delayMs)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(delayMs),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            PageAnimator.Reset(PreviewHost);
        };
        _settleTimer = timer;
        timer.Start();
    }

    /// <summary>关窗时把两个定时器停掉 —— 不然它们会打到已经拆掉的视觉树上。</summary>
    protected override void OnClosed(EventArgs e)
    {
        _previewTimer?.Stop();
        _settleTimer?.Stop();
        base.OnClosed(e);
    }

    // 小工具

    /// <summary>
    /// 某一档预设的装饰色取哪个主题 token；⚠️ 色值一律**不写死**：从当前主题的调色板里取，所以浅
    /// 色主题和用户自定义的强调色都会跟着走。
    /// </summary>
    private static string PresetColorToken(string key) => key switch
    {
        "slide_left" => "AccentBrush",
        "slide_up" => "PackTextBrush",
        "slide_left_bounce" => "WarnBrush",
        "pcl_like" => "TextDimBrush",
        "slide_left_back" => "AccentTextBrush",
        "slide_up_elastic" => "DangerBrush",
        "pcl_recipe" => "PackBorderBrush",
        _ => "AccentBrush",
    };

    /// <summary>把带**极简 HTML 标记**的文案塞进 TextBlock。</summary>
    private static void SetMarkup(TextBlock target, string text)
    {
        target.Inlines.Clear();

        var bold = false;
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] == '<')
            {
                if (Match(text, index, "<b>")) { bold = true; index += 3; continue; }
                if (Match(text, index, "</b>")) { bold = false; index += 4; continue; }
                if (Match(text, index, "<br>")) { target.Inlines.Add(new LineBreak()); index += 4; continue; }
                if (Match(text, index, "<br/>")) { target.Inlines.Add(new LineBreak()); index += 5; continue; }
            }

            var next = text.IndexOf('<', index);
            if (next < 0)
            {
                next = text.Length;
            }
            target.Inlines.Add(new Run(text[index..next])
            {
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            });
            index = next;
        }
    }

    private static bool Match(string text, int index, string token)
        => index + token.Length <= text.Length
            && string.CompareOrdinal(text, index, token, 0, token.Length) == 0;
}
