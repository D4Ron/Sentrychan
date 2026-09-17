using Avalonia.Controls;
using Sentrychan.UI.ViewModels;

namespace Sentrychan.UI.Views.Dialogs;

public partial class AnimePreviewDialog : Window
{
    public AnimePreviewDialog()
    {
        InitializeComponent();
    }

    public AnimePreviewDialog(AnimePreviewViewModel vm) : this()
    {
        DataContext = vm;
        vm.CloseRequested += Close;
    }
}
