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
        // Filled in with the usual places, so "Get started" works without browsing. "Skip" used to
        // leave the library unset, and then nothing that downloaded was ever filed.
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var videos = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyVideos);
        DownloadBox.Text = System.IO.Path.Combine(home, "Downloads");
        LibraryBox.Text = System.IO.Path.Combine(string.IsNullOrEmpty(videos) ? home : videos, "Anime");
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
    {
        var download = DownloadBox.Text?.Trim() ?? string.Empty;
        var library = LibraryBox.Text?.Trim() ?? string.Empty;
        // The suggested library folder usually doesn't exist yet.
        if (!Sentrychan.Core.Library.LibraryFolder.Ensure(library, out var problem, out _) && library.Length > 0)
        {
            Problem.Text = "The library folder can't be used: " + problem;
            Problem.IsVisible = true;
            return;
        }
        Close((download, library));
    }

    private void OnSkip(object? sender, RoutedEventArgs e) => Close(null);
}
