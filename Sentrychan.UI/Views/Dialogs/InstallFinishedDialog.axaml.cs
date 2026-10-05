using Avalonia.Controls;
using Avalonia.Interactivity;
using Sentrychan.Core;

namespace Sentrychan.UI.Views.Dialogs;

/// <summary>
/// The first start after installing: the choices an installer's last page would offer. The Mac
/// installer no longer opens the app by itself, so this is the first thing the user sees when
/// they open it. Closes with whether to open at login.
/// </summary>
public partial class InstallFinishedDialog : Window
{
    public InstallFinishedDialog()
    {
        InitializeComponent();
        Heading.Text = $"{BuildInfo.AppName} is installed";
        OpenAtLoginLabel.Text = $"Open {BuildInfo.AppName} when I log in";
    }

    private void OnContinue(object? sender, RoutedEventArgs e) => Close(OpenAtLogin.IsChecked == true);
}
