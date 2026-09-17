using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;

namespace Sentrychan.UI.ViewModels;

/// <summary>
/// The Manga page: a searchable MangaDex browser on top, the tracked-manga library
/// below. Add pulls a title into the library; clicking a library cover opens details.
/// </summary>
public class MangaLibraryViewModel : ViewModelBase
{
    private readonly IMangaService _mangaService;
    private readonly IMangaSourceRegistry _sources;
    private readonly ISecretModeService? _secretMode;
    private readonly Action<Manga> _onOpen;
    // Novel mode: this page shows only novel sources + the novel library (comics are the
    // default). Keeps the two source lineups from crowding each other.
    private readonly bool _novelMode;

    // Full tracked set (post secret-mode filter), and the reading-status sections
    // shown in the library — mirrors the anime library's sectioning.
    private readonly System.Collections.Generic.List<Manga> _all = [];
    public ObservableCollection<MangaCardVm> ContinueList { get; } = [];
    public ObservableCollection<MangaCardVm> RecentList { get; } = [];
    public ObservableCollection<MangaCardVm> ReadingList { get; } = [];
    public ObservableCollection<MangaCardVm> CompletedList { get; } = [];
    public ObservableCollection<MangaCardVm> PlanList { get; } = [];

    public ObservableCollection<MangaResultVm> SearchResults { get; } = [];

    // ── Library filter + category chips (mirrors the anime library) ──
    public string[] LibraryCategories { get; } = ["All", "Recently Added", "Reading", "Completed", "Plan to Read"];

    private string _libraryCategory = "All";
    public string LibraryCategory
    {
        get => _libraryCategory;
        set { this.RaiseAndSetIfChanged(ref _libraryCategory, value); RaiseSectionVisibility(); }
    }

    private string _libraryFilter = string.Empty;
    public string LibraryFilter
    {
        get => _libraryFilter;
        set { this.RaiseAndSetIfChanged(ref _libraryFilter, value); RebuildSections(); }
    }

    private bool CatShows(string cat) => LibraryCategory == "All" || LibraryCategory == cat;
    // Continue Reading only shows on the "All" view, like a home shelf.
    public bool ShowContinue  => ContinueList.Count > 0  && LibraryCategory == "All";
    public bool ShowRecent    => RecentList.Count > 0    && CatShows("Recently Added");
    public bool ShowReading   => ReadingList.Count > 0   && CatShows("Reading");
    public bool ShowCompleted => CompletedList.Count > 0 && CatShows("Completed");
    public bool ShowPlan      => PlanList.Count > 0       && CatShows("Plan to Read");

    private void RaiseSectionVisibility()
    {
        this.RaisePropertyChanged(nameof(ShowContinue));
        this.RaisePropertyChanged(nameof(ShowRecent));
        this.RaisePropertyChanged(nameof(ShowReading));
        this.RaisePropertyChanged(nameof(ShowCompleted));
        this.RaisePropertyChanged(nameof(ShowPlan));
    }

    // Source picker for the search box. Adult sources appear only in secret mode;
    // recomputed on each page load so entering secret mode reveals them.
    private string[] _sourceNames = [];
    public string[] SourceNames
    {
        get => _sourceNames;
        private set => this.RaiseAndSetIfChanged(ref _sourceNames, value);
    }

