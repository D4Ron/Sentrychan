using Avalonia.Controls;
using Sentrychan.UI.ViewModels;

namespace Sentrychan.UI.Views.Dialogs;

public partial class CreateWatchPartyDialog : Window
{
    public CreateWatchPartyDialog()
    {
        InitializeComponent();
        
        DataContextChanged += (s, e) =>
        {
            if (DataContext is CreateWatchPartyViewModel vm)
            {
                vm.BrowseCommand.Subscribe(async _ => await OpenFilePickerAsync());
                vm.CancelCommand.Subscribe(_ => Close(false));
                vm.ProceedToLobbyCommand.Subscribe(_ => Close(true));
                vm.CopyToClipboard = CopyToClipboardAsync;
            }
        };
    }

    public async Task CopyToClipboardAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(text);
    }

    private void Cancel_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close(false);
    }

    // This is still needed because the VM doesn't have direct access to StorageProvider
    public async Task OpenFilePickerAsync()
    {
        var vm = DataContext as CreateWatchPartyViewModel;
        if (vm == null) return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;

        var result = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Select Video File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new Avalonia.Platform.Storage.FilePickerFileType("Video Files")
                {
                    Patterns = new[] { "*.mkv", "*.mp4", "*.avi", "*.m4v", "*.mov", "*.wmv" }
                }
            }
        });

        if (result.Count > 0)
        {
            vm.VideoSource = result[0].Path.LocalPath;
            vm.FileFoundAutomatically = false;
        }
    }
}
