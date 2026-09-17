using Avalonia.Controls;
using Sentrychan.UI.ViewModels;

namespace Sentrychan.UI.Views;

public partial class SeasonalView : UserControl
{
    public SeasonalView()
    {
        InitializeComponent();
    }

    private void OnDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (sender is Control control && control.DataContext is SeasonalAnimeVm vm)
        {
            vm.OpenDetailsCommand.Execute().Subscribe();
        }
    }
}
