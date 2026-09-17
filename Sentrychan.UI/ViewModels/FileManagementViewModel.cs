using ReactiveUI;
using System.Reactive;

namespace Sentrychan.UI.ViewModels;

public class FileManagementViewModel : ViewModelBase
{
    public DownloadsViewModel Downloads { get; }
    public UnmatchedResolverViewModel UnmatchedResolver { get; }

    private int _selectedSubTab = 0;
    public int SelectedSubTab
    {
        get => _selectedSubTab;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSubTab, value);
            this.RaisePropertyChanged(nameof(IsDownloadsTabActive));
            this.RaisePropertyChanged(nameof(IsUnmatchedTabActive));
        }
    }

    public bool IsDownloadsTabActive => _selectedSubTab == 0;
    public bool IsUnmatchedTabActive => _selectedSubTab == 1;

    public ReactiveCommand<Unit, Unit> SelectDownloadsTabCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectUnmatchedTabCommand { get; }

    public FileManagementViewModel(
        DownloadsViewModel downloads,
        UnmatchedResolverViewModel unmatchedResolver)
    {
        Downloads = downloads;
        UnmatchedResolver = unmatchedResolver;

        SelectDownloadsTabCommand = ReactiveCommand.Create(() => { SelectedSubTab = 0; });
        SelectUnmatchedTabCommand = ReactiveCommand.Create(() => { SelectedSubTab = 1; });
    }
}
