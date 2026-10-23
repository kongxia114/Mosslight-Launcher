using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Mosslight.App.Infrastructure;
using Mosslight.Core.Localization;
using Mosslight.Core.Services;
using Mosslight.Core.Services.Engine;

namespace Mosslight.App.Views;

/// <summary>「版本管理」页。</summary>
public partial class VersionsPage : UserControl, IMosslightPage
{
    private const double RowHeight = 68;

    // 左侧徽章方块的边长。
    private const double BadgeSize = 44;

    // 类型图的边长 = 44 - 6。
    private const double BadgeIconSize = BadgeSize - 6;

    // ⚠️ 分组标题的行高**没有**写成常量：
    // 它是靠 Controls.xaml 里 `VersionGroupItem` 那个样式的 Height 给的，
    // 理由见那边的注释（VersionRowTemplate 自带 2px 上下边距，套上去标题会被切）。

    // (kind, 分组标题的源文案) —— 顺序就是界面上从上到下的顺序。
    private static readonly (VersionKind Kind, string Title)[] Groups =
    {
        (VersionKind.Vanilla, "官方版本"),
        (VersionKind.Loader, "加载器版本"),
        (VersionKind.Pack, "整合包"),
    };

    // 筛选下拉：`("all", "全部")` + 三段。
    private static readonly (string Key, string Title)[] Filters =
        new[] { ("all", "全部") }
            .Concat(Groups.Select(g => (KeyOf(g.Kind), g.Title)))
            .ToArray();

    private string _filter = "all";
    private GameVersion? _selected;
    private bool _repairing;

    // 正在装整合包 —— 期间不让再点（一次装包要下几百 MB）。
    private bool _importing;

    // 挡住"重建筛选下拉项"时被当成"用户换了筛选"。
    private bool _fillingFilter;

    // 游戏目录列表正在重建 —— 挡住 SelectedItem 那一下被当成"用户选的"。
    private bool _fillingDirs;

    // 这一页已经离开可视树了（切走 / 窗口关了），后台任务回来时别再碰控件。
    private bool _detached;

    public VersionsPage()
    {
        InitializeComponent();

        BuildFilterItems();
        HookPlaceholder();

        // 切走以后后台线程回来不能再改控件树（WPF 里对脱离可视树的元素改 Items 会抛）
        Unloaded += (_, _) => _detached = true;

        // 子段 `geometry` 要在**布局排完之后**才能量，所以挂在 LayoutUpdated 上
        // （离屏截图不 Show 窗口，Loaded 不一定来；App.SaveWindowPng 手动 Measure/Arrange
        // 那一趟之后 LayoutUpdated 一定会来 —— 跟设置页/版本设置页同一套理由）
        LayoutUpdated += OnLayoutUpdated;

        Reload();
    }

    // 子段 `geometry` 要求的布局实测还没打出来（打完就置回 false）。
    private bool _wantGeometry;

