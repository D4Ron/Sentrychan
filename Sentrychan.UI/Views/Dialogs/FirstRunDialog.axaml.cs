using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.Linq;
using System.Threading.Tasks;

namespace Sentrychan.UI.Views.Dialogs;

/// <summary>
/// Shown on first launch (or whenever the two core folders are unconfigured).
/// Returns a (DownloadPath, LibraryPath) tuple, or null if skipped.
/// </summary>
public partial class FirstRunDialog : Window
{
    public FirstRunDialog()
    {
        InitializeComponent();
    }

    private async void OnBrowseDownload(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Select your downloads folder");
        if (path != null) DownloadBox.Text = path;
    }

    private async void OnBrowseLibrary(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Select your anime library folder");
        if (path != null) LibraryBox.Text = path;
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        return folders?.FirstOrDefault()?.TryGetLocalPath();
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
        => Close((DownloadBox.Text ?? string.Empty, LibraryBox.Text ?? string.Empty));

    private void OnSkip(object? sender, RoutedEventArgs e) => Close(null);
}
