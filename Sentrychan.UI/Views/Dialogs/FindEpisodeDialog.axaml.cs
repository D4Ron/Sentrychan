using Avalonia.ReactiveUI;
using ReactiveUI;
using Sentrychan.UI.ViewModels;
using System;
using System.Reactive.Linq;

namespace Sentrychan.UI.Views.Dialogs;

public partial class FindEpisodeDialog : ReactiveWindow<FindEpisodeViewModel>
{
    public FindEpisodeDialog()
    {
        InitializeComponent();
        if (Avalonia.Controls.Design.IsDesignMode) return;

        this.WhenActivated(d =>
        {
            if (ViewModel == null) return;
            d(ViewModel.DownloadCommand.Subscribe(pick => Close(pick)));
            d(ViewModel.CancelCommand.Subscribe(_ => Close(null)));

            // Open straight onto results for the next episode, any group.
            ViewModel.SearchCommand.Execute().Subscribe();
        });
    }
}
