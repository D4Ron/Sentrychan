using ReactiveUI;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Sentrychan.Core.Models.Api;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unit = System.Reactive.Unit;
using Sentrychan.UI.ViewModels;
using System.Linq;
namespace Sentrychan.UI.ViewModels;

public class AddSeriesViewModel : ViewModelBase
{
    private readonly IAnimeApiService _apiService;
    private readonly ISeriesService _seriesService;
    private readonly ITitleResolverService? _resolver;

    // ── Properties ─────────────────────────────────────────────────
    private string _searchQuery = string.Empty;
    public string SearchQuery
    {
        get => _searchQuery;
        set => this.RaiseAndSetIfChanged(ref _searchQuery, value);
    }

    private AnimeResult? _selectedResult;
    public AnimeResult? SelectedResult
    {
        get => _selectedResult;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedResult, value);
            this.RaisePropertyChanged(nameof(HasSelection));
        }
    }

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        set => this.RaiseAndSetIfChanged(ref _isSearching, value);
    }

    private bool _isAdding;
    public bool IsAdding
    {
        get => _isAdding;
        set => this.RaiseAndSetIfChanged(ref _isAdding, value);
    }

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    private bool _hasError;
    public bool HasError
    {
        get => _hasError;
        set => this.RaiseAndSetIfChanged(ref _hasError, value);
    }

    public bool HasSelection => SelectedResult != null;
    public bool HasResults => SearchResults.Count > 0;

    // ── Collections ────────────────────────────────────────────────
    public ObservableCollection<AnimeResult> SearchResults { get; } = [];

    // ── Commands ───────────────────────────────────────────────────
    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<Unit, Unit> AddCommand { get; }
    public ReactiveCommand<AnimeResult, Unit> SelectResultCommand { get; }

    // ── Result ─────────────────────────────────────────────────────
    public Series? AddedSeries { get; private set; }

    // ── Design-time constructor ────────────────────────────────────
    public AddSeriesViewModel()
    {
        _apiService = null!;
        _seriesService = null!;

        SearchCommand = ReactiveCommand.CreateFromTask(async ct => await SearchAsync(ct));
        AddCommand = ReactiveCommand.CreateFromTask(async ct => await AddSeriesAsync(ct));
        SelectResultCommand = ReactiveCommand.Create<AnimeResult>(r => SelectedResult = r);

        // NOTE: Auto-search subscription is set up in the runtime constructor only,
        // to avoid crashing when _apiService is null in design-time.
    }

    // ── Runtime constructor ────────────────────────────────────────
    public AddSeriesViewModel(
        IAnimeApiService apiService,
        ISeriesService seriesService) : this()
    {
        _apiService = apiService;
        _seriesService = seriesService;
        _resolver = App.Services?.GetService(typeof(ITitleResolverService)) as ITitleResolverService;

        // Debounced auto-search — only set up when we have real services
        this.WhenAnyValue(x => x.SearchQuery)
        .Throttle(TimeSpan.FromMilliseconds(800))
        .Where(q => q?.Length >= 3)
        .ObserveOn(RxApp.MainThreadScheduler)
        .Subscribe(_ => Avalonia.Threading.Dispatcher.UIThread.Post(
        () => SearchCommand.Execute().Subscribe()));
    }

    // ── Search ─────────────────────────────────────────────────────
    private async Task SearchAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(SearchQuery) || _apiService == null) return;

        IsSearching = true;
        HasError = false;
        StatusMessage = string.Empty;

        try
        {
            List<AnimeResult> results;

            // Offline database first — instant and immune to Jikan's flaky /anime?q
            // search endpoint. Falls back to Jikan only if the DB isn't ready.
            if (_resolver?.IsReady == true)
            {
                // Only entries with a real MAL id — the whole app keys on MalId.
                results = _resolver.Search(SearchQuery, 25)
                    .Where(r => r.MalId > 0)
                    .Select(ToAnimeResult)
                    .ToList();
            }
            else
            {
                results = await _apiService.SearchAnimeAsync(SearchQuery, ct: ct);
            }

            SearchResults.Clear();
            foreach (var r in results.Take(15))
                SearchResults.Add(r);

            if (SearchResults.Count == 0)
                StatusMessage = "No results found";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>Maps an offline-DB result to the AnimeResult the UI expects.</summary>
    private static AnimeResult ToAnimeResult(ResolvedAnime r) => new()
    {
        MalId = r.MalId,
        Title = r.CanonicalTitle,
        Episodes = r.Episodes,
        Status = r.Status switch
        {
            "ONGOING"  => "Currently Airing",
            "UPCOMING" => "Not yet aired",
            "FINISHED" => "Finished Airing",
            _          => r.Status ?? string.Empty
        },
        Year = r.Year,
        Images = new AnimeImages
        {
            Jpg = new AnimeImageSet { LargeImageUrl = r.PictureUrl ?? r.ThumbnailUrl }
        }
    };

    // ── Add ────────────────────────────────────────────────────────
    private async Task AddSeriesAsync(CancellationToken ct)
    {
        if (SelectedResult == null || _seriesService == null) return;

        IsAdding = true;
        StatusMessage = string.Empty;

        try
        {
            var altTitles = new List<string>();

            if (!string.IsNullOrEmpty(SelectedResult.TitleEnglish))
                altTitles.Add(SelectedResult.TitleEnglish);

            if (!string.IsNullOrEmpty(SelectedResult.TitleJapanese))
                altTitles.Add(SelectedResult.TitleJapanese);

            if (SelectedResult.Titles != null)
                foreach (var t in SelectedResult.Titles)
                    if (!string.IsNullOrEmpty(t.Title) && !altTitles.Contains(t.Title))
                        altTitles.Add(t.Title);

            // Look for earliest episode by default (episode 0 or 1)
            int startEpisode = -1;

            var series = new Series
            {
                MalId = SelectedResult.MalId,
                Title = SelectedResult.Title,
                OriginalTitle = SelectedResult.TitleEnglish ?? SelectedResult.Title,
                TitleJapanese = SelectedResult.TitleJapanese ?? string.Empty,
                AlternativeTitlesJson = System.Text.Json.JsonSerializer.Serialize(altTitles),
                LastEpisodeNumber = startEpisode,
                AddedAt = DateTime.UtcNow,
                AiringStatus = Sentrychan.Core.Services.AiringStatusNormalizer.Normalize(SelectedResult.Status),
                TotalEpisodes = SelectedResult.Episodes
            };

            var addedSeries = await _seriesService.AddAsync(series, SelectedResult.LargeImageUrl, ct);

            if (addedSeries != null)
            {
                AddedSeries = addedSeries;
                StatusMessage = $"Added: {series.Title}";
            }
            else
            {
                HasError = true;
                StatusMessage = "Series already in library";
            }
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = $"Failed to add: {ex.Message}";
        }
        finally
        {
            IsAdding = false;
        }
    }
}