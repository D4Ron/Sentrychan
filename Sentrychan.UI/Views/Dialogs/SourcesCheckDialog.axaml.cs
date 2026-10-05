using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sentrychan.UI.Views.Dialogs;

public partial class SourcesCheckDialog : Window
{
    public SourcesCheckDialog() => InitializeComponent();

    private void OnDone(object? sender, RoutedEventArgs e) => Close();
}
