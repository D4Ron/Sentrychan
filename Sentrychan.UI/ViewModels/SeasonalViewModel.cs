using ReactiveUI;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models.Api;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Reactive;
using System.Linq;

namespace Sentrychan.UI.ViewModels;

public class SeasonalViewModel : ViewModelBase
{
    private readonly IAnimeApiService _apiService;
    private readonly ISeriesService _seriesService;
    private readonly MainWindowViewModel _mainWindowVm;

    public ObservableCollection<SeasonalAnimeVm> AnimeList { get; } = [];
    public ObservableCollection<int> YearOptions { get; } = [];
    public string[] SeasonOptions { get; } = ["Winter", "Spring", "Summer", "Fall"];

    /// <summary>Airing-day tabs. Filters client-side on Jikan's broadcast.day.</summary>
    public string[] DayOptions { get; } =
        ["All", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday", "TBA"];

    // Unfiltered season results; AnimeList is the day-filtered view.
    private readonly System.Collections.Generic.List<SeasonalAnimeVm> _allAnime = [];

    private string _selectedDay = "All";
    public string SelectedDay
    {
        get => _selectedDay;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedDay, value);
            ApplyDayFilter();
        }
    }

    private int _selectedYear;
    public int SelectedYear
    {
        get => _selectedYear;
        set => this.RaiseAndSetIfChanged(ref _selectedYear, value);
    }

    private string _selectedSeason;
    public string SelectedSeason
    {
        get => _selectedSeason;
        set => this.RaiseAndSetIfChanged(ref _selectedSeason, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public ReactiveCommand<Unit, Unit> LoadCommand { get; }

    private readonly Action<AnimeResult> _onOpenDetails;

    public SeasonalViewModel(IAnimeApiService apiService, ISeriesService seriesService, MainWindowViewModel mainWindowVm, Action<AnimeResult> onOpenDetails)
    {
        _apiService = apiService;
        _seriesService = seriesService;
        _mainWindowVm = mainWindowVm;
        _onOpenDetails = onOpenDetails;

        var currentYear = DateTime.Now.Year;
        for (int y = currentYear + 1; y >= currentYear - 2; y--)
            YearOptions.Add(y);

        _selectedYear = currentYear;
        _selectedSeason = GetCurrentSeason();

        LoadCommand = ReactiveCommand.CreateFromTask(LoadSeasonalAsync);
    }

    private string GetCurrentSeason()
    {
        int month = DateTime.Now.Month;
        if (month >= 1 && month <= 3) return "Winter";
        if (month >= 4 && month <= 6) return "Spring";
        if (month >= 7 && month <= 9) return "Summer";
        return "Fall";
    }

    private async Task LoadSeasonalAsync()
    {
        IsLoading = true;
        try
        {
            var results = await _apiService.GetSeasonalAnimeAsync(SelectedYear, SelectedSeason);
            var libraryMalIds = (await _seriesService.GetAllAsync()).Select(s => s.MalId).ToHashSet();

            _allAnime.Clear();
            foreach (var r in results)
            {
                var vm = new SeasonalAnimeVm(r, _seriesService, _mainWindowVm, _onOpenDetails, libraryMalIds.Contains(r.MalId));
                _allAnime.Add(vm);
            }
            ApplyDayFilter();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyDayFilter()
    {
        AnimeList.Clear();
        System.Collections.Generic.IEnumerable<SeasonalAnimeVm> src = _allAnime;

        if (SelectedDay == "TBA")
            src = _allAnime.Where(a => string.IsNullOrEmpty(a.Anime.Broadcast?.Day));
        else if (SelectedDay != "All")
            // Jikan reports plural day names ("Mondays") — StartsWith covers both forms
            src = _allAnime.Where(a =>
                a.Anime.Broadcast?.Day?.StartsWith(SelectedDay, StringComparison.OrdinalIgnoreCase) == true);

        foreach (var a in src) AnimeList.Add(a);
    }
}
