using Avalonia.Controls;

namespace Sentrychan.UI.Views.Dialogs;

public partial class AccountDialog : Window
{
    public AccountDialog()
    {
        InitializeComponent();
    }

    private void CloseButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Close();
}
