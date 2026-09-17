using ReactiveUI;
using Sentrychan.Core.Interfaces;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;

namespace Sentrychan.UI.ViewModels;

/// <summary>A library series whose folder disappeared from disk.</summary>
public class MissingFolderRowVm : ViewModelBase
{
    public int MalId { get; }
    public string Title { get; }
    public string Detail { get; }

    public MissingFolderRowVm(SeriesScanResult result)
    {
        MalId = result.MalId;
        Title = result.Title;
        Detail = $"Expected folder \"{result.ExpectedFolder}\" not found";
    }
}

/// <summary>A folder in the library that doesn't belong to any tracked series.</summary>
public class UnknownFolderRowVm : ViewModelBase
{
    public string FolderName { get; }

    public UnknownFolderRowVm(string folderName) => FolderName = folderName;
}

/// <summary>
/// Backs the post-scan decisions dialog: series whose folders were deleted
/// outside the app, and folders on disk the app doesn't recognize.
/// </summary>
public class LibraryScanViewModel : ViewModelBase
{
    private readonly ISeriesService _seriesService;
    private readonly IAnimeApiService _apiService;
    private readonly MainWindowViewModel _mainWindowVm;

    public ObservableCollection<MissingFolderRowVm> MissingFolders { get; } = [];
    public ObservableCollection<UnknownFolderRowVm> UnknownFolders { get; } = [];

    public bool HasMissing => MissingFolders.Count > 0;
    public bool HasUnknown => UnknownFolders.Count > 0;
    public string Summary { get; }

    public ReactiveCommand<MissingFolderRowVm, Unit> RemoveSeriesCommand { get; }
    public ReactiveCommand<MissingFolderRowVm, Unit> KeepSeriesCommand { get; }
    public ReactiveCommand<UnknownFolderRowVm, Unit> AddFolderAsSeriesCommand { get; }
    public ReactiveCommand<UnknownFolderRowVm, Unit> DismissFolderCommand { get; }

    public LibraryScanViewModel(
        LibraryScanReport report,
        ISeriesService seriesService,
        IAnimeApiService apiService,
        MainWindowViewModel mainWindowVm)
    {
        _seriesService = seriesService;
        _apiService = apiService;
        _mainWindowVm = mainWindowVm;

        foreach (var missing in report.MissingFolders)
            MissingFolders.Add(new MissingFolderRowVm(missing));
        foreach (var folder in report.UnknownFolders)
            UnknownFolders.Add(new UnknownFolderRowVm(folder));

        var advanced = report.ProgressAdvances.Count;
        Summary = advanced > 0
            ? $"{advanced} series synced to files on disk. The items below need your decision:"
            : "The items below need your decision:";

        RemoveSeriesCommand = ReactiveCommand.CreateFromTask<MissingFolderRowVm>(RemoveSeriesAsync);
        KeepSeriesCommand = ReactiveCommand.Create<MissingFolderRowVm>(row =>
        {
            // "I deleted it on purpose, keep tracking" — just drop the question.
            MissingFolders.Remove(row);
            RaiseCounts();
        });
        AddFolderAsSeriesCommand = ReactiveCommand.CreateFromTask<UnknownFolderRowVm>(AddFolderAsSeriesAsync);
        DismissFolderCommand = ReactiveCommand.Create<UnknownFolderRowVm>(row =>
        {
            UnknownFolders.Remove(row);
            RaiseCounts();
        });
    }

    private void RaiseCounts()
    {
        this.RaisePropertyChanged(nameof(HasMissing));
        this.RaisePropertyChanged(nameof(HasUnknown));
    }

    private async Task RemoveSeriesAsync(MissingFolderRowVm row)
    {
        try
        {
            var ok = await _seriesService.RemoveAsync(row.MalId);
            if (ok)
            {
                _mainWindowVm.ShowToast("Series removed", row.Title);
                MissingFolders.Remove(row);
                RaiseCounts();
            }
        }
        catch (Exception ex)
        {
            _mainWindowVm.ShowToast("Remove failed", ex.Message);
        }
    }

    private async Task AddFolderAsSeriesAsync(UnknownFolderRowVm row)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
                is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow == null) return;

        var vm = new AddSeriesViewModel(_apiService, _seriesService)
        {
            SearchQuery = row.FolderName
        };
        var dialog = new Views.Dialogs.AddSeriesDialog { DataContext = vm };
        await dialog.ShowDialog(desktop.MainWindow);

        if (vm.AddedSeries != null)
        {
            _mainWindowVm.AddSeriesToLibrary(vm.AddedSeries);
            UnknownFolders.Remove(row);
            RaiseCounts();
        }
    }
}
