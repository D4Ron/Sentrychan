using Avalonia.Controls;
using Sentrychan.UI.ViewModels;

namespace Sentrychan.UI.Views.Dialogs;

public partial class ReleaseDetailsDialog : Window
{
    public ReleaseDetailsDialog()
    {
        InitializeComponent();
    }

    public ReleaseDetailsDialog(ReleaseDetailsViewModel vm) : this()
    {
        DataContext = vm;
        vm.CloseRequested += Close;
        // The download action closes the dialog; the caller wired the actual enqueue.
        vm.DownloadRequested += Close;
    }
}
