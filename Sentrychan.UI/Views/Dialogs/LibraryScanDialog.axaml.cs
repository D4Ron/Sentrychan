using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sentrychan.UI.Views.Dialogs;

public partial class LibraryScanDialog : Window
{
    public LibraryScanDialog()
    {
        InitializeComponent();
    }

    private void OnDone(object? sender, RoutedEventArgs e) => Close();
}
