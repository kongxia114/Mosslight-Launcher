using System.Windows;

namespace Mosslight.App.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string headline, string message, string confirmText)
    {
        InitializeComponent();
        HeadlineText.Text = headline;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
    }

    public static bool Show(Window? owner, string headline, string message, string confirmText)
    {
        var dialog = new ConfirmDialog(headline, message, confirmText);
        var shell = MainWindow.Current;
        if (owner is not null && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        shell?.SetDangerScrim(true);
        try
        {
            return dialog.ShowDialog() == true;
        }
        finally
        {
            shell?.SetDangerScrim(false);
        }
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
