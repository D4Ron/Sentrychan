using ReactiveUI;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Reactive;

namespace Sentrychan.UI.ViewModels;

public class TrackerItem : ViewModelBase
{
    public Series Series { get; set; } = null!;
    public int LatestAired { get; set; }
    public int LocalCount { get; set; }
    public bool IsMissing => LocalCount < LatestAired;
    public string StatusText => IsMissing ? $"{LatestAired - LocalCount} Missing" : "Up to date";
    public string StatusColor => IsMissing ? "#F44336" : "#00C853";
}

public class EpisodeTrackerViewModel : ViewModelBase
{
    private readonly ISeriesService _seriesService;
    private readonly IAniDbApiService _aniDbService;

    public ObservableCollection<TrackerItem> TrackerItems { get; } = [];

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    public EpisodeTrackerViewModel(ISeriesService seriesService, IAniDbApiService aniDbService)
    {
        _seriesService = seriesService;
        _aniDbService = aniDbService;
        RefreshCommand = ReactiveCommand.CreateFromTask(LoadTrackerAsync);
    }

    private async Task LoadTrackerAsync()
    {
        var seriesList = await _seriesService.GetAllAsync();
        TrackerItems.Clear();

        foreach (var series in seriesList)
        {
            // Search AniDB by title to get accurate latest episode count
            var results = await _aniDbService.SearchAsync(series.Title);
            var bestMatch = results.FirstOrDefault();
            
            var item = new TrackerItem
            {
                Series = series,
                LocalCount = series.LastEpisodeNumber,
                LatestAired = bestMatch?.Episodes ?? series.LastEpisodeNumber
            };
            
            TrackerItems.Add(item);
        }
    }
}
