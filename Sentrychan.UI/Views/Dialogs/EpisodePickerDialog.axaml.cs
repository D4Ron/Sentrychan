using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sentrychan.UI.Views.Dialogs;

public partial class EpisodePickerDialog : Window
{
    public EpisodePickerDialog()
    {
        InitializeComponent();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(-1); // Return -1 for cancellation
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var value = (int)(EpisodeInput.Value ?? 0);
        Close(value);
    }
}
