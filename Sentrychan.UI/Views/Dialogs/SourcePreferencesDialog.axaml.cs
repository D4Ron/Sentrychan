using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sentrychan.UI.Views.Dialogs;

public partial class SourcePreferencesDialog : Window
{
    public SourcePreferencesDialog()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
