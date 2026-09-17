using Avalonia.Controls;
using Avalonia.Input;
using Sentrychan.UI.ViewModels;
using System.Reactive;
using System.Reactive.Linq;

namespace Sentrychan.UI.Views;

public partial class DownloadHubView : UserControl
{
    public DownloadHubView()
    {
        InitializeComponent();
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is DownloadHubViewModel vm)
        {
            vm.SearchCommand.Execute().Subscribe();
            e.Handled = true;
        }
    }
}
