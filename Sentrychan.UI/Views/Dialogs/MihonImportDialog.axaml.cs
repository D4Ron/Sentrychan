using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sentrychan.UI.Views.Dialogs;

public partial class MihonImportDialog : Window
{
    public MihonImportDialog()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
