using Avalonia.Controls;
using Avalonia.Interactivity;
using Sentrychan.UI.ViewModels;

namespace Sentrychan.UI.Views.Dialogs;

/// <summary>The sources guide. Closes with where to go next (<see cref="SourcesGuideExit"/>).</summary>
public partial class SourcesGuideDialog : Window
{
    public SourcesGuideDialog() => InitializeComponent();

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (DataContext is SourcesGuideViewModel vm) await vm.SaveDismissalAsync();
    }

    private void OnDone(object? sender, RoutedEventArgs e) => Close(SourcesGuideExit.Close);
    private void OnFeedSettings(object? sender, RoutedEventArgs e) => Close(SourcesGuideExit.FeedSettings);
    private void OnSourceSettings(object? sender, RoutedEventArgs e) => Close(SourcesGuideExit.SourceSettings);
}
