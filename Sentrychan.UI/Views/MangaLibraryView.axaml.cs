using System;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Sentrychan.UI.ViewModels;
using Sentrychan.UI.Views.Dialogs;

namespace Sentrychan.UI.Views;

public partial class MangaLibraryView : UserControl
{
    public MangaLibraryView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is not MangaLibraryViewModel vm) return;

        // Give the VM a way to open the preview dialog with this window as owner.
        vm.PreviewHandler = async (result, source) =>
        {
            var owner = this.FindAncestorOfType<Window>();
            if (owner == null) return;
            var choice = await new MangaPreviewDialog(result, source).ShowDialog<string?>(owner);
            // "Read" opens the in-app reader without adding the title to the library.
            if (choice == "read" && vm.PreviewReadHandler != null)
                await vm.PreviewReadHandler(result, source);
        };
    }

    // Infinite scroll: pull the next page automatically as the results near the bottom,
    // so browsing/searching doesn't need repeated "Load more" clicks.
    private void ResultsScroll_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv || DataContext is not MangaLibraryViewModel vm) return;
        if (!vm.CanLoadMore || vm.IsLoadingMore) return;

        // Within ~600px of the bottom (and actually scrollable) → load the next page.
        var nearBottom = sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - 600;
        if (nearBottom && sv.Extent.Height > sv.Viewport.Height)
            vm.LoadMoreCommand.Execute().Subscribe();
    }
}
