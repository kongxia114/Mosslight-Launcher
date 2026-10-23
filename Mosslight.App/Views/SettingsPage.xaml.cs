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

public partial class SettingsPage : UserControl, IMosslightPage
{
    private bool _loading = true;

    // 正在重建 Java 下拉/清单 —— 挡住 SelectedIndex 那一下被当成"用户选的"。
    private bool _fillingJava;

    // 正在重建「下载源」下拉 —— 同理，别把填值当成用户改的。
    private bool _fillingSource;

    // 正在重建「游戏语言」下拉。
    private bool _fillingGameLang;

    // Java 下拉每一项对应的 java_path（空串 = 自动选择），下标跟 JavaCombo 的项一一对应。
    private readonly List<string> _javaPaths = new();

    // 正在扫描 —— 「重新扫描」按钮点第二下直接忽略。
    private bool _scanning;

    // 这一页已经离开可视树了（切走 / 窗口关了）。
    private bool _detached;

    // 物理内存信息；⚠️ 每 1.5 秒重查，PCL 的「已使用内存」是实时变化的。
    private MemoryInfo _memory = new();

    /// <summary>
    /// 「已使用内存」的刷新计时器（照 PCL 的观感：数值自己是会动的）；⚠️ 只重算**系统占用**那一半：
    /// 堆上限、滑块位置、用户填的值一律不碰 —— 刷新绝不能把用户正在拖的滑块拽回去。
    /// </summary>
    private readonly DispatcherTimer _memoryRefreshTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1.5),
    };

    private const int SaveDelayMs = 400;

    private const int MinMemoryMax = 32768;

    private readonly DispatcherTimer _saveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(SaveDelayMs),
    };

    // `--page settings:<子段>` 要求滚到哪张卡片（自动化截图用，见 ShowSection）。
    private string? _pendingSection;

    private bool _sectionApplied;

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
            _memoryRefreshTimer.Stop();
            FlushMemory();
        };

        // 「已使用内存」实时刷新（PCL 那边是动着的）：只更新系统占用，不碰滑块和用户填的值
        _memoryRefreshTimer.Tick += (_, _) =>
        {
            if (_detached)
            {
                return;
            }
            _memory = SystemMemory.Query();
            RefreshMemoryBar();
        };
        Loaded += (_, _) =>
        {
            if (!App.ScreenshotMode)
            {
                _memoryRefreshTimer.Start();
            }
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

    /// <summary>这一页不做入场动画。</summary>
    public Panel? AnimationHost => null;

    // 读

    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            var settings = App.Settings;

            // 文案里带路径 → 动态生成：配置目录跟着程序目录走，写死一定是错的
            SubtitleText.Text = Localizer.Tr("改动会立即生效并写入 {path}",
                ("path", settings.FilePath));

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
            // ⚠️ 键名必须是 show_log_window；写成 show_log_on_launch 会读不到，开关失效。
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
        RefreshJunkHint();
    }

    private void RefreshJunkHint()
    {
        var mcDir = App.Services.Games.MinecraftDir;
        JunkHint.Text = Localizer.Tr("正在统计可清理的日志…");
        _ = Task.Run(() =>
        {
            var bytes = Maintenance.MeasureGameJunk(mcDir);
            Dispatcher.Invoke(() =>
            {
                if (_detached)
                {
                    return;
                }
                JunkHint.Text = bytes > 0
                    ? Localizer.Tr("日志和崩溃报告占了 {size}，可以一键清掉。", ("size", FormatSize(bytes)))
                    : Localizer.Tr("没有可清理的日志，挺干净的。");
                CleanJunkButton.IsEnabled = bytes > 0;
            });
        });
    }

    private async void OnCleanJunkClick(object sender, RoutedEventArgs e)
    {
        var mcDir = App.Services.Games.MinecraftDir;
        CleanJunkButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => Maintenance.CleanGameJunk(mcDir));
            if (result.Failed.Count > 0)
            {
                MainWindow.Current?.ShowToast(
                    Localizer.Tr("清掉了 {n} 个文件（{size}），有 {fail} 个删不掉（游戏可能正在运行）。",
                        ("n", result.Files), ("size", FormatSize(result.Bytes)), ("fail", result.Failed.Count)),
                    ToastKind.Warning);
            }
            else
            {
                MainWindow.Current?.ShowToast(
                    Localizer.Tr("已清理 {n} 个文件，释放 {size}。", ("n", result.Files), ("size", FormatSize(result.Bytes))),
                    ToastKind.Success);
            }
        }
        finally
        {
            RefreshJunkHint();
        }
    }

    private async void OnOptimizeMemoryClick(object sender, RoutedEventArgs e)
    {
        OptimizeMemoryButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(Maintenance.OptimizeMemory);
            MainWindow.Current?.ShowToast(
                Localizer.Tr("收回了 {n} 个进程的工作集，约 {size}。", ("n", result.Processes), ("size", FormatSize(result.Bytes))),
                ToastKind.Success);
        }
        finally
        {
            OptimizeMemoryButton.IsEnabled = true;
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return (bytes / 1024.0 / 1024 / 1024).ToString("0.##") + " GB";
        }
        if (bytes >= 1024L * 1024)
        {
            return (bytes / 1024.0 / 1024).ToString("0.#") + " MB";
        }
        if (bytes >= 1024)
        {
            return (bytes / 1024.0).ToString("0.#") + " KB";
        }
        return bytes + " B";
    }

    /// <summary>最大内存滑块的上限：跟物理内存挂钩。</summary>
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

    // 游戏语言

    /// <summary>
    /// 「游戏语言」下拉：第一项是「自动（跟随启动器语言）」，其余用该语言自己的写法 （跟启动器的
    /// 语言下拉同一个约定：语言名字不进语言文件）。
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
    /// 说清楚这个设置**只影响第一次启动**：已有 `options.txt` 的版本一个字都不会被改，免得用户以
    /// 为"选了中文，游戏里还是英文就是坏了"（那段是他自己在游戏里改过的）。
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

    // 下载源

    /// <summary>填「下载源」下拉。</summary>
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
    /// 把当前策略解释清楚 —— 四个选项的行为差别不小（尤其"只用 BMCLAPI"和"双流"），光看名字很容易
    /// 选错，所以每个都要有一句人话。
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

    // Java

    /// <summary>重建下拉框和"已识别 N 个 Java"清单。</summary>
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
            RemoveJavaButton.IsEnabled = index > 0 && index < _javaPaths.Count &&
                App.Services.Java.CustomPaths.Any(p => PathEquals(p, _javaPaths[index]));

            JavaListPanel.Children.Clear();
            if (usable.Count > 0)
            {
                JavaCountText.Text = Localizer.Tr("已找到 {n} 个 Java：", ("n", usable.Count));
                JavaListScroll.Visibility = Visibility.Visible;
                var style = (Style)FindResource("HintText");
                var customs = App.Services.Java.CustomPaths;
                foreach (var java in usable)
                {
                    var manual = customs.Any(p => PathEquals(p, java.Path));
                    JavaListPanel.Children.Add(new TextBlock
                    {
                        Text = "    " + java.Label() + "    ·    " + java.Path
                               + (manual ? "    ·    " + Localizer.Tr("手动添加") : string.Empty),
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

    /// <summary>首次进页时后台扫一遍 Java；⚠️ 走缓存，不要每次强制重扫（要十几秒）。</summary>
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

    private async void OnAddJavaClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localizer.Tr("选择 java.exe"),
            Filter = "java.exe|java.exe;javaw.exe|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        AddJavaButton.IsEnabled = false;
        try
        {
            var runtime = await App.Services.Java.AddCustomAsync(dialog.FileName);
            if (runtime is null)
            {
                MainWindow.Current?.ShowToast(
                    Localizer.Tr("这个文件不是可用的 Java（读不出版本号），换一个试试。"), ToastKind.Error);
                return;
            }

            App.Settings.Set("java_path", runtime.Path);
            App.Settings.Save();
            RefreshJavaList();
            MainWindow.Current?.ShowToast(
                Localizer.Tr("已添加 Java {major}：{path}", ("major", runtime.Major), ("path", runtime.Path)),
                ToastKind.Success);
        }
        finally
        {
            AddJavaButton.IsEnabled = true;
        }
    }

    private void OnRemoveJavaClick(object sender, RoutedEventArgs e)
    {
        var index = JavaCombo.SelectedIndex;
        var path = index >= 0 && index < _javaPaths.Count ? _javaPaths[index] : string.Empty;
        if (path.Length == 0 || !App.Services.Java.CustomPaths.Any(p => PathEquals(p, path)))
        {
            return;
        }

        App.Services.Java.RemoveCustom(path);
        if (PathEquals(path, App.Settings.GetString("java_path") ?? string.Empty))
        {
            App.Settings.Set("java_path", string.Empty);
            App.Settings.Save();
        }

        RefreshJavaList();
        MainWindow.Current?.ShowToast(Localizer.Tr("已把这个 Java 从列表里移除。"), ToastKind.Info);
    }
    private void OnRescanJavaClick(object sender, RoutedEventArgs e)    {
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

    private static bool PathEquals(string a, string b)
        => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    // 内存

    private int CurrentMaxMb() => (int)MaxSlider.Value;

    /// <summary>
    /// 最小堆和最大堆**互相夹**；⚠️ `-Xms` 大于 `-Xmx` 时 JVM 会**直接拒绝启动**（不是警告，是起
    /// 不来）。
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
    /// 套用内存模式；⚠️ `persist=false` 是构造期用的：只摆界面，不写用户配置（否则"打开设置页"这
    /// 个动作本身就会把自动算出来的值落盘，用户没点过任何东西配置却变了）。
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

    /// <summary>MB → "12.4"（配合文案里的 GB 用）。</summary>
    private static string Gb(int megabytes)
        => (Math.Max(0, megabytes) / 1024.0).ToString("F1", CultureInfo.InvariantCulture);

    private void ScheduleMemorySave()
    {
        // 每次改动都重置计时 —— 拖滑块期间一次都不写，停手 400ms 才写
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>防抖之后**一次写完两个内存值**。</summary>
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

    // 下载

    private int CurrentThreads() => (int)Math.Round(ThreadSlider.Value);

    /// <summary>
    /// 开关状态 + 滑块可用性 + 线程说明；⚠️ 关闭时用默认 8 个线程，不是单线程，别改。
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

    // 其他

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

    /// <summary>「配置位置：…」那一行。</summary>
    private void RefreshConfigHint()
    {
        var settings = App.Settings;
        var dir = Path.GetDirectoryName(settings.FilePath) ?? settings.FilePath;
        ConfigHint.Text = settings.UsingFallbackDir
            ? Localizer.Tr("配置位置：{path}（程序目录写不了，已改存在这里）", ("path", dir))
            : Localizer.Tr("配置位置：{path}", ("path", dir));
    }

    // 关于

    // 调试卡（只有 Debug 构建里可见）

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

    /// <summary>打开「开发版提示窗」。</summary>
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
    /// 「检查更新」：查 → 下载 → 校验 → 交给脚本替换；⚠️ 流程本身在
    /// `Infrastructure/UpdateCheck.cs`：关于对话框里那颗「检查更新」 要的是同一套东西，抄第二份的
    /// 话两处迟早会不一样。
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

    // tab 切换

    private void OnTabClick(object sender, RoutedEventArgs e)
        => ShowPane((sender as FrameworkElement)?.Tag as string);

    /// <summary>
    /// 切 tab（启动 / 个性化 / 其他）；⚠️ 表驱动：tab、面板、滚动条三者的对应关系写在一张表里，加
    /// 一栏只改这一处。
    /// </summary>
    private void ShowPane(string? key)
    {
        ActiveTabKey = key switch
        {
            "personalize" => "personalize",
            "other" => "other",
            _ => "launch",
        };

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

    // 自动化截图的子段入口

    /// <summary>`--page settings:&lt;子段&gt;`：滚到某张卡片；⚠️ 页面里已经没有 DirBox。</summary>
    public void ShowSection(string section)
    {
        if (string.Equals(section, "aboutdialog", StringComparison.Ordinal))
        {
            _aboutDialog = new AboutDialog();
            return;
        }

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
    /// 离屏截图时打开的那个对话框实例（`--page settings:aboutdialog`、`--page personalize:anim`
    /// 建的）。
    /// </summary>
    public Window? ActiveDialog => _aboutDialog ?? Personalize.ActiveDialog;

    /// <summary>当前停在哪个标签页（launch / personalize / other）；切语言重建页面时用来回到原位。</summary>
    public string ActiveTabKey { get; private set; } = "launch";

    private Window? _aboutDialog;

    /// <summary>
    /// 布局排完之后再滚；⚠️ 不能挂在 `Loaded` 上：离屏截图**不 Show 窗口**，`Loaded` 不一定会来；
    /// 而 `LayoutUpdated` 在 App 手动 Measure/Arrange 那一趟之后一定会来。
    /// </summary>
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_pendingSection is null || _sectionApplied)
        {
            return;
        }
        _sectionApplied = true;

        // ⚠️ 目标类型要**显式写出来**：几个分支是互不相干的控件类型（ComboBox / MemoryBar /
        // CheckBox / Button），switch 表达式推不出共同类型，会报 CS8506。
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
        // 所以滚的是**当前这一栏**的 ScrollViewer，不再是原来那一个 PageScroll。
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
