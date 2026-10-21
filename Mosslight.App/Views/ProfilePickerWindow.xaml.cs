using System.Collections.Generic;
using System.Windows;
using Mosslight.App.Infrastructure;
using Mosslight.Core.Services.Auth;

namespace Mosslight.App.Views;

/// <summary>登录后从多个角色里挑一个（点两下也能定）。</summary>
public partial class ProfilePickerWindow : Window
{
    public ProfilePickerWindow(IReadOnlyList<MinecraftProfile> profiles, string? preselectId = null)
    {
        InitializeComponent();
        FramelessWindow.Apply(this);

        ProfileList.ItemsSource = profiles;
        var index = 0;
        for (var i = 0; i < profiles.Count; i++)
        {
            if (string.Equals(profiles[i].Id, preselectId, System.StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }
        ProfileList.SelectedIndex = profiles.Count > 0 ? index : -1;
    }

    /// <summary>用户选中的角色；取消时为 null。</summary>
    public MinecraftProfile? Selected { get; private set; }

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        OkButton.IsEnabled = ProfileList.SelectedItem is MinecraftProfile;
    }

    private void OnListDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ProfileList.SelectedItem is MinecraftProfile)
        {
            Confirm();
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => Confirm();

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Confirm()
    {
        if (ProfileList.SelectedItem is not MinecraftProfile profile)
        {
            return;
        }
        Selected = profile;
        DialogResult = true;
    }
}