    // 布局已经过几趟（第一趟时机太早，量到的是中间态）。
    private int _layoutPasses;

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_wantGeometry || ActualWidth <= 0)
        {
            return;
        }
        // 尺寸定下来之后可能还会再排一两趟（列表项容器是这趟才生成的）。
        // 每趟都打一遍、带上趟号，看最后那一组数就行 —— 猜"哪趟是最后一趟"更容易错。
        if (++_layoutPasses > 3)
        {
            _wantGeometry = false;
            return;
        }
        DumpGeometry(_layoutPasses);
    }

    /// <summary>版本列表不做入场动画。</summary>
    public Panel? AnimationHost => null;

    /// <summary>选中的版本变了 —— 留给外面接。</summary>
    public event EventHandler<GameVersion?>? VersionSelected;

    private static string KeyOf(VersionKind kind) => kind switch
    {
        VersionKind.Vanilla => "vanilla",
        VersionKind.Loader => "loader",
        _ => "pack",
    };

    // 工具条：筛选下拉 + 搜索占位

    /// <summary>
    /// 填筛选下拉的候选项；⚠️ 文案逐个写出来，**不要**写成循环变量再塞进 Localizer.Tr：那样字面量
    /// 是经由变量进去的，文案提取工具认不出来。
    /// </summary>
    private void BuildFilterItems()
    {
        _fillingFilter = true;
        try
        {
            var index = KindFilter.SelectedIndex;
            KindFilter.Items.Clear();
            foreach (var (key, title) in Filters)
            {
                KindFilter.Items.Add(new ComboBoxItem { Content = Localizer.Tr(title), Tag = key });
            }
            KindFilter.SelectedIndex = index >= 0 ? index : 0;
        }
        finally
        {
            _fillingFilter = false;
        }
    }

    /// <summary>
    /// 接上"输入框空着就显示占位文案"（WPF 的 TextBox 没有 Qt 的 setPlaceholderText）。
    /// </summary>
    private void HookPlaceholder()
    {
        void Sync()
        {
            SearchPlaceholder.Visibility =
                SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            // QLineEdit.setClearButtonEnabled(True)：有字才显示那个 ✕
            ClearSearchButton.Visibility =
                SearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        SearchBox.TextChanged += (_, _) => Sync();
        Sync();
    }

    // 列表：筛选 + 分组 + 重建

    /// <summary>当前生效的筛选 key（`all` / `vanilla` / `loader` / `pack`）。</summary>
    private string CurrentFilter()
    {
        var index = KindFilter.SelectedIndex;
        return index >= 0 && index < Filters.Length ? Filters[index].Key : "all";
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingFilter)
        {
            return;
        }
        _filter = CurrentFilter();
        Reload();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        // 占位文案的显示与否由 HookPlaceholder 挂的另一个处理器管，这里只管重建列表
        Reload();
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        SearchBox.Focus();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = ReloadAsync();

    /// <summary>
    /// 重新读版本、按 kind 分组重画；⚠️ 顺序：先筛选 + 关键词，再三段分组，空组不画标题。
    /// </summary>
    public void Reload()
    {
        var all = App.Services.Games.Installed;

        var keyword = SearchBox.Text.Trim();
        _filter = CurrentFilter();

        var rows = all
            .Where(v => _filter == "all" || _filter == KeyOf(v.Kind))
            .Where(v => keyword.Length == 0
                        || v.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var wanted = _selected?.Id ?? App.Settings.LastVersion;
        _selected = null;

        VersionList.Items.Clear();
        foreach (var (kind, title) in Groups)
        {
            var group = rows.Where(v => v.Kind == kind).ToList();
            if (group.Count == 0)
            {
                continue;
            }

            VersionList.Items.Add(new ListBoxItem
            {
                Content = new TextBlock
                {
                    Text = $"{Localizer.Tr(title)}    {group.Count}",
                    Style = (Style)FindResource("VersionGroupTitle"),
                },
                Style = (Style)FindResource("VersionGroupItem"),
                IsEnabled = false,
                Focusable = false,
            });

            foreach (var version in group)
            {
                VersionList.Items.Add(new ListBoxItem
                {
                    Tag = version,
                    Content = BuildRow(version),
                    Height = RowHeight,
                });
            }
        }

        // 尽量保住原来选中那个；冷启动用配置里的 last_version，找不到就第一个
        if (!SelectById(wanted))
        {
            SelectFirstVersion();
        }

        RefreshFooter(all.Count, rows.Count);
        RefreshDetail();
        RefreshDirList();
    }

    /// <summary>
    /// 列表底下那行小字；⚠️ 一个版本都没有时**不是**显示"没找到版本"就完了 —— 要把当前游戏目录报
    /// 出来，不然用户不知道版本该往哪儿放。
    /// </summary>
    private void RefreshFooter(int total, int shown)
    {
        if (total == 0)
        {
            ListFooter.Text = Localizer.Tr(
                "没有扫描到版本 —— 当前游戏目录：{path}",
                ("path", App.Services.Games.MinecraftDir));
        }
        else if (shown == total)
        {
            ListFooter.Text = Localizer.Tr("共 {n} 个本地版本", ("n", total));
        }
        else
        {
            ListFooter.Text = Localizer.Tr(
                "共 {n} 个本地版本，符合筛选的 {m} 个", ("n", total), ("m", shown));
        }
    }

    private bool SelectById(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }
        foreach (var item in VersionList.Items)
        {
            if (item is ListBoxItem { Tag: GameVersion v } box && v.Id == id)
            {
                VersionList.SelectedItem = box;
                return true;
            }
        }
        return false;
    }

    private void SelectFirstVersion()
    {
        foreach (var item in VersionList.Items)
        {
            if (item is ListBoxItem { Tag: GameVersion })
            {
                VersionList.SelectedItem = item;
                return;
            }
        }
        RefreshDetail();
    }

    // 列表：一行长什么样

    /// <summary>一行版本：左侧方块图标 + 名称/元信息 + 右侧标签。</summary>
    private FrameworkElement BuildRow(GameVersion version)
    {
        var row = new Grid { Margin = new Thickness(10, 6, 12, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = BuildBadge(version);
        Grid.SetColumn(badge, 0);
        row.Children.Add(badge);

        var text = new StackPanel
        {
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.Children.Add(new TextBlock
        {
            Text = version.Name,
            Style = (Style)FindResource("VersionRowName"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = RowMetaText(version),
            Style = (Style)FindResource("VersionRowMeta"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        // 行尾标签：缺 jar 优先（那是"起不来"），否则显示加载器名
        Border? tag = null;
        if (!version.Complete)
        {
            // ⚠️ “⚠ 缺少 jar” 里的前导空格是对齐用的，不要 trim。
            tag = MakeTag("⚠ 缺少 jar", "BadgeWarn");
        }
        else if (!string.IsNullOrEmpty(version.LoaderKey))
        {
            tag = MakeTag(version.Loader, "BadgeAccent");
        }

        if (tag is not null)
        {
            Grid.SetColumn(tag, 2);
            row.Children.Add(tag);
        }
        return row;
    }

    /// <summary>左侧那个方块。</summary>
    private FrameworkElement BuildBadge(GameVersion version)
    {
        var iconName = VersionIcons.NameFor(
            version.Type,
            version.LoaderKey ?? string.Empty,
            version.Kind == VersionKind.Pack);

        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var image = VersionIcons.Load(iconName, (int)BadgeIconSize, dpi);
        if (image is not null)
        {
            var box = new Border
            {
                Style = (Style)FindResource("BadgeIcon"),
                Width = BadgeSize,
                Height = BadgeSize,
            };
            var picture = new Image
            {
                Source = image,
                Width = BadgeIconSize,
                Height = BadgeIconSize,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // 分步缩已经在 VersionIcons 里做过了，这里别再让它插值一次
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            box.Child = picture;
            return box;
        }

        var badge = new Border
        {
            Style = (Style)FindResource(version.Kind switch
            {
                VersionKind.Vanilla => "BadgeVanilla",
                VersionKind.Loader => "BadgeLoader",
                _ => "BadgePack",
            }),
            Width = BadgeSize,
            Height = BadgeSize,
        };
        badge.Child = new TextBlock
        {
            Text = BadgeText(version),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
        };
        return badge;
    }

    /// <summary>
    /// 认不出类型时方块里的字：原版「原」、整合包「包」、**加载器用加载器名前两位大写**（`Fabric`
    /// → `FA`）—— 不是首字母，两位才够区分 Fabric / Forge / NeoForge。
    /// </summary>
    private static string BadgeText(GameVersion version) => version.Kind switch
    {
        VersionKind.Vanilla => Localizer.Tr("原"),
        VersionKind.Pack => Localizer.Tr("包"),
        _ => version.Loader.Length >= 2
            ? version.Loader[..2].ToUpperInvariant()
            : version.Loader.ToUpperInvariant(),
    };

    private static Border MakeTag(string text, string styleKey)
    {
        var tag = new Border
        {
            Style = (Style)Application.Current.FindResource(styleKey),
            VerticalAlignment = VerticalAlignment.Center,
        };
        tag.Child = new TextBlock { Text = text };
        return tag;
    }

    /// <summary>
    /// **列表行**里的元信息：`正式版 · Java 21 · 版本隔离 · `；⚠️ 它跟**详情卡**那行
    /// **不是同一份**，别合并：行里**不写加载器名**（右边已经有徽章了），但文件夹名跟显示名不一样
    /// 时要补上；详情卡反过来 —— 写加载器名，不写文件夹名（下面那行本来就是完整路径）。
    /// </summary>
    private static string RowMetaText(GameVersion version)
    {
        var parts = new List<string> { VersionScanner.TypeLabel(version.Type) };

        if (version.JavaMajor is > 0)
        {
            parts.Add($"Java {version.JavaMajor}");
        }
        if (version.Isolated)
        {
            parts.Add(Localizer.Tr("版本隔离"));
        }
        if (version.DirName != version.Name && version.DirName.Length > 0)
        {
            parts.Add(version.DirName);
        }
        return string.Join("    ·    ", parts);
    }

    /// <summary>**详情卡**里的元信息：`正式版 · Fabric · Java 21 · 版本隔离`。</summary>
    private static string DetailMetaText(GameVersion version)
    {
        var parts = new List<string> { VersionScanner.TypeLabel(version.Type) };

        if (!string.IsNullOrEmpty(version.LoaderKey))
        {
            parts.Add(version.Loader);
        }
        if (version.JavaMajor is > 0)
        {
            parts.Add($"Java {version.JavaMajor}");
        }
        if (version.Isolated)
        {
            parts.Add(Localizer.Tr("版本隔离"));
        }
        return string.Join("    ·    ", parts);
    }

    // 选中 / 双击 / 回车

    private void OnVersionSelected(object sender, SelectionChangedEventArgs e)
    {
        // 分组标题不可选，但保险起见还是判一下类型
        _selected = VersionList.SelectedItem is ListBoxItem { Tag: GameVersion version }
            ? version
            : null;

        if (_selected is not null)
        {
            App.Settings.LastVersion = _selected.Id;
        }

        RefreshDetail();
        VersionSelected?.Invoke(this, _selected);
    }

    /// <summary>
    /// 双击一个版本 = 选中它 + 切回启动页；⚠️ 得先确认这一下**打在版本行上**：MouseDoubleClick 在
    /// 列表的空白处、分组标题上也会冒泡上来，不判一下的话"双击空白"会莫名其妙跳回启动页。
    /// </summary>
    private void OnVersionActivated(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindVersionItem(source) is null)
        {
            return;
        }
        ActivateSelected();
    }

    /// <summary>回车 = 双击。</summary>
    private void OnVersionListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ActivateSelected();
            e.Handled = true;
        }
    }

    private void ActivateSelected()
    {
        if (_selected is null)
        {
            return;
        }
        App.Settings.LastVersion = _selected.Id;
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.ShowPage("home");
        }
    }

    /// <summary>从鼠标落点往上找那个"装着版本"的列表项（找不到说明没打在版本行上）。</summary>
    private static ListBoxItem? FindVersionItem(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is ListBoxItem { Tag: GameVersion } item)
            {
                return item;
            }
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    // 版本详情卡

    private void RefreshDetail()
    {
        DetailProgress.Text = string.Empty;

        if (_selected is null)
        {
            DetailName.Text = Localizer.Tr("未找到本地版本");
            DetailMeta.Text = string.Empty;
            DetailPath.Text = string.Empty;
            SetLibraries(string.Empty, warn: false);
            SettingsButton.IsEnabled = false;
            FolderButton.IsEnabled = false;
            RepairButton.IsEnabled = false;
            return;
        }

        SettingsButton.IsEnabled = true;
        FolderButton.IsEnabled = true;
        DetailName.Text = _selected.Name;
        DetailMeta.Text = DetailMetaText(_selected);
        DetailPath.Text = _selected.Path;

        RefreshLibraries();
    }

    /// <summary>
    /// 详情卡里"缺哪些库"那一行；⚠️ 读的是**合并后**的版本 JSON（VersionJsonLoader.Load）：加载器
    /// 版本自己那份里只有它自己的库，原版那几十个在父版本里 —— 只看自己那份会得出"什么都不缺"的错
    /// 误结论。
    /// </summary>
    private void RefreshLibraries()
    {
        if (_selected is null)
        {
            return;
        }

        if (!_selected.Complete)
        {
            SetLibraries(Localizer.Tr("缺少客户端 jar，这个版本启动不了"), warn: true);
            RepairButton.IsEnabled = !_repairing;
            return;
        }

        try
        {
            var mcDir = App.Services.Games.MinecraftDir;
            var merged = VersionJsonLoader.Load(mcDir, _selected.Id);
            var missing = GameRepair.MissingLibraries(mcDir, merged.Raw);

            SetLibraries(
                missing.Count == 0
                    ? Localizer.Tr("库都齐了")
                    : Localizer.Tr("缺 {n} 个库", ("n", missing.Count)),
                warn: missing.Count > 0);
        }
        catch (Exception ex) when (ex is VersionJsonException or IOException
                                      or UnauthorizedAccessException or NotSupportedException)
        {
            SetLibraries(Localizer.Tr("读不了版本 JSON：{err}", ("err", ex.Message)), warn: true);
            RepairButton.IsEnabled = false;
            return;
        }

        RepairButton.IsEnabled = !_repairing;
    }

    /// <summary>「缺什么」那一行的两种色调。</summary>
    private void SetLibraries(string text, bool warn)
    {
        DetailLibs.Text = text;
        DetailLibsHost.Style = warn ? (Style)FindResource("BadgeWarn") : null;
        DetailLibs.Foreground = (Brush)FindResource(warn ? "WarnBrush" : "TextFaintBrush");
    }

    // 详情卡的三个按钮

    private void OnVersionSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null || Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        // ⚠️ 这一页是**切页**不是弹窗，所以要**明确告诉它改哪个版本** ——
        // 它自己只认"上次启动的版本"，而用户可能只是在列表里点了一下看看。
        // 切过去之前先把页面建出来拿到实例（ShowPage 内部也会建一次，
        // 但那一次我们够不着）。
        main.ShowPage("versionsettings");
        if (main.PageHost.Content is VersionSettingsPage settings)
        {
            settings.Configure(_selected);
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }
        var path = _selected.Path;
        var target = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(target) || !Directory.Exists(target))
        {
            DetailProgress.Text = Localizer.Tr("这个文件夹不在了：{path}", ("path", path));
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            DetailProgress.Text = Localizer.Tr("打不开文件夹：{err}", ("err", ex.Message));
        }
    }

    /// <summary>
    /// 「补全缺失的文件」：把版本 JSON 里声明了下载地址、但本地没有的库下回来；⚠️ 必须丢到后台线
    /// 程：GameRepair.RepairAsync 里是同步下载，在 UI 线程上直接 await 会把界面卡死（而且下几十
    /// MB 是常事）。
    /// </summary>
    private void OnRepairClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _repairing)
        {
            return;
        }

        var version = _selected;
        var mcDir = App.Services.Games.MinecraftDir;

        _repairing = true;
        // 按钮自己也要变样：否则点下去毫无反馈，用户以为没反应
        RepairButton.IsEnabled = false;
        RepairButton.Content = Localizer.Tr("补全中…");
        VersionList.IsEnabled = false;
        // 反馈要**贴在按钮附近** —— detail_libs 就在这一排按钮下面
        SetLibraries(Localizer.Tr("正在检查缺哪些文件…"), warn: false);
        DetailProgress.Text = Localizer.Tr("正在检查缺哪些文件…");

        _ = Task.Run(async () =>
        {
            string message;
            try
            {
                var merged = VersionJsonLoader.Load(mcDir, version.Id);
                var result = await GameRepair.RepairAsync(
                    mcDir, merged.Raw,
                    onLine: line => Dispatcher.Invoke(() => DetailProgress.Text = line),
                    maxWorkers: App.Settings.EffectiveThreads,
                    source: App.Settings.DownloadSource);

                message = result.Total == 0
                    ? Localizer.Tr("检查完了，文件都是齐的")
                    : result.Ok
                        ? Localizer.Tr("补全完成，补了 {n} 个文件", ("n", result.Repaired))
                        : Localizer.Tr("补全结束，但有 {n} 个文件失败", ("n", result.Failed.Count));
            }
            catch (Exception ex) when (ex is VersionJsonException or IOException
                                          or UnauthorizedAccessException or InvalidOperationException)
            {
                message = Localizer.Tr("补全失败：{err}", ("err", ex.Message));
            }

            Dispatcher.Invoke(() =>
            {
                _repairing = false;
                RepairButton.Content = Localizer.Tr("补全缺失的文件");
                VersionList.IsEnabled = true;
                if (_detached)
                {
                    return;
                }
                RefreshLibraries();
                // 结果贴在按钮下面（跟"缺 N 个库"拼在一起），不然用户不知道点完发生了什么
                var bits = new[] { DetailLibs.Text, message }
                    .Where(b => !string.IsNullOrEmpty(b));
                SetLibraries(string.Join("　·　", bits), warn: false);
                DetailProgress.Text = message;
            });
        });
    }

    // 游戏目录

    /// <summary>固定两项：官方启动器文件夹 + 启动器目录旁边。</summary>
    private static IEnumerable<(string Label, string Path)> Presets()
    {
        // 文案直接写在 Tr() 里，提取工具才认得出
        yield return (Localizer.Tr("官方启动器文件夹"), VersionScanner.OfficialMinecraftDir());
        yield return (Localizer.Tr("启动器目录旁边"), Path.Combine(App.BaseDir, ".minecraft"));
    }

    /// <summary>列表里那一项的标题。</summary>
    private static string DirLabel(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var name = Path.GetFileName(full);
            if (string.Equals(name, ".minecraft", StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetFileName(Path.GetDirectoryName(full) ?? string.Empty);
                return parent.Length > 0 ? parent : path;
            }
            return name.Length > 0 ? name : path;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static bool SameDir(string a, string b)
        => string.Equals(
            Normalize(a),
            Normalize(b),
            StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).Replace('/', '\\').TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (path ?? string.Empty).Replace('/', '\\').TrimEnd('\\');
        }
    }

    /// <summary>把"认识的目录"填进列表：固定两项 + 记住用过的 + 当前在用的。</summary>
    private void RefreshDirList()
    {
        var current = App.Services.Games.MinecraftDir;
        var entries = new List<(string Label, string Path)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (label, path) in Presets())
        {
            if (seen.Add(Normalize(path)))
            {
                entries.Add((label, path));
            }
        }
        foreach (var text in App.Settings.KnownMinecraftDirs)
        {
            if (!string.IsNullOrWhiteSpace(text) && seen.Add(Normalize(text)))
            {
                entries.Add((DirLabel(text), text));
            }
        }
        // 当前在用的那个不在上面任何一项里 → 补一项「当前使用」
        if (!string.IsNullOrWhiteSpace(current) && seen.Add(Normalize(current)))
        {
            entries.Add((Localizer.Tr("当前使用"), current));
        }

        _fillingDirs = true;
        try
        {
            DirList.Items.Clear();
            ListBoxItem? currentItem = null;
            foreach (var (label, path) in entries)
            {
                var item = new ListBoxItem
                {
                    Tag = path,
                    ToolTip = path,
                    Content = BuildDirRow(label, path),
                };
                DirList.Items.Add(item);
                if (SameDir(path, current))
                {
                    currentItem = item;
                }
            }
            DirList.SelectedItem = currentItem;
        }
        finally
        {
            _fillingDirs = false;
        }

        RefreshDirHint();
    }

    private FrameworkElement BuildDirRow(string label, string path)
    {
        var box = new StackPanel { Margin = new Thickness(10, 3, 10, 3) };
        box.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("DirRowName"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        box.Children.Add(new TextBlock
        {
            Text = path,
            Style = (Style)FindResource("DirRowPath"),
            Margin = new Thickness(0, 1, 0, 0),
        });
        return box;
    }

    private void RefreshDirHint()
    {
        var path = App.Services.Games.MinecraftDir;
        var exists = SafeDirExists(path);

        CreateDirButton.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;
        DirHint.Text = exists
            ? Localizer.Tr("当前游戏目录：{path}", ("path", path))
            : Localizer.Tr("⚠ {path} 不存在 —— 点「新建 .minecraft 文件夹」就地建一个。",
                ("path", path));
    }

    private static bool SafeDirExists(string path)
    {
        try
        {
            return path.Length > 0 && Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void OnDirSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingDirs || DirList.SelectedItem is not ListBoxItem { Tag: string path })
        {
            return;
        }

        if (!SafeDirExists(path))
        {
            // ⚠️ 这里必须问一句，不能"选了就认"。
            // 列表里有「启动器目录旁边」这种**算出来的、可能还不存在**的项；
            // 不问一句就落盘的话，下载会装到那个目录里，而界面还高亮着官方目录 ——
            // "高亮的位置"和"实际在用的位置"不一致，用户是看不出来的。
            var answer = MessageBox.Show(
                Window.GetWindow(this),
                Localizer.Tr("{path}\n\n这个目录还不存在，现在建一个吗？", ("path", path)),
                Localizer.Tr("目录不存在"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);

            if (answer != MessageBoxResult.Yes)
            {
                // 不建就**别切**：把选中项退回真正在用的那个，
                // 不然高亮和实际用的不一致，等于骗人
                RefreshDirList();
                return;
            }
            try
            {
                Directory.CreateDirectory(path);
                Directory.CreateDirectory(Path.Combine(path, "versions"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                MessageBox.Show(Window.GetWindow(this), ex.Message,
                    Localizer.Tr("创建失败"), MessageBoxButton.OK, MessageBoxImage.Warning);
                RefreshDirList();
                return;
            }
        }

        ApplyDir(path);
    }

    /// <summary>把游戏目录切过去并重扫。</summary>
    private void ApplyDir(string pathText)
    {
        // 归一化：浏览框给的是正斜杠，存进配置前统一成原生写法
        var path = Normalize(pathText);
        App.Settings.MinecraftDir = path;
        // 记进"用过的列表"：下次打开还在列表里，不用重新浏览一遍
        App.Settings.RememberMinecraftDir(path);
        RefreshDirHint();
        _ = ReloadAsync();
    }

    private void OnAddDirClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Localizer.Tr("选择 .minecraft 目录"),
            Multiselect = false,
        };
        var current = App.Services.Games.MinecraftDir;
        if (SafeDirExists(current))
        {
            dialog.InitialDirectory = current;
        }
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var chosen = dialog.FolderName;
        // 选错目录（比如指到了桌面上）会让"扫不到版本"变成一个谜，
        // 所以这里先问一句 —— 但只问浏览来的，预设是可信的
        if (!SafeDirExists(Path.Combine(chosen, "versions")))
        {
            var answer = MessageBox.Show(
                Window.GetWindow(this),
                Localizer.Tr("选中的目录里没有 versions 文件夹：\n{path}\n\n仍要使用吗？",
                    ("path", chosen)),
                Localizer.Tr("目录可能不对"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        ApplyDir(chosen);
        RefreshDirList();
    }

    /// <summary>
    /// 把**当前这个**游戏目录建出来并切过去；⚠️ 目标必须是"当前这个"，不能写死成"启动器旁边那个"：
    /// 按钮的显示判据、提示语说的都是当前目录 —— 目标写死的话，用户选着别的路径点下去，建出来的
    /// 却是启动器旁边那个，提示和结果对不上。
    /// </summary>
    private void OnCreateDirClick(object sender, RoutedEventArgs e)
    {
        var path = App.Services.Games.MinecraftDir;
        try
        {
            Directory.CreateDirectory(path);
            Directory.CreateDirectory(Path.Combine(path, "versions"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message,
                Localizer.Tr("创建失败"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ApplyDir(path);
        RefreshDirList();
    }


    /// <summary>
    /// 选一个 `.mrpack` → 先解析预览 → 确认 → 装进当前游戏目录；⚠️ 先 Inspect 再确认，不直接装：
    /// `.mrpack` 是外来的 ZIP，先解析能把"这不是整合包 / 里面有**不安全的路径**"当场说出来，而不
    /// 是装到一半才失败。
    /// </summary>
    private void OnImportModpackClick(object sender, RoutedEventArgs e)
    {
        if (_importing)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localizer.Tr("选择整合包"),
            Filter = "Modrinth 整合包 (*.mrpack)|*.mrpack|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        byte[] blob;
        try
        {
            blob = File.ReadAllBytes(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DetailProgress.Text = Localizer.Tr("读不了这个文件：{err}", ("err", ex.Message));
            return;
        }

        var plan = App.Services.Modpacks.Inspect(blob, out var error);
        if (plan is null)
        {
            DetailProgress.Text = error;
            return;
        }

        var (loaderKey, loaderVersion) = plan.Loader;
        var lines = new List<string>
        {
            $"{plan.Name}    ·    {plan.VersionId}",
            Localizer.Tr("要装 {n} 个文件（{size}）",
                ("n", plan.Files.Count), ("size", DownloadManager.HumanSize(plan.TotalBytes))),
            Localizer.Tr("Minecraft {version}    ·    {loader}",
                ("version", plan.Minecraft.Length > 0 ? plan.Minecraft : "?"),
                ("loader", loaderKey.Length > 0
                    ? $"{loaderKey} {loaderVersion}"
                    : Localizer.Tr("原版"))),
        };
        // ⚠️ 安全告警要**显眼**：里面有 `../` 之类的路径时引擎已经跳过了，
        // 但必须让用户看见 —— 这是安全事件，不该静默
        lines.AddRange(plan.Warnings.Select(w => "⚠ " + w));
        lines.Add(string.Empty);
        lines.Add(Localizer.Tr("装到：{path}", ("path", App.Services.Games.MinecraftDir)));

        var answer = MessageBox.Show(
            Window.GetWindow(this), string.Join("\n", lines),
            Localizer.Tr("导入整合包"), MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        _importing = true;
        ImportDirButton.IsEnabled = false;
        DetailProgress.Text = Localizer.Tr("正在装整合包…");

        var mrpack = dialog.FileName;
        var mcDir = App.Services.Games.MinecraftDir;

        _ = Task.Run(async () =>
        {
            string message;
            string? versionId = null;
            try
            {
                var progress = new Progress<LaunchProgress>(
                    p => Dispatcher.Invoke(() => DetailProgress.Text = p.Message));
                var result = await App.Services.Modpacks.InstallAsync(new ModpackInstallRequest(
                    MrpackPath: mrpack,
                    MinecraftDir: mcDir,
                    // 用整合包自己的名字（服务里会消毒成合法目录名）
                    InstanceName: null,
                    InstallLoader: true), progress);

                message = result.Message;
                versionId = result.VersionId;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or InvalidOperationException)
            {
                message = Localizer.Tr("装整合包失败：{err}", ("err", ex.Message));
            }

            Dispatcher.Invoke(() =>
            {
                _importing = false;
                ImportDirButton.IsEnabled = true;
                DetailProgress.Text = message;
                if (_detached)
                {
                    return;
                }
                if (versionId is not null)
                {
                    // 新装好的版本马上出现在列表里，并且就是选中的那个
                    App.Settings.LastVersion = versionId;
                    _selected = null;
                }
                _ = ReloadAsync();
            });
        });
    }

    // 重扫

    /// <summary>游戏目录换了 / 装完整合包之后重扫一遍。</summary>
    private async Task ReloadAsync()
    {
        try
        {
            await App.Services.Games.RefreshAsync();
        }
        catch (Exception)
        {
            // 扫不动就保持原样（扫描器自己会把坏版本记进 ScanErrors，不抛）
        }
        if (!_detached)
        {
            Reload();
        }
    }

    // 自动化截图的子段入口

    public void ShowSection(string section)
    {
        // geometry 是"最后再做"的一步：单独给，或者用 `+` 挂在别的一段后面
        // （`dirnext+geometry`）。⚠️ 只认**结尾**那个 +geometry，不做 split ——
        // 关键词里带 `+` 是合法的（`search:C++` 这种）。
        var wantGeometry = section == "geometry"
                           || section.EndsWith("+geometry", StringComparison.Ordinal);
        if (wantGeometry)
        {
            if (section.Length > "geometry".Length)
            {
                section = section[..^"+geometry".Length];
            }
            else
            {
                section = string.Empty;
            }
            _layoutPasses = 0;
            _wantGeometry = true;
        }

        if (section.StartsWith("filter:", StringComparison.Ordinal))
        {
            var key = section["filter:".Length..];
            var index = Array.FindIndex(Filters, f => f.Key == key);
            if (index >= 0)
            {
                KindFilter.SelectedIndex = index;
            }
            return;
        }

        if (section.StartsWith("search:", StringComparison.Ordinal))
        {
            SearchBox.Text = section["search:".Length..];
            return;
        }

        switch (section)
        {
            case "emptydir":
                App.Settings.MinecraftDir =
                    Path.Combine(App.BaseDir, ".minecraft-not-installed");
                _ = ReloadAsync();
                break;

            case "dirnext":
                SelectNextDir();
                break;

            case "activate":
                ActivateSelected();
                break;
        }
    }

    /// <summary>`geometry`：把这一页的**实际布局数字**打到标准输出。</summary>
    private void DumpGeometry(int pass)
    {
        Console.WriteLine($"[geo] ---- pass {pass} ----");
        Console.WriteLine($"[geo] page            w={ActualWidth:F1} h={ActualHeight:F1}");

        void Line(string tag, FrameworkElement? element)
        {
            if (element is null)
            {
                Console.WriteLine($"[geo] {tag,-18} (null)");
                return;
            }
            try
            {
                var p = element.TransformToAncestor(this).Transform(new Point(0, 0));
                Console.WriteLine(
                    $"[geo] {tag,-18} x={p.X:F1} y={p.Y:F1} w={element.ActualWidth:F1} h={element.ActualHeight:F1}");
            }
            catch (InvalidOperationException)
            {
                Console.WriteLine($"[geo] {tag,-18} (不在可视树里)");
            }
        }

        Line("DetailCard", DetailCard);
        Line("ListCard", ListCard);
        Line("DirCard", DirCard);
        Line("SearchBox", SearchBox);
        Line("KindFilter", KindFilter);
        Line("RefreshButton", RefreshButton);
        Line("VersionList", VersionList);
        Line("ListFooter", ListFooter);
        Line("DirList", DirList);
        Line("DirHint", DirHint);
        Line("CreateDirButton", CreateDirButton);
        Line("AddDirButton", AddDirButton);
        Line("ImportDirButton", ImportDirButton);
        Line("DetailName", DetailName);
        Line("DetailLibsHost", DetailLibsHost);
        Line("SettingsButton", SettingsButton);
        Line("FolderButton", FolderButton);
        Line("RepairButton", RepairButton);

        // 列表项的实测：行高和行距只能这么量（截图里数不出"item 到 item"）
        for (var i = 0; i < VersionList.Items.Count; i++)
        {
            if (VersionList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem item)
            {
                Line($"VersionItem[{i}]", item);
            }
        }
        for (var i = 0; i < DirList.Items.Count; i++)
        {
            if (DirList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem item)
            {
                Line($"DirItem[{i}]", item);
            }
        }

        // ⚠️ BaseTextBox 模板把 Padding 算了两遍，自然高度 48.6；本页搜索框用固定 Height=37 钉住，别改全局样式。
        foreach (var (tag, styleKey) in new[]
                 {
                     ("PageTitle", "PageTitle"),
                     ("SectionTitle", "SectionTitle"),
                     ("HintText", "HintText"),
                     ("VersionRowName", "VersionRowName"),
                     ("VersionRowMeta", "VersionRowMeta"),
                     ("DirRowName", "DirRowName"),
                     ("DirRowPath", "DirRowPath"),
                 })
        {
            var probe = new TextBlock { Text = "版本管理Ag", Style = (Style)FindResource(styleKey) };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Console.WriteLine($"[geo] 字号 {tag,-16} 一行高 {probe.DesiredSize.Height:F1}  ({probe.FontSize:F0}px)");
        }

        var freshBox = new TextBox { Style = (Style)FindResource("BaseTextBox") };
        freshBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var noPadBox = new TextBox
        {
            Style = (Style)FindResource("BaseTextBox"),
            Padding = new Thickness(0),
        };
        noPadBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Console.WriteLine($"[geo] BaseTextBox 自然高 {freshBox.DesiredSize.Height:F1}"
                          + $"，去掉 Padding 后 {noPadBox.DesiredSize.Height:F1}（工具条那排的实际高 "
                          + $"{SearchBox.ActualHeight:F1}）");
    }

    /// <summary>`dirnext`：选中列表里的**下一项**，走的是 OnDirSelected 同一条路。</summary>
    private void SelectNextDir()
    {
        var count = DirList.Items.Count;
        if (count == 0)
        {
            return;
        }
        var index = DirList.SelectedIndex;
        DirList.SelectedIndex = (index + 1) % count;
    }
}
