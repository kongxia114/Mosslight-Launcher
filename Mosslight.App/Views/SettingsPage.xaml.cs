using Mosslight.App.Infrastructure;
﻿using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Mosslight.Core.Localization;
using Mosslight.Core.Services;
using Mosslight.Core.Services.Engine;
using Mosslight.Core.Settings;

namespace Mosslight.App.Views;

/// <summary>
/// 「设置」页。移植自 1.0.0 的 `ui/pages/settings_page.py`。
///
/// ## 写设置的三条规矩（跟 1.0.0 一致）
///
/// 1. **改动立刻落盘**：内存/线程走 <see cref="ScheduleMemorySave"/> 的 400ms 防抖，
///    开关和下拉是即改即存 —— 1.0.0 也是即改即存，没有"保存"按钮。
/// 2. **改完要通知别人**：Java 变了，启动页的检查结果就得重算。
///    这里只更新本页的提示，跨页刷新交给「重新扫描」按钮（1.0.0 同样）。
///    （游戏目录不在这页 —— 它在「版本」页，那边改完自己会重扫版本列表。）
/// 3. **文件对话框要用 `Microsoft.Win32`**，不是 `System.Windows.Forms` ——
///    后者会把 WinForms 整套依赖拉进来（这个工程一个多余依赖都没有）。
///
/// ## 移到这一版的"现代化"改动，逐条都记在下面
///   · 1.0.0 用 QTimer 做 400ms 防抖 → 这里用 <see cref="DispatcherTimer"/>（同样 400ms，
///     而且**一次 save() 写完两个内存值**，不写两份 config.json）
///   · 1.0.0 的"已识别 Java"是多行 QLabel → 这里套了一层能滚的 ScrollViewer
///     （理由见 SettingsPage.xaml 里的注释）
/// </summary>
public partial class SettingsPage : UserControl, IMosslightPage
{
    /// <summary>设计说明见 docs/design-notes.md#mosslight-app-views-settingspage-xaml-cs</summary>
    private bool _loading = true;

    /// <summary>正在重建 Java 下拉/清单 —— 挡住 <c>SelectedIndex</c> 那一下被当成"用户选的"。</summary>
    private bool _fillingJava;

    /// <summary>正在重建「下载源」下拉 —— 同理，别把填值当成用户改的。</summary>
    private bool _fillingSource;

    /// <summary>正在重建「游戏语言」下拉。</summary>
    private bool _fillingGameLang;

    /// <summary>
    /// Java 下拉每一项对应的 <c>java_path</c>（空串 = 自动选择），下标跟
    /// <see cref="JavaCombo"/> 的项一一对应。见 <see cref="RefreshJavaList"/> 里的说明。
    /// </summary>
    private readonly List<string> _javaPaths = new();

    /// <summary>正在扫描 —— 「重新扫描」按钮点第二下直接忽略（1.0.0 的 <c>isRunning()</c> 判断）。</summary>
    private bool _scanning;

    /// <summary>
    /// 这一页已经离开可视树了（切走 / 窗口关了）。
    /// 后台扫描回来时**不要再碰控件** —— 那时候页面已经没人用，
    /// 对着一棵脱离可视树的控件树改 Items 在 WPF 里是会抛的。
    /// </summary>
    private bool _detached;

    /// <summary>物理内存信息。构造时查一次就够（页面活不了多久，没必要逐帧重查）。</summary>
    private MemoryInfo _memory = new();

    /// <summary>1.0.0 的 <c>SAVE_DELAY_MS</c>：拖滑块时不要每一格都写一次 config.json。</summary>
    private const int SaveDelayMs = 400;

    /// <summary>1.0.0 给「最小」那个 QSpinBox 的上限（<c>setRange(512, 32768)</c>）。</summary>
    private const int MinMemoryMax = 32768;