    private string _selectedSourceName;
    public string SelectedSourceName
    {
        get => _selectedSourceName;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSourceName, value);
            this.RaisePropertyChanged(nameof(SourceSupportsBrowse));
        }
    }

    private IMangaSourceService CurrentSource => _sources.Get(_selectedSourceName);

    /// <summary>True when the selected source offers query-less browsing (Popular/Latest).</summary>
    public bool SourceSupportsBrowse => CurrentSource.SupportsBrowse;

    private CancellationTokenSource? _searchCts;

    private string _searchQuery = string.Empty;
    public string SearchQuery
    {
        get => _searchQuery;
        set => this.RaiseAndSetIfChanged(ref _searchQuery, value);
    }

    private bool _isSearching;
    public bool IsSearching { get => _isSearching; set => this.RaiseAndSetIfChanged(ref _isSearching, value); }

    // ── Result pagination ──────────────────────────────────────────
    private const int ResultPageSize = 24;
    private int _resultPage = 1;
    private bool _lastWasBrowse;
    private string _lastBrowseCategory = "Popular";
    private string _lastQuery = string.Empty;

    private bool _canLoadMore;
    public bool CanLoadMore { get => _canLoadMore; set => this.RaiseAndSetIfChanged(ref _canLoadMore, value); }

    private bool _isLoadingMore;
    public bool IsLoadingMore { get => _isLoadingMore; set => this.RaiseAndSetIfChanged(ref _isLoadingMore, value); }

    private bool _inSearchMode;
    public bool InSearchMode
    {
        get => _inSearchMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _inSearchMode, value);
            this.RaisePropertyChanged(nameof(ShowLibrary));
        }
    }
    public bool ShowLibrary => !InSearchMode;

    private string _statusMessage = string.Empty;
    public string StatusMessage { get => _statusMessage; set => this.RaiseAndSetIfChanged(ref _statusMessage, value); }

    public bool IsLibraryEmpty => _all.Count == 0;

    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearSearchCommand { get; }
    public ReactiveCommand<string, Unit> BrowseCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }

    /// <summary>Set by the view so the VM can open the preview dialog with a window owner.</summary>
    public Func<MangaResultVm, IMangaSourceService, Task>? PreviewHandler { get; set; }

    /// <summary>Set by MainWindow: opens the reader for a result without adding it to the library.</summary>
    public Func<MangaResultVm, IMangaSourceService, Task>? PreviewReadHandler { get; set; }

    public MangaLibraryViewModel(IMangaService mangaService, IMangaSourceRegistry sources,
        ISecretModeService? secretMode, Action<Manga> onOpen, bool novelMode = false)
    {
        _mangaService = mangaService;
        _sources = sources;
        _secretMode = secretMode;
        _onOpen = onOpen;
        _novelMode = novelMode;
        _selectedSourceName = sources.Default.SourceName;
        RefreshSourceNames();

        SearchCommand      = ReactiveCommand.CreateFromTask(SearchAsync);
        ClearSearchCommand = ReactiveCommand.Create(ClearSearch);
        BrowseCommand      = ReactiveCommand.CreateFromTask<string>(BrowseAsync);
        LoadMoreCommand    = ReactiveCommand.CreateFromTask(LoadMoreAsync);
    }

    /// <summary>Fetches the next page of the current search/browse and appends it.</summary>
    private async Task LoadMoreAsync()
    {
        if (IsLoadingMore || !CanLoadMore) return;
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        IsLoadingMore = true;
        try
        {
            _resultPage++;
            var results = _lastWasBrowse
                ? await CurrentSource.BrowseAsync(_lastBrowseCategory, ResultPageSize, _resultPage, ct)
                : await CurrentSource.SearchAsync(_lastQuery, ResultPageSize, _resultPage, ct);
            if (ct.IsCancellationRequested) return;
            await PopulateResultsAsync(results, ct, append: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusMessage = $"Load more failed: {ex.Message}"; }
        finally { IsLoadingMore = false; }
    }

    private async Task BrowseAsync(string category)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        InSearchMode = true;
        IsSearching = true;
        _lastWasBrowse = true;
        _lastBrowseCategory = category;
        _resultPage = 1;
        StatusMessage = $"{category} on {CurrentSource.SourceName}…";
        SearchResults.Clear();

        try
        {
            var results = await CurrentSource.BrowseAsync(category, ResultPageSize, 1, ct);
            if (ct.IsCancellationRequested) return;
            await PopulateResultsAsync(results, ct, append: false);
            StatusMessage = results.Count == 0 ? $"Browsing isn't available for {CurrentSource.SourceName}." : $"{category} · {CurrentSource.SourceName}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusMessage = $"Browse failed: {ex.Message}"; }
        finally { IsSearching = false; }
    }

    private async void OpenPreview(MangaResultVm vm)
    {
        if (PreviewHandler != null) await PreviewHandler(vm, CurrentSource);
    }

    private void RefreshSourceNames()
    {
        var secret = _secretMode?.IsSecretModeActive ?? false;
        SourceNames = _sources.Sources
            .Where(s => s.IsNovel == _novelMode && (secret || !s.IsAdultSource))
            .Select(s => s.SourceName)
            .ToArray();

        // If the selected source is no longer valid (wrong section, or left secret mode),
        // fall back to the first source available in this section.
        if (!SourceNames.Contains(_selectedSourceName))
            SelectedSourceName = SourceNames.FirstOrDefault() ?? _sources.Default.SourceName;
    }

    public async Task LoadAsync()
    {
        RefreshSourceNames(); // reflect current secret-mode state each visit
        var all = await _mangaService.GetAllAsync();
        var secret = _secretMode?.IsSecretModeActive ?? false;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _all.Clear();
            // This section shows only its own kind (comics vs novels); adult titles need secret mode.
            _all.AddRange(all.Where(m => m.IsNovel == _novelMode && (secret || !m.IsCensored)));
            RebuildSections();
        });
    }

    /// <summary>Buckets the tracked manga into reading-status sections, honouring the filter box.</summary>
    private void RebuildSections()
    {
        ContinueList.Clear();
        RecentList.Clear(); ReadingList.Clear(); CompletedList.Clear(); PlanList.Clear();

        var q = _libraryFilter.Trim();
        var cards = new System.Collections.Generic.List<MangaCardVm>();
        foreach (var m in _all)
        {
            if (q.Length > 0 && m.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;

            var card = new MangaCardVm(m, _onOpen, RemoveAsync, MarkAllReadAsync);
            cards.Add(card);
            switch (card.Bucket)
            {
                case MangaReadingBucket.RecentlyAdded: RecentList.Add(card); break;
                case MangaReadingBucket.Reading:       ReadingList.Add(card); break;
                case MangaReadingBucket.Completed:     CompletedList.Add(card); break;
                default:                               PlanList.Add(card); break;
            }
        }

        // Continue Reading shelf: started-but-not-finished, most recently touched first.
        foreach (var card in cards
            .Where(c => c.Manga.LastReadChapter > 0
                     && !(c.Manga.TotalChapters is { } t && t > 0 && c.Manga.LastReadChapter >= t))
            .OrderByDescending(c => c.Manga.LastCheckedAt ?? c.Manga.AddedAt)
            .Take(12))
        {
            ContinueList.Add(new MangaCardVm(card.Manga, _onOpen, RemoveAsync, MarkAllReadAsync));
        }

        this.RaisePropertyChanged(nameof(IsLibraryEmpty));
        RaiseSectionVisibility();
    }

    private async Task MarkAllReadAsync(Manga manga)
    {
        await _mangaService.MarkAllReadAsync(manga.Id);
        await LoadAsync();
    }

    private void ClearSearch()
    {
        _searchCts?.Cancel();
        SearchQuery = string.Empty;
        SearchResults.Clear();
        InSearchMode = false;
        CanLoadMore = false;
        _resultPage = 1;
        StatusMessage = string.Empty;
    }

    private async Task SearchAsync()
    {
        var q = SearchQuery.Trim();
        if (q.Length < 2) return;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        InSearchMode = true;
        IsSearching = true;
        _lastWasBrowse = false;
        _lastQuery = q;
        _resultPage = 1;
        StatusMessage = $"Searching {CurrentSource.SourceName}…";
        SearchResults.Clear();

        try
        {
            var results = await CurrentSource.SearchAsync(q, ResultPageSize, 1, ct);
            if (ct.IsCancellationRequested) return;
            await PopulateResultsAsync(results, ct, append: false);
            StatusMessage = results.Count == 0 ? $"No results for \"{q}\"." : string.Empty;
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex) { StatusMessage = $"Search failed: {ex.Message}"; }
        finally { IsSearching = false; }
    }

    private async Task PopulateResultsAsync(System.Collections.Generic.List<MangaSearchResult> results, CancellationToken ct, bool append)
    {
        var libIds = (await _mangaService.GetAllAsync(ct))
            .Where(m => m.Source == CurrentSource.SourceName)
            .Select(m => m.SourceId).ToHashSet();

        var srcName = CurrentSource.SourceName;
        if (!append) SearchResults.Clear();

        // Skip dupes when appending (offset overlaps can repeat an item).
        var existing = SearchResults.Select(vm => vm.Result.SourceId).ToHashSet();
        foreach (var r in results)
        {
            if (append && !existing.Add(r.SourceId)) continue;
            SearchResults.Add(new MangaResultVm(r, srcName, AddAsync, OpenPreview, libIds.Contains(r.SourceId)));
        }

        // A full page back suggests there's more to fetch.
        CanLoadMore = results.Count >= ResultPageSize;
    }

    private async Task AddAsync(MangaResultVm vm)
    {
        if (vm.IsInLibrary || vm.IsAdding) return;
        vm.IsAdding = true;
        try
        {
            var r = vm.Result;
            var manga = new Manga
            {
                Source                = CurrentSource.SourceName,
                SourceId              = r.SourceId,
                Title                 = r.Title,
                OriginalTitle         = r.OriginalTitle,
                AlternativeTitlesJson = JsonSerializer.Serialize(r.AltTitles),
                Description           = r.Description,
                CoverPath             = r.CoverUrl,
                Status                = r.Status,
                Year                  = r.Year,
                TotalChapters         = r.LastChapter,
                IsCensored            = r.IsAdult,
                IsNovel               = CurrentSource.IsNovel
            };
            await _mangaService.AddAsync(manga);
            vm.IsInLibrary = true;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            // A failed add must never crash the app (an unobserved ReactiveCommand
            // exception would). Surface it in the status line instead.
            StatusMessage = $"Couldn't add \"{vm.Title}\": {ex.Message}";
        }
        finally { vm.IsAdding = false; }
    }

    private async Task RemoveAsync(Manga manga)
    {
        await _mangaService.RemoveAsync(manga.Id);
        await LoadAsync();
    }
}
