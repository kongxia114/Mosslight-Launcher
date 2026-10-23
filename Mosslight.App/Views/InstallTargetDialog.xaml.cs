using System.IO;
using System.Windows;
using System.Windows.Controls;
using Mosslight.Core.Localization;

namespace Mosslight.App.Views;

/// <summary>
/// 「装到哪儿？」对话框：点下载之后先让用户定目录（= PCL 的做法）；⚠️ 取消时 ChosenDir 是空串，调
/// 用方必须检查 —— "空串当成了有效目录"会让文件下到进程当前目录里去。
/// </summary>
public partial class InstallTargetDialog : Window
{
    // 「自定义文件夹…」那一项的标记（不在 Tag 里放路径，避免跟真实目录混淆）。
    private const string CustomTag = "\u0000custom";

    private readonly IReadOnlyList<(string VersionId, string Dir)> _options;

    /// <summary>用户最后选定的目录（取消时是空串）。</summary>
    public string ChosenDir { get; private set; } = string.Empty;

    // 自定义的目录（选过「自定义文件夹…」才有值）。
    private string _customDir = string.Empty;

    // 铺下拉时会触发 SelectionChanged —— 那一下不算"用户改选"。
    private bool _filling;

    public InstallTargetDialog(
        IReadOnlyList<(string VersionId, string Dir)> options,
        string defaultDir,
        string? fieldLabel = null,
        string? hint = null)
    {
        InitializeComponent();
        Infrastructure.FramelessWindow.Apply(this);

        if (!string.IsNullOrEmpty(fieldLabel))
        {
            FieldLabelText.Text = fieldLabel;
        }
        if (!string.IsNullOrEmpty(hint))
        {
            HintText.Text = hint;
        }

        _options = options;

        _filling = true;
        try
        {
            foreach (var (versionId, dir) in options)
            {
                TargetBox.Items.Add(new ComboBoxItem { Content = versionId, Tag = dir });
            }
            TargetBox.Items.Add(new ComboBoxItem
            {
                Content = Localizer.Tr("自定义文件夹…"),
                Tag = CustomTag,
            });

            // 默认选中"界面上原来那个落点"（对不上就选第一个）
            var index = 0;
            for (var i = 0; i < options.Count; i++)
            {
                if (string.Equals(options[i].Dir, defaultDir, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }
            if (TargetBox.Items.Count > 0)
            {
                TargetBox.SelectedIndex = index;
            }
        }
        finally
        {
            _filling = false;
        }

        UpdatePath();
    }

    /// <summary>下拉里那一项的 Tag 是不是"自定义文件夹"。</summary>
    private static bool IsCustom(object? tag)
        => tag is string text && string.Equals(text, CustomTag, StringComparison.Ordinal);

    private void OnTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling)
        {
            return;
        }

        if (TargetBox.SelectedItem is ComboBoxItem { } item && IsCustom(item.Tag))
        {
            if (!Browse())
            {
                // 用户取消了选目录：退回上一次的选择，别把下拉停在"自定义"上却没有路径
                _filling = true;
                try
                {
                    RestoreSelection();
                }
                finally
                {
                    _filling = false;
                }
            }
        }

        UpdatePath();
    }

    /// <summary>打开文件夹选择框；选了返回 true。</summary>
    private bool Browse()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Localizer.Tr("选择文件夹"),
            Multiselect = false,
        };
        if (!string.IsNullOrEmpty(_customDir) && Directory.Exists(_customDir))
        {
            dialog.InitialDirectory = _customDir;
        }
        else if (CurrentDir().Length > 0 && Directory.Exists(CurrentDir()))
        {
            dialog.InitialDirectory = CurrentDir();
        }

        if (dialog.ShowDialog(this) != true || string.IsNullOrEmpty(dialog.FolderName))
        {
            return false;
        }

        _customDir = dialog.FolderName;
        return true;
    }

    /// <summary>取消选目录之后，把下拉退回到"当前真正生效的那个目录"。</summary>
    private void RestoreSelection()
    {
        var current = CurrentDir();
        foreach (var item in TargetBox.Items)
        {
            if (item is ComboBoxItem option
                && option.Tag is string dir
                && !IsCustom(dir)
                && string.Equals(dir, current, StringComparison.OrdinalIgnoreCase))
            {
                TargetBox.SelectedItem = option;
                return;
            }
        }
        if (TargetBox.Items.Count > 0)
        {
            // 自定义过但这次取消了：回到第一个真实版本
            TargetBox.SelectedIndex = 0;
        }
    }

    /// <summary>当前下拉对应的目录（自定义时是自定义那个）。</summary>
    private string CurrentDir()
    {
        if (TargetBox.SelectedItem is not ComboBoxItem { } item)
        {
            return string.Empty;
        }
        if (IsCustom(item.Tag))
        {
            return _customDir;
        }
        return item.Tag as string ?? string.Empty;
    }

    private void UpdatePath()
    {
        var dir = CurrentDir();
        PathText.Text = dir.Length > 0 ? dir : Localizer.Tr("还没选目录");
        ConfirmButton.IsEnabled = dir.Length > 0;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        var dir = CurrentDir();
        if (dir.Length == 0)
        {
            return;
        }

        ChosenDir = dir;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
