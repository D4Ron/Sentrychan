using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sentrychan.UI.Views.Dialogs;

/// <summary>
/// Asked when the user downloads a release for a series not in their library.
/// Closes with "add" (add series, then download), "download" (download into
/// the _Standalone library folder), or null (cancel).
/// </summary>
public partial class AddToLibraryPromptDialog : Window
{
    public AddToLibraryPromptDialog()
    {
        InitializeComponent();
    }

    public AddToLibraryPromptDialog(string seriesTitle) : this()
    {
        SeriesTitleText.Text = seriesTitle;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
    private void OnJustDownload(object? sender, RoutedEventArgs e) => Close("download");
    private void OnAddAndDownload(object? sender, RoutedEventArgs e) => Close("add");
}