    private readonly DispatcherTimer _saveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(SaveDelayMs),
    };

    /// <summary>`--page settings:&lt;子段&gt;` 要求滚到哪张卡片（自动化截图用，见 <see cref="ShowSection"/>）。</summary>
    private string? _pendingSection;

    private bool _sectionApplied;

    /// <param name="initialTab">
    /// 打开时落在哪个 tab（`launch` / `personalize` / `other`，null = 启动）。
    /// `--page personalize` 就是靠它落到「个性化」那一栏的 —— 独立页已经并进来了。
    /// </param>
    public SettingsPage(string? initialTab = null)
    {
        InitializeComponent();

        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            FlushMemory();
        };
        // 离开这一页（切页会把 Content 换掉 → Unloaded）时把还没落盘的内存值补上，
        // 不然"拖完滑块 400ms 内切页"这次改动就丢了
        Unloaded += (_, _) =>
        {
            _detached = true;
            _saveTimer.Stop();
            FlushMemory();
        };
        // 滚动到指定子段：等布局排完再滚（离屏截图不 Show 窗口，Loaded 不一定来，
        // 所以挂在 LayoutUpdated 上而不是 Loaded）
        LayoutUpdated += OnLayoutUpdated;

        // 调试卡只在 Debug 构建里露出来（发布版连占位都不要）
        DebugCard.Visibility = App.IsDevelopmentBuild ? Visibility.Visible : Visibility.Collapsed;

        // NumberBox 的「值变了」是普通事件，不是 XAML 能挂的 RoutedEvent —— 在码里接。
        // ⚠️ 排在 InitializeComponent **之后**：构造期的赋值已经被 _loading 挡住了
        MinBox.ValueChanged += OnMinChanged;

        LoadFromSettings();

        // 落在指定的 tab 上（XAML 默认选中「启动」）。⚠️ 构造期不播动画。
        if (initialTab is { Length: > 0 })
        {
            ShowPane(initialTab);
        }
    }

    /// <summary>这一页不做入场动画（1.0.0 的设置页就是普通卡片）。</summary>
    public Panel? AnimationHost => null;

    // ---------- 读 ----------

    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            var settings = App.Settings;

            // 文案里带路径 → 动态生成：配置目录跟着程序目录走，写死一定是错的
            SubtitleText.Text = Localizer.Tr("改动会立即生效并写入 {path}",
                ("path", settings.FilePath));

            // 内存范围：上限跟物理内存挂钩（1.0.0 是
            // `min(MEMORY_MAX, max(4096, total))` 再对齐到 512 的档）
            _memory = SystemMemory.Query();
            var upper = MaxUpperBound(_memory);

            MinBox.Minimum = SystemMemory.MemoryMin;
            MinBox.Maximum = MinMemoryMax;
            MinBox.Step = SystemMemory.MemoryStep;
            MinBox.SetValueSilently(Math.Clamp(settings.MinMemory, SystemMemory.MemoryMin, MinMemoryMax));

            // ⚠️ 顺序要紧：先抬 Maximum 再给 Value，反过来的话 Value 会先被老上限夹一次
            MaxSlider.Maximum = upper;
            MaxSlider.Value = Math.Clamp(settings.MaxMemory, SystemMemory.MemoryMin, upper);

            MultiThreadCheck.IsChecked = settings.MultiThread;
            ThreadSlider.Value = Math.Clamp(settings.DownloadThreads,
                AppSettings.MinThreadRange, AppSettings.MaxThreadRange);

            FillSourceCombo(settings.DownloadSource);
            FillGameLangCombo(settings.GameLanguage);

            CloseOnLaunchCheck.IsChecked = settings.GetBool("close_on_launch", false);
            // ⚠️ 键名是 `show_log_window`（1.0.0 core/config.py 的 DEFAULT_CONFIG）。
            //    2.0 之前写的 `show_log_on_launch` 是**两个版本都不认**的键：
            //    1.0.0 从不读它，2.0 自己也只在这一个文件里读写 —— 于是用户在
            //    1.0.0 里改过的这个开关，到 2.0 里会显示成默认值。按 1.0.0 对齐。
            ShowLogCheck.IsChecked = settings.GetBool("show_log_window", true);

            // 内存模式（PCL 的「自动配置 / 自定义」）：存的是用户上次选的，默认自定义
            MemoryAutoRadio.IsChecked = settings.MemoryAuto;
            MemoryManualRadio.IsChecked = !settings.MemoryAuto;
        }
        finally
        {
            _loading = false;
        }

        // 自动模式：先把推荐值套上（不改配置，等用户真去动滑块/切模式才落盘）
        ApplyMemoryMode(App.Settings.MemoryAuto, persist: false);

        RefreshLabels();
        RefreshJavaList();
        EnsureJavaScanned();
    }

    /// <summary>最大内存滑块的上限：跟物理内存挂钩（1.0.0 的 <c>_make_memory_card</c>）。</summary>
    private static int MaxUpperBound(MemoryInfo info)
    {
        var upper = info.Ok
            ? Math.Min(SystemMemory.MemoryMax, Math.Max(4096, info.TotalMb))
            : SystemMemory.MemoryMax;
        return upper / SystemMemory.MemoryStep * SystemMemory.MemoryStep;
    }

    private void RefreshLabels()
    {
        MaxValue.Text = $"{CurrentMaxMb()} MB";
        ThreadValue.Text = CurrentThreads().ToString(CultureInfo.InvariantCulture);

        // 线程数只在开了多线程时才有意义（关着的时候其实还是用默认 8，见 EffectiveThreads）
        ThreadSlider.IsEnabled = App.Settings.MultiThread;

        RefreshThreadHint();
        RefreshSourceHint();
        RefreshGameLangHint();
        RefreshMemoryBar();
        RefreshConfigHint();
    }

    // ---------- 游戏语言 ----------

    /// <summary>
    /// 「游戏语言」下拉：第一项是「自动（跟随启动器语言）」，其余用该语言自己的写法
    /// （跟启动器的语言下拉同一个约定：语言名字不进语言文件）。
    /// </summary>
    private void FillGameLangCombo(string current)
    {
        _fillingGameLang = true;
        try
        {
            if (GameLangCombo.Items.Count == 0)
            {
                foreach (var (key, _, native) in GameLocales.Choices)
                {
                    GameLangCombo.Items.Add(new ComboBoxItem
                    {
                        Content = key == GameLocales.Auto
                            ? Localizer.Tr("自动（跟随启动器语言）")
                            : native,
                        Tag = key,
                    });
                }
            }

            var index = 0;
            for (var i = 0; i < GameLangCombo.Items.Count; i++)
            {
                if (GameLangCombo.Items[i] is ComboBoxItem { Tag: string tag }
                    && string.Equals(tag, current, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }
            GameLangCombo.SelectedIndex = index;
        }
        finally
        {
            _fillingGameLang = false;
        }
    }

    private void OnGameLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _fillingGameLang)
        {
            return;
        }
        if (GameLangCombo.SelectedItem is not ComboBoxItem { Tag: string key })
        {
            return;
        }
        App.Settings.GameLanguage = key;
        RefreshGameLangHint();
    }

    /// <summary>
    /// 说清楚这个设置**只影响第一次启动**：已有 `options.txt` 的版本一个字都不会被改，
    /// 免得用户以为"选了中文，游戏里还是英文就是坏了"（那段是他自己在游戏里改过的）。
    /// </summary>
    private void RefreshGameLangHint()
    {
        var key = App.Settings.GameLanguage;
        var locale = GameLocales.Resolve(key, Localizer.CurrentLanguage);
        GameLangHint.Text = string.Equals(key, GameLocales.Auto, StringComparison.OrdinalIgnoreCase)
            ? Localizer.Tr(
                "只在版本第一次启动（还没有 options.txt）时写入；现在是「自动」，跟着启动器语言走（{lang}）。之后你在游戏里改的语言不会被覆盖。",
                ("lang", GameLocales.NativeName(locale)))
            : Localizer.Tr(
                "只在版本第一次启动（还没有 options.txt）时写入（{lang}）；之后你在游戏里改的语言不会被覆盖。",
                ("lang", GameLocales.NativeName(locale)));
    }

    // ---------- 下载源 ----------

    /// <summary>
    /// 填「下载源」下拉。四项顺序**必须**跟 <see cref="DownloadSourceMode"/> 的数值对齐
    /// （0=自动 / 1=只用官方 / 2=只用 BMCLAPI / 3=双流），直接按下标换算。
    /// </summary>
    private void FillSourceCombo(DownloadSourceMode current)
    {
        _fillingSource = true;
        try
        {
            if (SourceCombo.Items.Count == 0)
            {
                SourceCombo.Items.Add(Localizer.Tr("自动（推荐）"));
                SourceCombo.Items.Add(Localizer.Tr("只用官方源"));
                SourceCombo.Items.Add(Localizer.Tr("只用 BMCLAPI"));
                SourceCombo.Items.Add(Localizer.Tr("双流并发（官方 + BMCLAPI）"));
            }
            SourceCombo.SelectedIndex = (int)current;
        }
        finally
        {
            _fillingSource = false;
        }
    }

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _fillingSource)
        {
            return;
        }
        var index = SourceCombo.SelectedIndex;
        if (index < 0)
        {
            return;
        }
        App.Settings.DownloadSource = (DownloadSourceMode)index;
        RefreshSourceHint();
    }

    /// <summary>
    /// 把当前策略解释清楚 —— 四个选项的行为差别不小（尤其"只用 BMCLAPI"和"双流"），
    /// 光看名字很容易选错，所以每个都要有一句人话。
    /// </summary>
    private void RefreshSourceHint()
    {
        SourceHint.Text = App.Settings.DownloadSource switch
        {
            DownloadSourceMode.Official => Localizer.Tr(
                "只用官方源：Mojang / Forge / NeoForge 直连，不走 BMCLAPI。国内网络可能很慢。"),
            DownloadSourceMode.Mirror => Localizer.Tr(
                "只用 BMCLAPI：能镜像的都走镜像；镜像不覆盖的地址（Modrinth / CurseForge 等）仍然直连。"),
            DownloadSourceMode.Dual => Localizer.Tr(
                "双流并发：同一个文件同时从 BMCLAPI 和官方源下，谁先完成用谁。带宽会翻倍，不建议常开。"),
            _ => Localizer.Tr(
                "自动（推荐）：优先用 BMCLAPI 镜像，镜像不通时自动换官方源；镜像连续失败会暂停 60 秒再试。"),
        };
    }

    // ---------- Java ----------

    /// <summary>
    /// 重建下拉框和"已识别 N 个 Java"清单。逻辑照 1.0.0 的 <c>_fill_java_combo</c>：
    ///   · 第一项永远是「自动选择（推荐）」（对应配置里 <c>java_path</c> 是空串）
    ///   · 然后每个能用的 Java 一项，显示 `Java {主版本} · {路径}`
    ///   · 配置里指定的那个**没扫到也列出来**，免得用户以为设置丢了
    /// </summary>
    private void RefreshJavaList()
    {
        // 防重入：后台扫描回来那一刻可能正好撞上「重新扫描」按钮的回调，
        // 两次 Items.Clear() + 重填会在同一次消息里白干一遍
        if (_fillingJava)
        {
            return;
        }
        _fillingJava = true;
        try
        {
            var usable = App.Services.Java.Detected.Where(j => j.Usable).ToList();
            var saved = App.Settings.GetString("java_path") ?? string.Empty;

            // 下拉项放**纯字符串**，路径另存在 _javaPaths 里按下标对应 ——
            // 跟 1.0.0 的 `addItem(text, data)` 是同一个模型（文本和值分开存），
            // 比自建 ComboBoxItem 再往 Tag 里塞东西少一层容器状态。
            _javaPaths.Clear();
            JavaCombo.SelectedIndex = -1;
            JavaCombo.Items.Clear();

            _javaPaths.Add(string.Empty);
            JavaCombo.Items.Add(Localizer.Tr("自动选择（推荐）"));

            foreach (var java in usable)
            {
                _javaPaths.Add(java.Path);
                JavaCombo.Items.Add($"Java {java.Major} · {java.Path}");
            }

            if (saved.Length > 0 && !usable.Any(j => PathEquals(j.Path, saved)))
            {
                _javaPaths.Add(saved);
                JavaCombo.Items.Add(Localizer.Tr("手动指定：{path}", ("path", saved)));
            }

            var index = 0;
            for (var i = 1; i < _javaPaths.Count; i++)
            {
                if (PathEquals(_javaPaths[i], saved))
                {
                    index = i;
                    break;
                }
            }
            JavaCombo.SelectedIndex = index;

            JavaListPanel.Children.Clear();
            if (usable.Count > 0)
            {
                JavaCountText.Text = Localizer.Tr("已找到 {n} 个 Java：", ("n", usable.Count));
                JavaListScroll.Visibility = Visibility.Visible;
                var style = (Style)FindResource("HintText");
                foreach (var java in usable)
                {
                    // 一行一个：厂商 / 版本 / 位数（JavaRuntime.Label()）+ 路径。
                    // 1.0.0 只显示 Label()；用户明确要"每一项显示……路径"，所以补上路径
                    JavaListPanel.Children.Add(new TextBlock
                    {
                        Text = "    " + java.Label() + "    ·    " + java.Path,
                        Style = style,
                        TextWrapping = TextWrapping.NoWrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        ToolTip = java.Path,
                    });
                }
            }
            else
            {
                JavaCountText.Text = Localizer.Tr("没找到任何 Java，点「重新扫描」，或者自己装一个。");
                JavaListScroll.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            _fillingJava = false;
        }
    }

    /// <summary>
    /// 首次进这一页时如果还没有扫描结果，后台扫一遍（走缓存，通常几十毫秒）。
    ///
    /// ⚠️ 跟 1.0.0 的差别：1.0.0 每次进设置页都强制**忽略缓存**重扫一遍
    ///    （`QuickJavaScanTask(..., use_cache=False)`），代价是十几秒。
    ///    2.0 每次切页都会新建一个 SettingsPage，照抄的话每点一次导航就起十几个
    ///    java 进程 —— 所以只在这一页**还没有数据**时自动扫，
    ///    真要重扫由用户点「重新扫描」（那个是忽略缓存的，跟 1.0.0 一样）。
    /// </summary>
    private void EnsureJavaScanned()
    {
        if (App.Services.Java.Detected.Count > 0)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await App.Services.Java.ScanAsync();
            }
            catch (Exception)
            {
                // 扫描是"锦上添花"，失败就保持原样
            }
            Dispatcher.Invoke(() =>
            {
                // 这一页可能早就被切走了（扫描要好几秒）—— 那就别再碰它的控件
                if (!_detached)
                {
                    RefreshJavaList();
                }
            });
        });
    }

    private void OnJavaSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _fillingJava)
        {
            return;
        }

        var index = JavaCombo.SelectedIndex;
        var path = index >= 0 && index < _javaPaths.Count ? _javaPaths[index] : string.Empty;
        var current = App.Settings.GetString("java_path") ?? string.Empty;
        if (!PathEquals(path, current))
        {
            App.Settings.Set("java_path", path);
            App.Settings.Save();
        }
    }

    private void OnRescanJavaClick(object sender, RoutedEventArgs e)
    {
        if (_scanning)
        {
            return;
        }
        _scanning = true;

        // 界面先改口，别让人以为没反应（真正的扫描要起十几个 java 进程，好几秒）
        RescanJavaButton.IsEnabled = false;
        RescanJavaButton.Content = Localizer.Tr("扫描中…");

        _ = Task.Run(async () =>
        {
            try
            {
                if (App.Services.Java is RealJavaService real)
                {
                    // 按钮点的是"重新扫描" —— 忽略缓存，真去探一遍
                    await real.RescanAsync();
                }
                else
                {
                    await App.Services.Java.ScanAsync();
                }
            }
            catch (Exception)
            {
                // 同上：失败不该把界面卡住
            }

            // 回 UI 线程再碰控件
            Dispatcher.Invoke(() =>
            {
                _scanning = false;
                if (_detached)
                {
                    return;
                }
                RescanJavaButton.IsEnabled = true;
                RescanJavaButton.Content = Localizer.Tr("重新扫描");
                RefreshJavaList();
            });
        });
    }

    /// <summary>路径比较（Windows 上大小写不敏感；1.0.0 那边是直接字符串相等）。</summary>
    private static bool PathEquals(string a, string b)
        => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    // ---------- 内存 ----------

    private int CurrentMaxMb() => (int)MaxSlider.Value;

    /// <summary>
    /// 最小堆和最大堆**互相夹**。
    ///
    /// ⚠️ `-Xms` 大于 `-Xmx` 时 JVM 会**直接拒绝启动**（不是警告，是起不来）。
    ///    所以这不是体贴，是必需 —— 1.0.0 的原文案也写着「两边会自动联动」。
    ///
    /// 规则（逐条照 1.0.0 的 `_on_min_memory_changed`）：
    ///   最小 → 最大：最小超过最大时，把**最大**一起顶上去（不弹窗骂用户）
    ///   最大 → 最小：最大小于最小时，把**最小**一起压下来
    /// </summary>
    private void OnMinChanged(object? sender, int value)
    {
        if (_loading)
        {
            return;
        }

        if (value > MaxSlider.Value)
        {
            MaxSlider.Value = value;
        }
        RefreshLabels();
        ScheduleMemorySave();
    }

    private void OnMaxChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        // 滑块拖出来的值不是整数档，先吸附 —— 不吸附的话配置里会出现 3586 这种数，
        // 而滑块本身又画不出那么细的差别
        var value = SystemMemory.SnapMemory((int)e.NewValue);
        if (value != (int)MaxSlider.Value)
        {
            // 递归一次（同步），那次会走完下面的流程
            MaxSlider.Value = value;
            return;
        }

        if (value < MinBox.Value)
        {
            MinBox.SetValueSilently(value);
        }
        RefreshLabels();
        ScheduleMemorySave();
    }

    /// <summary>「用推荐值（N MB）」按钮。</summary>
    private void OnRecommendClick(object sender, RoutedEventArgs e)
    {
        if (!_memory.Ok)
        {
            return;
        }
        var value = SystemMemory.RecommendMemory(_memory.TotalMb);
        // 最小跟着走一半，别让 min 卡住 max（联动逻辑在 On*Changed 里）
        MinBox.SetValueSilently(Math.Min(value, Math.Max(SystemMemory.MemoryMin, value / 2)));
        MaxSlider.Value = Math.Min(value, (int)MaxSlider.Maximum);
        RefreshLabels();
        ScheduleMemorySave();
    }

    /// <summary>「自动配置 / 自定义」两个单选（照 PCL 的内存分配）。</summary>
    private void OnMemoryModeClick(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        ApplyMemoryMode(MemoryAutoRadio.IsChecked == true, persist: true);
    }

    /// <summary>
    /// 套用内存模式。
    ///
    /// · 自动：把堆上限设成推荐值（跟「用推荐值」按钮同一套算法），数字框和滑块**收起来**；
    /// · 自定义：把那两个控件放出来，值保持用户上次的（切回自定义不会凭空改数）。
    ///
    /// ⚠️ `persist=false` 是构造期用的：只摆界面，不写用户配置（否则"打开设置页"这个动作
    ///    本身就会把自动算出来的值落盘，用户没点过任何东西配置却变了）。
    /// </summary>
    private void ApplyMemoryMode(bool auto, bool persist)
    {
        MemoryControls.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;

        if (auto && _memory.Ok)
        {
            var value = Math.Min(SystemMemory.RecommendMemory(_memory.TotalMb), (int)MaxSlider.Maximum);
            MinBox.SetValueSilently(Math.Min(value, Math.Max(SystemMemory.MemoryMin, value / 2)));
            MaxSlider.Value = value;
        }

        RefreshLabels();

        if (persist)
        {
            App.Settings.MemoryAuto = auto;
            ScheduleMemorySave();
        }
    }

    private void RefreshMemoryBar()
    {
        var heap = CurrentMaxMb();
        var overhead = SystemMemory.JvmOverheadMb(heap);
        MemBar.SetValues(_memory.TotalMb, _memory.UsedMb, heap, overhead);

        // 条下面那两行数值（照 PCL：左「已使用 / 物理」，右「游戏分配」）
        if (_memory.Ok)
        {
            MemoryUsedValue.Text = $"{Gb(_memory.UsedMb)} GB / {Gb(_memory.TotalMb)} GB";
            MemoryHeapValue.Text = $"{Gb(heap)} GB";
        }
        else
        {
            MemoryUsedValue.Text = "—";
            MemoryHeapValue.Text = $"{Gb(heap)} GB";
        }

        if (_memory.Ok)
        {
            MemoryLegend.Text = Localizer.Tr(
                "物理内存 {total} GB · 系统已用 {used} GB · 游戏最多占用 {heap} GB · JVM 开销约 {overhead} GB",
                ("total", Gb(_memory.TotalMb)), ("used", Gb(_memory.UsedMb)),
                ("heap", Gb(heap)), ("overhead", Gb(overhead)));
            RecommendButton.IsEnabled = true;
            RecommendButton.Content = Localizer.Tr("用推荐值（{n} MB）",
                ("n", SystemMemory.RecommendMemory(_memory.TotalMb)));
        }
        else
        {
            // 读不到就老实说 —— 只按用户填的值画，不假装知道系统占用
            MemoryLegend.Text = Localizer.Tr("读不出系统内存，这条只按你填的值画。");
            RecommendButton.IsEnabled = false;
            RecommendButton.Content = Localizer.Tr("用推荐值");
        }
    }

    /// <summary>MB → "12.4"（配合文案里的 GB 用）。小数位固定 1 位，跟 Python 的 <c>:.1f</c> 一致。</summary>
    private static string Gb(int megabytes)
        => (Math.Max(0, megabytes) / 1024.0).ToString("F1", CultureInfo.InvariantCulture);

    private void ScheduleMemorySave()
    {
        // 每次改动都重置计时 —— 拖滑块期间一次都不写，停手 400ms 才写
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>
    /// 防抖之后**一次写完两个内存值**。
    ///
    /// 1.0.0 的注释解释过为什么直接改 <c>config.data</c> 再调一次 <c>save()</c>、
    /// 而不是调两次 <c>config.set()</c>：那样一次拖动会写出两份 config.json。
    /// 这里同理：<see cref="AppSettings.Set(string,int)"/> 只改内存里的节点，
    /// <see cref="AppSettings.Save"/> 才落盘，所以下面只有一次写盘。
    /// </summary>
    private void FlushMemory()
    {
        var min = MinBox.Value;
        var max = CurrentMaxMb();
        var settings = App.Settings;

        // 值没变就别碰用户的 config.json（切页/关窗时也会走到这儿）
        if (settings.GetInt("min_memory", AppSettings.DefaultMinMemory) == min
            && settings.GetInt("max_memory", AppSettings.DefaultMaxMemory) == max)
        {
            return;
        }

        settings.Set("min_memory", min);
        settings.Set("max_memory", max);
        settings.Save();
    }

    // ---------- 下载 ----------

    private int CurrentThreads() => (int)Math.Round(ThreadSlider.Value);

    /// <summary>
    /// 开关状态 + 滑块可用性 + 那句"实际用几个线程"的说明。
    ///
    /// ⚠️ 「关着的时候用默认的 8 个线程（不是单线程）」—— 这是 1.0.0 从实验项目
    ///    照搬的语义（"先让用户自己开，出问题好定位"），别自作主张改成"关 = 单线程"。
    /// </summary>
    private void RefreshThreadHint()
    {
        var settings = App.Settings;
        if (settings.MultiThread)
        {
            ThreadHint.Text = Localizer.Tr(
                "开得越多下载越快，但太吃网络和磁盘；8~16 一般就够了。当前用 {n} 个线程。",
                ("n", settings.EffectiveThreads));
        }
        else
        {
            ThreadHint.Text = Localizer.Tr(
                "关着的时候用默认的 {d} 个线程（不是单线程）。当前用 {n} 个线程。",
                ("d", AppSettings.DefaultThreads), ("n", settings.EffectiveThreads));
        }
    }

    private void OnMultiThreadClick(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        App.Settings.MultiThread = MultiThreadCheck.IsChecked == true;
        RefreshLabels();
    }

    private void OnThreadChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }
        App.Settings.DownloadThreads = CurrentThreads();
        RefreshLabels();
    }

    // ---------- 其他 ----------

    private void OnCloseOnLaunchClick(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        App.Settings.Set("close_on_launch", CloseOnLaunchCheck.IsChecked == true);
        App.Settings.Save();
    }

    private void OnShowLogClick(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        App.Settings.Set("show_log_window", ShowLogCheck.IsChecked == true);
        App.Settings.Save();
    }

    /// <summary>
    /// 「配置位置：…」那一行。程序目录写不了时会退到 `%APPDATA%`，
    /// 那种情况必须写明，否则用户按显示的路径找不到东西。
    /// </summary>
    private void RefreshConfigHint()
    {
        var settings = App.Settings;
        var dir = Path.GetDirectoryName(settings.FilePath) ?? settings.FilePath;
        ConfigHint.Text = settings.UsingFallbackDir
            ? Localizer.Tr("配置位置：{path}（程序目录写不了，已改存在这里）", ("path", dir))
            : Localizer.Tr("配置位置：{path}", ("path", dir));
    }

    // ---------- 关于 ----------

    // ---------- 调试卡（只有 Debug 构建里可见） ----------

    private void OnDebugToastInfo(object sender, RoutedEventArgs e)
        => MainWindow.Current?.ShowToast(
            Localizer.Tr("部分调试信息将在刷新或启动器重启后切换显示！"), ToastKind.Info);

    private void OnDebugToastSuccess(object sender, RoutedEventArgs e)
        => MainWindow.Current?.ShowToast(
            Localizer.Tr("安装完成，可以启动了。"), ToastKind.Success);

    private void OnDebugToastWarning(object sender, RoutedEventArgs e)
        => MainWindow.Current?.ShowToast(
            Localizer.Tr("这个版本还没有装完，先别启动。"), ToastKind.Warning);

    private void OnDebugToastError(object sender, RoutedEventArgs e)
        => MainWindow.Current?.ShowToast(
            Localizer.Tr("下载失败了，检查一下网络再试。"), ToastKind.Error);

    /// <summary>打开「开发版提示窗」。正文框里有字就用它，空着用默认那三段。</summary>
    private void OnDebugNotice(object sender, RoutedEventArgs e)
    {
        var custom = DebugNoticeBox.Text.Trim();
        var dialog = new DebugBuildNoticeDialog(custom.Length > 0 ? custom : null)
        {
            Owner = Window.GetWindow(this),
        };
        dialog.ShowDialog();
    }

    /// <summary>
    /// 「检查更新」：查 → 下载 → 校验 → 交给脚本替换。点了这个按钮 = 同意更新，
    /// 所以下载完直接重启进新版本，不再弹第二个确认框。
    ///
    /// ⚠️ 流程本身在 `Infrastructure/UpdateCheck.cs`：关于对话框里那颗「检查更新」
    ///    要的是同一套东西，抄第二份的话两处迟早会不一样。
    /// </summary>
    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        await Infrastructure.UpdateCheck.RunAsync(Window.GetWindow(this), UpdateButton);
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        // ⚠️ Owner 要显式给：不给的话对话框可能跑到主窗口后面去
        var dialog = new AboutDialog { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }

    // ---------- tab 切换 ----------

    private void OnTabClick(object sender, RoutedEventArgs e)
        => ShowPane((sender as FrameworkElement)?.Tag as string);

    /// <summary>
    /// 切 tab（启动 / 个性化 / 其他）。
    ///
    /// ⚠️ 表驱动：tab、面板、滚动条三者的对应关系写在一张表里，加一栏只改这一处。
    /// ⚠️ 切过去时给新面板播一次入场动画（复用 <see cref="PageTransition"/>，跟换页同一套参数）；
    ///    截图模式必须跳过 —— 离屏渲染不推进动画时钟，面板会停在 Opacity=0 截成空白。
    /// </summary>
    private void ShowPane(string? key)
    {
        var target = key switch
        {
            "personalize" => PersonalizePane,
            "other" => OtherPane,
            _ => LaunchPane,
        };

        var panes = new (ToggleButton Tab, ScrollViewer Pane)[]
        {
            (TabLaunch, LaunchPane),
            (TabPersonalize, PersonalizePane),
            (TabOther, OtherPane),
        };

        foreach (var (tab, pane) in panes)
        {
            var active = ReferenceEquals(pane, target);
            tab.IsChecked = active;
            pane.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!App.ScreenshotMode)
        {
            PageTransition.PlayIn(target);
        }
    }

    // ---------- 自动化截图的子段入口 ----------

    /// <summary>
    /// `--page settings:&lt;子段&gt;`：把页面滚到某张卡片。
    ///
    /// 为什么需要它：这一页（Java / 内存 / 下载 / 其他 / 关于）加起来
    /// 比 820 高的窗口高一截，离屏截图**只会截到最上面那一屏** ——
    /// 底下的「下载」「其他」「关于」就永远没人验过。
    /// 传 `java` / `memory` / `download` / `other` / `about`。
    ///
    /// ⚠️ 没有 `dir` 了：游戏目录那张卡已经跟 1.0.0 对齐删掉（见 SettingsPage.xaml
    ///    的文件头说明），页面里已经没有 `DirBox` 可滚。第一张卡就是 Java。
    ///
    /// 特例 `aboutdialog`：只把「关于」对话框建出来、**不滚页** ——
    /// 那个对话框是点出来的，脚本点不了鼠标（跟 `personalize:anim` 一个思路）。
    /// </summary>
    public void ShowSection(string section)
    {
        if (string.Equals(section, "aboutdialog", StringComparison.Ordinal))
        {
            _aboutDialog = new AboutDialog();
            return;
        }

        // 个性化那几个子段（`personalize:anim`、`personalize:bg`）以前挂在独立页上，
        // 现在归这儿：先切到「个性化」tab，再交给那个控件去开对话框。
        if (section.StartsWith("anim", StringComparison.Ordinal)
            || string.Equals(section, "bg", StringComparison.Ordinal))
        {
            ShowPane("personalize");
            Personalize.ShowSection(section);
            return;
        }

        // 滚动子段：先落在正确的 tab 上，再等布局排完滚过去
        ShowPane(section switch
        {
            "personalize" => "personalize",
            "download" or "other" or "about" or "debug" => "other",
            _ => "launch",
        });
        _pendingSection = section;
        _sectionApplied = false;
    }

    /// <summary>
    /// 离屏截图时打开的那个对话框实例（`--page settings:aboutdialog`、
    /// `--page personalize:anim` 建的）。
    /// ⚠️ 只有在 <see cref="App.ScreenshotMode"/> 下才有值：那种模式下对话框**不 Show**，
    ///    得留个引用让 App 去渲染它。
    /// </summary>
    public Window? ActiveDialog => _aboutDialog ?? Personalize.ActiveDialog;

    private Window? _aboutDialog;

    /// <summary>
    /// 布局排完之后再滚。
    ///
    /// ⚠️ 不能挂在 `Loaded` 上：离屏截图**不 Show 窗口**，`Loaded` 不一定会来；
    ///    而 `LayoutUpdated` 在 App 手动 Measure/Arrange 那一趟之后一定会来。
    ///    一次性：滚完就把自己摘掉，免得用户手动滚动时被反复拽回去。
    /// </summary>
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_pendingSection is null || _sectionApplied)
        {
            return;
        }
        _sectionApplied = true;

        // ⚠️ 目标类型要**显式写出来**：几个分支是互不相干的控件类型（ComboBox / MemoryBar /
        //    CheckBox / Button），switch 表达式推不出共同类型，会报 CS8506。
        FrameworkElement? target = _pendingSection switch
        {
            "java" => JavaCombo,
            "memory" => MemBar,
            "download" => MultiThreadCheck,
            "other" => GameLangCombo,
            "debug" => DebugToastButton,
            "about" => AboutButton,
            _ => null,
        };
        _pendingSection = null;
        if (target is null)
        {
            return;
        }

        // 直接把偏移量算出来滚过去，比 BringIntoView 可控
        // （后者会尽量"少滚一点"，卡片只露出一半，截图反而看不全）
        // ⚠️ 三个 tab 各有自己的滚动条：Java/内存 在「启动」栏，下载/其他/关于 在「其他」栏，
        //    所以滚的是**当前这一栏**的 ScrollViewer，不再是原来那一个 PageScroll。
        try
        {
            var offset = target.TransformToAncestor(this).Transform(new Point(0, 0)).Y;
            var pane = TabOther.IsChecked == true ? OtherPane : LaunchPane;
            pane.ScrollToVerticalOffset(Math.Max(0, offset - 24));
        }
        catch (InvalidOperationException)
        {
            // 目标不在可视树里（页面刚被换掉）—— 忽略就好
        }
    }
}
