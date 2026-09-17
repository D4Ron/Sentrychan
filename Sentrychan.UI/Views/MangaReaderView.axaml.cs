using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sentrychan.UI.ViewModels;

namespace Sentrychan.UI.Views;

public partial class MangaReaderView : UserControl
{
    public MangaReaderView()
    {
        InitializeComponent();
        // Focus so arrow keys / space drive paging.
        AttachedToVisualTree += (_, _) => Focus();
    }

    private MangaReaderViewModel? Vm => DataContext as MangaReaderViewModel;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var vm = Vm;
        if (vm == null) { base.OnKeyDown(e); return; }

        switch (e.Key)
        {
            case Key.Right:
            case Key.Space:
            case Key.PageDown:
                vm.NextPageCommand.Execute().Subscribe();
                e.Handled = true;
                break;
            case Key.Left:
            case Key.PageUp:
                vm.PrevPageCommand.Execute().Subscribe();
                e.Handled = true;
                break;
            case Key.Escape:
                vm.CloseCommand.Execute().Subscribe();
                e.Handled = true;
                break;
            default:
                base.OnKeyDown(e);
                break;
        }
    }

    // Paged mode wheel: Ctrl+wheel zooms; plain wheel flips pages (or pans when zoomed).
    private void PagedWheel(object? sender, PointerWheelEventArgs e)
    {
        var vm = Vm;
        if (vm == null || !vm.ShowPagedImages) return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.ZoomBy(e.Delta.Y > 0 ? 0.25 : -0.25);
            e.Handled = true;
            return;
        }

        if (vm.ZoomLevel <= 1.01)
        {
            // Not zoomed → wheel turns the page.
            if (e.Delta.Y > 0) vm.PrevPageCommand.Execute().Subscribe();
            else vm.NextPageCommand.Execute().Subscribe();
            e.Handled = true;
        }
        else if (this.FindControl<ScrollViewer>("PageScroll") is { } sv)
        {
            // Zoomed → pan the page vertically.
            sv.Offset = new Vector(sv.Offset.X, sv.Offset.Y - e.Delta.Y * 90);
            e.Handled = true;
        }
    }

    // Long-strip: mark the chapter read once the reader is scrolled near the bottom.
    private void StripScroll_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - 40 && sv.Extent.Height > sv.Viewport.Height)
            Vm?.OnReachedEnd();
    }
}
