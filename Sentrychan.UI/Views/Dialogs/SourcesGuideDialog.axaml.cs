using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sentrychan.UI.Services;
using Sentrychan.UI.ViewModels;
using System.Linq;

namespace Sentrychan.UI.Views.Dialogs;

/// <summary>The sources guide. Closes with where to go next (<see cref="SourcesGuideExit"/>).</summary>
public partial class SourcesGuideDialog : Window
{
    public SourcesGuideDialog()
    {
        InitializeComponent();

        // The guide is modal, so the main window's drop target can't be reached while it's open.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        });
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            var paths = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths is not { Count: > 0 } || DataContext is not SourcesGuideViewModel vm) return;
            e.Handled = true;
            await vm.ImportAsync(paths);
        });
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (DataContext is SourcesGuideViewModel vm) await vm.SaveDismissalAsync();
    }

    private void OnDone(object? sender, RoutedEventArgs e) => Close(SourcesGuideExit.Close);
    private void OnFeedSettings(object? sender, RoutedEventArgs e) => Close(SourcesGuideExit.FeedSettings);
    private void OnSourceSettings(object? sender, RoutedEventArgs e) => Close(SourcesGuideExit.SourceSettings);
}
