using System;
using Avalonia.Controls;
using Avalonia.Threading;
using Sentrychan.Core.Interfaces;
using Sentrychan.UI.ViewModels;

namespace Sentrychan.UI.Views.Dialogs;

public partial class MangaPreviewDialog : Window
{
    public MangaPreviewDialog()
    {
        InitializeComponent();
    }

    public MangaPreviewDialog(MangaResultVm result, IMangaSourceService source) : this()
    {
        DataContext = result;
        _ = LoadDescriptionAsync(result, source);
    }

    private async System.Threading.Tasks.Task LoadDescriptionAsync(MangaResultVm result, IMangaSourceService source)
    {
        // The search result often has a short/empty description — fetch the full one.
        string? desc = result.Result.Description;
        try
        {
            var details = await source.GetDetailsAsync(result.Result.SourceId);
            if (details != null && !string.IsNullOrWhiteSpace(details.Description))
                desc = details.Description;
        }
        catch { /* keep whatever the search gave us */ }

        Dispatcher.UIThread.Post(() =>
        {
            var tb = this.FindControl<TextBlock>("DescriptionText");
            if (tb != null)
                tb.Text = string.IsNullOrWhiteSpace(desc) ? "No description available." : desc;
        });
    }

    private void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    // Returns "read" to the caller (MangaLibraryView), which then opens the preview reader.
    private void OnRead(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close("read");
}
