using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sentrychan.UI.Views.Dialogs;

public partial class AddSeriesDialog : Window
{
    public AddSeriesDialog()
    {
        InitializeComponent();
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
        => Close(null);
}