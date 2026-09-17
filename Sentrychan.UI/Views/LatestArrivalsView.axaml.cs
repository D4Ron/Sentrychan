using Avalonia.Controls;
using Avalonia.Input;
using Sentrychan.UI.ViewModels;

namespace Sentrychan.UI.Views;

public partial class LatestArrivalsView : UserControl
{
    public LatestArrivalsView()
    {
        InitializeComponent();
    }

    private void OnPosterTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: SeasonalAnimeVm card }
            && DataContext is LatestArrivalsViewModel vm)
        {
            var url = card.Anime.LargeImageUrl;
            if (!string.IsNullOrEmpty(url)) vm.ShowPreview(url);
        }
    }

    private void OnClosePreview(object? sender, TappedEventArgs e)
    {
        if (DataContext is LatestArrivalsViewModel vm) vm.ClosePreview();
    }
}
