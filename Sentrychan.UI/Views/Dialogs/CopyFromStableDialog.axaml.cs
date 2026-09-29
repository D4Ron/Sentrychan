using Avalonia.Controls;
using Avalonia.Interactivity;
using Sentrychan.Core;
using Sentrychan.Core.Services;
using System;
using System.Threading.Tasks;

namespace Sentrychan.UI.Views.Dialogs;

/// <summary>
/// The preview's first-run offer to copy the stable library. Closes with true once a copy is
/// staged (the app then restarts to use it), false for "Start fresh", or null if dismissed —
/// in which case it's offered again next launch.
/// </summary>
public partial class CopyFromStableDialog : Window
{
    private const string CloseStableFirst =
        "Close Sentrychan first — its library is copied only while it isn't running, so the copy " +
        "can't catch it halfway through a change. Then press Copy my library again.";

    public CopyFromStableDialog()
    {
        InitializeComponent();
        if (InstanceGuard.PausedForOtherInstance) ShowNotice(CloseStableFirst);
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        // Checked now rather than at startup: the user may have closed it since.
        if (InstanceGuard.IsHeldElsewhere(InstanceGuard.StableInstanceMutex))
        {
            ShowNotice(CloseStableFirst);
            return;
        }

        SetBusy(true);
        ShowNotice("Copying your library…");
        try
        {
            await Task.Run(() => StableLibraryCopy.Stage(AppPaths.StableDataDir, AppPaths.DataDir));
            Close(true);
        }
        catch (Exception ex)
        {
            ShowNotice($"Couldn't copy the library: {ex.Message}");
            SetBusy(false);
        }
    }

    private void OnStartFresh(object? sender, RoutedEventArgs e) => Close(false);

    private void ShowNotice(string text)
    {
        NoticeText.Text = text;
        NoticeBox.IsVisible = true;
    }

    private bool _busy;

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CopyButton.IsEnabled = !busy;
        FreshButton.IsEnabled = !busy;
    }

    // A copy finishing after the dialog is gone would be applied on the next launch without the
    // user having seen it succeed.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_busy) e.Cancel = true;
        base.OnClosing(e);
    }
}
