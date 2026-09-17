using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models.Api;

namespace Sentrychan.UI.ViewModels;

/// <summary>
/// "Latest" page — Tachiyomi-style source browse. Loads in two phases:
/// Phase 1 renders rows immediately from parsed RSS titles (placeholder posters);
/// Phase 2 resolves MAL metadata/posters in the background and swaps rows in place.
/// SearchAnimeAsync has its own memory + persistent caching, so revisits are cheap.
/// </summary>
public class LatestArrivalsViewModel : ViewModelBase
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IAnimeApiService _apiService;
    private readonly ISeriesService _seriesService;
    private readonly IEpisodeNormalizer _normalizer;
    private readonly MainWindowViewModel _mainWindowVm;
    private readonly Sentrychan.UI.Services.IThemeService _themeService;
    private readonly ITitleResolverService? _titleResolver;

    public ObservableCollection<SeasonalAnimeVm> AnimeList { get; } = [];
    public ObservableCollection<Sentrychan.Core.Models.RssFeed> Feeds { get; } = [];

    private Sentrychan.Core.Models.RssFeed? _selectedFeed;
    public Sentrychan.Core.Models.RssFeed? SelectedFeed
    {
        get => _selectedFeed;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedFeed, value);
            if (value != null) _ = LoadLatestAsync();
        }
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    // ── Batch filter ───────────────────────────────────────────────
    public string[] BatchFilterOptions { get; } = ["All", "Episodes only", "Batches only"];

    private string _selectedBatchFilter = "All";
    public string SelectedBatchFilter
    {
        get => _selectedBatchFilter;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedBatchFilter, value);
            _ = LoadLatestAsync();
        }
    }

    // ── Poster lightbox ────────────────────────────────────────────
    private string? _previewImageUrl;
    public string? PreviewImageUrl
    {
        get => _previewImageUrl;
        set
        {
            this.RaiseAndSetIfChanged(ref _previewImageUrl, value);
            this.RaisePropertyChanged(nameof(HasPreview));
        }
    }
    public bool HasPreview => !string.IsNullOrEmpty(_previewImageUrl);

    public void ShowPreview(string? url) => PreviewImageUrl = url;
    public void ClosePreview() => PreviewImageUrl = null;

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> NextPageCommand { get; }
    public ReactiveCommand<Unit, Unit> PrevPageCommand { get; }

    // ── Paging ─────────────────────────────────────────────────────
    // Rows are paged CLIENT-side over everything the feeds returned, and only the
    // visible page is resolved. That cap matters: every resolved row costs a Jikan
    // call paced at 900ms, so eagerly resolving all pages would re-trigger the 429
    // cascade that used to leave every poster blank.
    private const int PageSize = 36;

    // nyaa/sukebei RSS honours &p=N (75 items/page). SubsPlease's own feed ignores
    // it and re-serves page 1, so it's hard-capped at the 50 items it returns.
    private const int NyaaPagesToFetch = 3;

    private List<RssEntry> _allEntries = [];
    private List<Sentrychan.Core.Models.Series> _librarySeries = [];
    private HashSet<int> _libraryMalIds = [];

    private int _currentPage;
    public int CurrentPage
    {
        get => _currentPage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _currentPage, value);
            RaisePagingChanged();
        }
    }

    public int TotalPages =>
        _allEntries.Count == 0 ? 1 : (int)Math.Ceiling(_allEntries.Count / (double)PageSize);

    public string PageLabel => $"Page {CurrentPage + 1} / {TotalPages}";
    public bool CanPrevPage => CurrentPage > 0;
    public bool CanNextPage => CurrentPage < TotalPages - 1;
    public bool HasPages => TotalPages > 1;

    private void RaisePagingChanged()
    {
        this.RaisePropertyChanged(nameof(TotalPages));
        this.RaisePropertyChanged(nameof(PageLabel));
        this.RaisePropertyChanged(nameof(CanPrevPage));
        this.RaisePropertyChanged(nameof(CanNextPage));
        this.RaisePropertyChanged(nameof(HasPages));
    }

    private readonly Action<AnimeResult> _onOpenDetails;
    private DateTime _lastLoaded = DateTime.MinValue;

    // Cancels the in-flight background resolution when a reload starts
    // or the user switches feeds.
    private CancellationTokenSource? _resolveCts;

    /// <summary>One parsed RSS entry after normalization/dedup.
    /// RawTitle/Link identify the exact release so it can be downloaded directly;
    /// ViewUrl (the RSS guid) is the nyaa/sukebei page. The remaining fields are
    /// the torrent metadata carried in the feed's nyaa: namespace.</summary>
    private sealed record RssEntry(
        string Title, string RawTitle, string Link, string ViewUrl,
        int? Episode, string Group, string FeedUrl,
        string Category, string SizeDisplay, int Seeders, int Leechers,
        int Downloads, string InfoHash, DateTime? Published, bool Trusted,
        bool IsBatch);

    private static string RssField(CodeHollow.FeedReader.FeedItem item, string localName)
    {
        var el = item.SpecificItem?.Element;
        return el?.Elements()
            .FirstOrDefault(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            ?.Value ?? string.Empty;
    }

    private static int RssInt(CodeHollow.FeedReader.FeedItem item, string localName) =>
        int.TryParse(RssField(item, localName), out var n) ? n : 0;

    public LatestArrivalsViewModel(
        IDbContextFactory<AppDbContext> dbFactory,
        IAnimeApiService apiService,
        ISeriesService seriesService,
        IEpisodeNormalizer normalizer,
        MainWindowViewModel mainWindowVm,
        Sentrychan.UI.Services.IThemeService themeService,
        Action<AnimeResult> onOpenDetails)
    {
        _dbFactory = dbFactory;
        _apiService = apiService;
        _seriesService = seriesService;
        _normalizer = normalizer;
        _mainWindowVm = mainWindowVm;
        _themeService = themeService;
        _onOpenDetails = onOpenDetails;
        _titleResolver = App.Services?.GetService(typeof(ITitleResolverService)) as ITitleResolverService;

        RefreshCommand  = ReactiveCommand.CreateFromTask(LoadLatestAsync);
        NextPageCommand = ReactiveCommand.Create(() => GoToPage(CurrentPage + 1));
        PrevPageCommand = ReactiveCommand.Create(() => GoToPage(CurrentPage - 1));
    }

    /// <summary>
    /// nyaa/sukebei RSS supports &amp;p=N, so we pull a few pages for a deeper list.
    /// Every other feed (notably SubsPlease's own, which ignores &amp;p= and would just
    /// re-serve page 1) is fetched once.
    /// </summary>
    private static IEnumerable<string> BuildFeedUrls(string feedUrl)
    {
        yield return feedUrl;

        if (feedUrl.IndexOf("nyaa", StringComparison.OrdinalIgnoreCase) < 0) yield break;

        for (int p = 2; p <= NyaaPagesToFetch; p++)
            yield return feedUrl.Contains('?') ? $"{feedUrl}&p={p}" : $"{feedUrl}?p={p}";
    }

    public async Task InitializeAsync()
    {
        // Auto-refresh if it's been more than 30 minutes since last load
        if (AnimeList.Count == 0 || (DateTime.Now - _lastLoaded).TotalMinutes > 30)
        {
            await LoadLatestAsync();
        }
    }

    private async Task LoadLatestAsync()
    {
        if (IsLoading) return;

        _resolveCts?.Cancel();
        _resolveCts = new CancellationTokenSource();
        var ct = _resolveCts.Token;

        IsLoading = true;
        StatusMessage = "Reading RSS feeds...";
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var feeds = await db.RssFeeds.Where(f => f.IsEnabled).ToListAsync(ct);

            if (_themeService.IsSecretMode && !feeds.Any(f => f.Url.Contains("sukebei", StringComparison.OrdinalIgnoreCase)))
            {
                feeds.Add(new Sentrychan.Core.Models.RssFeed
                {
                    Id = -2,
                    Url = "https://sukebei.nyaa.si/?page=rss",
                    IsEnabled = true
                });
            }

            if (feeds.Count == 0)
            {
                StatusMessage = "No RSS feeds configured or enabled.";
                return;
            }

            // Populate feeds dropdown unconditionally so it reacts to theme changes
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                var previousSelectionId = SelectedFeed?.Id ?? -1;
                Feeds.Clear();
                Feeds.Add(new Sentrychan.Core.Models.RssFeed { Id = -1, Url = "All Feeds", IsEnabled = true });
                foreach (var f in feeds) Feeds.Add(f);

                // Default to the SubsPlease feed the first time; keep the user's
                // choice afterward.
                var newSelection = Feeds.FirstOrDefault(f => f.Id == previousSelectionId);
                if (newSelection == null && previousSelectionId == -1)
                    newSelection = Feeds.FirstOrDefault(f =>
                        f.Url.Contains("subsplease", StringComparison.OrdinalIgnoreCase));
                if (newSelection != null)
                {
                    _selectedFeed = newSelection;
                    this.RaisePropertyChanged(nameof(SelectedFeed));
                }
            });

            // First load defaults to SubsPlease (its releases are the app's backbone).
            if (SelectedFeed == null)
                _selectedFeed = feeds.FirstOrDefault(f =>
                    f.Url.Contains("subsplease", StringComparison.OrdinalIgnoreCase));

            var feedsToProcess = SelectedFeed != null && SelectedFeed.Id != -1
                                 ? feeds.Where(f => f.Id == SelectedFeed.Id).ToList()
                                 : feeds;

            // ── Phase 1: parse feeds and render rows immediately ──────
            var uniqueTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<RssEntry>();

            foreach (var feed in feedsToProcess)
            foreach (var feedUrl in BuildFeedUrls(feed.Url))
            {
                try
                {
                    var parsed = await CodeHollow.FeedReader.FeedReader.ReadAsync(feedUrl, ct);
                    foreach (var item in parsed.Items)
                    {
                        if (string.IsNullOrWhiteSpace(item.Title)) continue;

                        int? episode;
                        string group;
                        string titlePart;

                        bool isBatch = Sentrychan.Core.Services.TitleResolverService
                            .IsBatchRelease(item.Title, 0);

                        if (_themeService.IsSecretMode)
                        {
                            // Doujin naming convention: [Circle (Artist)] Title (Event) [tags]
                            var (display, circle) = ParseDoujinTitle(item.Title);
                            titlePart = display;
                            episode = _normalizer.ExtractEpisodeNumber(item.Title);
                            group = circle.Length > 0 ? circle : ExtractGroup(item.Title);
                        }
                        else if (_titleResolver?.IsReady == true)
                        {
                            // Anitomy: proper tokenizer instead of regex heuristics
                            var rel = _titleResolver.ParseRelease(item.Title);
                            titlePart = rel.DisplayTitle;
                            episode = rel.Episode;
                            isBatch = isBatch || rel.IsBatch;
                            group = string.IsNullOrEmpty(rel.ReleaseGroup)
                                ? ExtractGroup(item.Title) : rel.ReleaseGroup;
                        }
                        else
                        {
                            var normalized = _normalizer.NormalizeTitle(item.Title);
                            titlePart = RemoveSourceGroupAndEpisode(normalized);
                            episode = _normalizer.ExtractEpisodeNumber(item.Title);
                            group = ExtractGroup(item.Title);
                        }
                        if (isBatch) episode = null;

                        // Secret mode dedupes on the RAW title — cleaned doujin titles
                        // can collide across different releases.
                        var dedupeKey = _themeService.IsSecretMode ? item.Title.Trim() : titlePart;
                        if (!string.IsNullOrWhiteSpace(titlePart) && titlePart.Length > 2
                            && uniqueTitles.Add(dedupeKey))
                        {
                            entries.Add(new RssEntry(
                                titlePart, item.Title.Trim(), item.Link ?? string.Empty,
                                item.Id ?? string.Empty,
                                episode, group, feed.Url,
                                Category: RssField(item, "category"),
                                SizeDisplay: RssField(item, "size"),
                                Seeders: RssInt(item, "seeders"),
                                Leechers: RssInt(item, "leechers"),
                                Downloads: RssInt(item, "downloads"),
                                InfoHash: RssField(item, "infoHash"),
                                Published: item.PublishingDate,
                                Trusted: RssField(item, "trusted").Equals("Yes", StringComparison.OrdinalIgnoreCase),
                                IsBatch: isBatch));
                        }
                    }
                }
                catch
                {
                    // Ignore individual feed errors during latest arrivals fetch
                }
            }

            if (entries.Count == 0)
            {
                StatusMessage = "No recent episodes found in feeds.";
                return;
            }

            _librarySeries = await _seriesService.GetAllAsync();
            _libraryMalIds = _librarySeries.Select(s => s.MalId).ToHashSet();
            var filtered = SelectedBatchFilter switch
            {
                "Episodes only" => entries.Where(e => !e.IsBatch),
                "Batches only"  => entries.Where(e => e.IsBatch),
                _               => entries
            };

            _allEntries = filtered.ToList();
            _lastLoaded = DateTime.Now;
            StatusMessage = string.Empty;
            IsLoading = false;

            GoToPage(0);
        }
        catch (OperationCanceledException) { /* reload superseded */ }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading latest arrivals: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Renders one page of rows and resolves ONLY that page. Navigating cancels the
    /// previous page's in-flight resolution so we never stack API work.
    /// </summary>
    private void GoToPage(int page)
    {
        if (_allEntries.Count == 0) return;

        var maxPage = Math.Max(0, TotalPages - 1);
        page = Math.Clamp(page, 0, maxPage);

        _resolveCts?.Cancel();
        _resolveCts = new CancellationTokenSource();
        var ct = _resolveCts.Token;

        CurrentPage = page;

        var pageEntries = _allEntries.Skip(page * PageSize).Take(PageSize).ToList();

        // Build the placeholder rows synchronously so the background resolver
        // has them BEFORE it starts (posting the construction to the dispatcher
        // raced the resolver, which then saw an empty list → nothing resolved).
        var placeholderMap = new List<(SeasonalAnimeVm Vm, RssEntry Entry)>();
        {
            foreach (var entry in pageEntries)
            {
                bool isCensored = _themeService.IsSecretMode &&
                    entry.FeedUrl.Contains("sukebei", StringComparison.OrdinalIgnoreCase);

                var placeholder = new AnimeResult
                {
                    MalId = entry.Title.GetHashCode(),
                    Title = entry.Title,
                    Status = SubLabel(entry),
                    Episodes = null
                };
                var entryCopy = entry; // capture for the closure
                var vm = new SeasonalAnimeVm(
                    placeholder, _seriesService, _mainWindowVm, _onOpenDetails,
                    isInLibrary: false, isCensored: isCensored,
                    onDownload: () => DownloadEntryAsync(entryCopy, malId: 0),
                    onShowDetails: () => ShowReleaseDetailsAsync(entryCopy, placeholder, 0));
                placeholderMap.Add((vm, entry));
            }

            // Render immediately — posters resolve in the background.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                AnimeList.Clear();
                foreach (var (vm, _) in placeholderMap) AnimeList.Add(vm);
            });

            // ── Phase 2: background resolution (posters, real ids) ─────
            // Only this page's rows — see the PageSize note above.
            if (!_themeService.IsSecretMode)
            {
                _ = ResolveDetailsAsync(placeholderMap, _librarySeries, _libraryMalIds, ct);
            }
            else
            {
                // Secret-mode ladder: offline DB (covers adult anime, with
                // posters) → sukebei view-page thumbnail scrape.
                _ = ResolveSecretDetailsAsync(placeholderMap, _libraryMalIds, ct);
            }
        }
    }

    /// <summary>
    /// Resolves each placeholder row and swaps the finished row into the grid in place.
    /// Resolution order: 1) library series match (no API — reuses the stored poster),
    /// 2) Jikan search (all statuses — recently-finished shows must resolve too).
    /// Pacing: a real API attempt is followed by a delay whether it succeeded or not;
    /// only confirmed cache hits (fast AND non-empty) skip it. This prevents the
    /// fast-fail 429 cascade that left every poster blank.
    /// </summary>
    private async Task ResolveDetailsAsync(
        List<(SeasonalAnimeVm Vm, RssEntry Entry)> rows,
        List<Sentrychan.Core.Models.Series> librarySeries,
        HashSet<int> libraryMalIds,
        CancellationToken ct)
    {
        var sw = new Stopwatch();
        int resolved = 0, fromLibrary = 0, offline = 0, failed = 0;

        // ── Pass 1: everything answerable WITHOUT the network ──────────
        // The offline resolver and the library match are pure in-memory lookups, but
        // they used to sit in the same sequential loop as the 900ms-paced Jikan calls
        // — so one slow API row stalled every instant row queued behind it. Doing the
        // free work first fills the grid almost immediately and leaves only genuine
        // unknowns for the paced pass.
        var needsApi = new List<(SeasonalAnimeVm Vm, RssEntry Entry)>();

        foreach (var (placeholderVm, entry) in rows)
        {
            if (ct.IsCancellationRequested) return;

            if (_titleResolver?.IsReady == true)
            {
                var res = _titleResolver.ResolveRelease(entry.RawTitle);
                if (res != null)
                {
                    var offlineResult = new AnimeResult
                    {
                        MalId    = res.MalId,
                        Title    = entry.Title,
                        Status   = SubLabel(entry),
                        Episodes = res.Episodes,
                        Images   = new AnimeImages
                        {
                            Jpg = new AnimeImageSet { LargeImageUrl = res.PictureUrl ?? res.ThumbnailUrl }
                        }
                    };
                    var offEntry = entry;
                    var offMalId = res.MalId;
                    SwapRow(placeholderVm, new SeasonalAnimeVm(
                        offlineResult, _seriesService, _mainWindowVm, _onOpenDetails,
                        isInLibrary: res.MalId > 0 && libraryMalIds.Contains(res.MalId),
                        isCensored: false,
                        onDownload: () => DownloadEntryAsync(offEntry, offMalId),
                        onShowDetails: () => ShowReleaseDetailsAsync(offEntry, offlineResult, offMalId)));
                    offline++;
                    continue;
                }
            }

            var lib = librarySeries.FirstOrDefault(s =>
                _normalizer.MatchesTitle(entry.Title, s.Title) ||
                (!string.IsNullOrEmpty(s.OriginalTitle) &&
                 _normalizer.MatchesTitle(entry.Title, s.OriginalTitle)));

            if (lib != null && !string.IsNullOrEmpty(lib.PosterPath))
            {
                var libResult = new AnimeResult
                {
                    MalId    = lib.MalId,
                    Title    = entry.Title,
                    Status   = SubLabel(entry),
                    Episodes = lib.TotalEpisodes,
                    Images   = new AnimeImages
                    {
                        Jpg = new AnimeImageSet { LargeImageUrl = lib.PosterPath }
                    }
                };
                var libEntry = entry;
                var libMalId = lib.MalId;
                SwapRow(placeholderVm, new SeasonalAnimeVm(
                    libResult, _seriesService, _mainWindowVm, _onOpenDetails,
                    isInLibrary: true, isCensored: lib.IsCensored,
                    onDownload: () => DownloadEntryAsync(libEntry, libMalId),
                    onShowDetails: () => ShowReleaseDetailsAsync(libEntry, libResult, libMalId)));
                fromLibrary++;
                continue;
            }

            needsApi.Add((placeholderVm, entry));
        }

        Console.WriteLine($"[Latest] Pass 1 — {offline} offline, {fromLibrary} from library, {needsApi.Count} need Jikan");

        // ── Pass 2: the stragglers, paced for Jikan's rate limit ───────
        foreach (var (placeholderVm, entry) in needsApi)
        {
            if (ct.IsCancellationRequested) return;

            try
            {
                // ── 2. Jikan search (all statuses, cached internally) ────────
                sw.Restart();
                var searchResults = await _apiService.SearchAnimeAsync(entry.Title, null, 1);
                bool wasCacheHit = sw.ElapsedMilliseconds < 150 && searchResults.Count > 0;

                var best = searchResults.FirstOrDefault();
                if (best != null)
                {
                    // The parsed RSS title is the ground truth of WHAT WAS FOUND.
                    // MAL matching is fuzzy and wrong too often — it contributes
                    // the poster and id only, never the displayed name.
                    best.Title = entry.Title;
                    best.TitleEnglish = entry.Title;
                    // Keep the episode/group info on the card instead of MAL's
                    // airing status — it's the point of a "Latest" page.
                    best.Status = SubLabel(entry);
                    var malEntry = entry;
                    var malId = best.MalId;
                    SwapRow(placeholderVm, new SeasonalAnimeVm(
                        best, _seriesService, _mainWindowVm, _onOpenDetails,
                        isInLibrary: libraryMalIds.Contains(best.MalId),
                        isCensored: false,
                        onDownload: () => DownloadEntryAsync(malEntry, malId),
                        onShowDetails: () => ShowReleaseDetailsAsync(malEntry, best, malId)));
                    resolved++;
                }
                else
                {
                    failed++;
                    Console.WriteLine($"[Latest] No MAL match for '{entry.Title}' " +
                                      $"(elapsed {sw.ElapsedMilliseconds}ms — possible rate limit)");
                }

                // Pace every genuine API round trip — including failures, which
                // are the rate-limited case that must NOT fast-loop.
                if (!wasCacheHit) await Task.Delay(900, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"[Latest] Resolve failed for '{entry.Title}': {ex.Message}");
            }
        }

        Console.WriteLine($"[Latest] Resolution done — {offline} offline, {resolved} via MAL, {fromLibrary} from library, {failed} failed");
    }

    /// <summary>
    /// Downloads the exact release behind a Latest card. If the series is in the
    /// library, it queues directly. Otherwise the user chooses: add the series
    /// (via the Add Series search, so THEY pick the right match — not our fuzzy
    /// guess), just download into the library's _Standalone folder, or cancel.
    /// </summary>
    private async Task DownloadEntryAsync(RssEntry entry, int malId)
    {
        if (string.IsNullOrEmpty(entry.Link))
        {
            _mainWindowVm.ShowToast("No download link", entry.Title);
            return;
        }

        var queue = App.Services?.GetService(typeof(Sentrychan.Core.Services.DownloadQueueManager))
            as Sentrychan.Core.Services.DownloadQueueManager;
        if (queue == null) return;

        try
        {
            // Already tracked? Queue straight into the normal pipeline.
            var library = await _seriesService.GetAllAsync();
            var lib = (malId > 0 ? library.FirstOrDefault(s => s.MalId == malId) : null)
                   ?? library.FirstOrDefault(s => _normalizer.MatchesTitle(entry.RawTitle, s.Title));

            if (lib != null)
            {
                await queue.EnqueueAsync(entry.Link, lib.Id, entry.Episode ?? 0, lib.Title, entry.RawTitle);
                _mainWindowVm.ShowToast("Download started",
                    entry.Episode.HasValue ? $"{lib.Title} · Episode {entry.Episode}" : lib.Title);
                return;
            }

            if (Avalonia.Application.Current?.ApplicationLifetime
                    is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                || desktop.MainWindow == null) return;

            var prompt = new Views.Dialogs.AddToLibraryPromptDialog(entry.Title);
            var choice = await prompt.ShowDialog<string?>(desktop.MainWindow);

            switch (choice)
            {
                case "add":
                    var api = App.Services?.GetService(typeof(IAnimeApiService)) as IAnimeApiService;
                    if (api == null) return;
                    var addVm = new AddSeriesViewModel(api, _seriesService) { SearchQuery = entry.Title };
                    var addDialog = new Views.Dialogs.AddSeriesDialog { DataContext = addVm };
                    await addDialog.ShowDialog(desktop.MainWindow);
                    if (addVm.AddedSeries != null)
                    {
                        _mainWindowVm.AddSeriesToLibrary(addVm.AddedSeries);
                        await queue.EnqueueAsync(entry.Link, addVm.AddedSeries.Id,
                            entry.Episode ?? 0, addVm.AddedSeries.Title, entry.RawTitle);
                        _mainWindowVm.ShowToast("Download started", addVm.AddedSeries.Title);
                    }
                    break;

                case "download":
                    // SeriesId 0 routes the completed file to Library/_Standalone/<title>
                    await queue.EnqueueAsync(entry.Link, 0, entry.Episode ?? 0, entry.Title, entry.RawTitle);
                    _mainWindowVm.ShowToast("Download started", $"{entry.Title} → _Standalone");
                    break;
            }
        }
        catch (Exception ex)
        {
            _mainWindowVm.ShowToast("Download failed", ex.Message);
        }
    }

    // ── Secret-mode resolution ────────────────────────────────────

    private static readonly System.Net.Http.HttpClient ThumbHttp = CreateThumbClient();

    private static System.Net.Http.HttpClient CreateThumbClient()
    {
        // sukebei gzips EVERY response whether or not you negotiate for it, and
        // HttpClient does not decompress by default — so GetStringAsync was handing
        // the scraper raw gzip bytes and every regex silently missed. This one line
        // is why doujin thumbnails never resolved.
        var handler = new System.Net.Http.HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip
                                   | System.Net.DecompressionMethods.Deflate
                                   | System.Net.DecompressionMethods.Brotli
        };
        var c = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Sentrychan/2.0");
        return c;
    }

    // Gallery links in a torrent description: e-hentai.org/g/{gid}/{token}/
    private static readonly System.Text.RegularExpressions.Regex EhGalleryPattern =
        new(@"(?:e-hentai|exhentai)\.org/g/(\d+)/([0-9a-fA-F]{10})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
          | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Doujin uploaders rarely embed a cover image — sukebei renders descriptions with
    /// client-side markdown, so the server HTML contains no &lt;img&gt; at all, and the
    /// "cover" is usually a link to an image-host viewer page (not a direct image, and
    /// often with no file extension). What they DO almost always include is an e-hentai
    /// gallery link, and e-hentai exposes a keyless metadata API that hands back a real
    /// cover thumbnail. That's far more reliable than scraping arbitrary hosts.
    /// </summary>
    private static async Task<string?> TryEhentaiCoverAsync(string html, CancellationToken ct)
    {
        var m = EhGalleryPattern.Match(html);
        if (!m.Success) return null;

        var gid   = m.Groups[1].Value;
        var token = m.Groups[2].Value;
        var key   = $"eh:{gid}:{token}";
        if (Sentrychan.UI.Services.ThumbUrlCache.TryGet(key, out var cached)) return cached;

        string? thumb = null;
        try
        {
            var payload = $"{{\"method\":\"gdata\",\"gidlist\":[[{gid},\"{token}\"]],\"namespace\":1}}";
            using var content = new System.Net.Http.StringContent(
                payload, System.Text.Encoding.UTF8, "application/json");
            using var resp = await ThumbHttp.PostAsync("https://api.e-hentai.org/api.php", content, ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("gmetadata", out var meta)
                    && meta.GetArrayLength() > 0
                    && meta[0].TryGetProperty("thumb", out var t))
                {
                    var v = t.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) thumb = v;
                }
            }
        }
        catch { /* gallery may be removed or the API busy — fall through */ }

        Sentrychan.UI.Services.ThumbUrlCache.Set(key, thumb);
        return thumb;
    }

    /// <summary>
    /// Secret-mode ladder: 1) the offline anime DB also covers adult ANIME
    /// (MAL/AniDB index hentai) — canonical id + poster, no scraping needed.
    /// 2) For doujins/manga/unknowns: fetch the sukebei view page and use the
    /// first description image as the thumbnail.
    /// </summary>
    private async Task ResolveSecretDetailsAsync(
        List<(SeasonalAnimeVm Vm, RssEntry Entry)> rows,
        HashSet<int> libraryMalIds,
        CancellationToken ct)
    {
        int fromDb = 0, jav = 0, scraped = 0;

        // These targets are a CDN and static view pages — not a rate-limited API like
        // Jikan — so they're fetched with bounded concurrency instead of one-at-a-time
        // with a sleep after every item. That was the whole reason this page crawled:
        // ~36 rows × (fetch + 400ms) served strictly in series.
        using var gate = new SemaphoreSlim(6);

        await Task.WhenAll(rows.Select(async pair =>
        {
            var (placeholderVm, entry) = pair;
            if (ct.IsCancellationRequested) return;

            await gate.WaitAsync(ct);
            try
            {
                // 1) Offline DB — adult anime resolve with poster
                if (_titleResolver?.IsReady == true)
                {
                    var res = _titleResolver.ResolveRelease(entry.RawTitle);
                    var poster = res?.PictureUrl ?? res?.ThumbnailUrl;
                    if (res != null && !string.IsNullOrEmpty(poster))
                    {
                        var dbResult = new AnimeResult
                        {
                            MalId    = res.MalId,
                            Title    = entry.Title,
                            Status   = SubLabel(entry),
                            Episodes = res.Episodes,
                            Images   = new AnimeImages
                            {
                                Jpg = new AnimeImageSet { LargeImageUrl = poster }
                            }
                        };
                        var dbEntry = entry;
                        var dbMalId = res.MalId;
                        SwapRow(placeholderVm, new SeasonalAnimeVm(
                            dbResult, _seriesService, _mainWindowVm, _onOpenDetails,
                            isInLibrary: res.MalId > 0 && libraryMalIds.Contains(res.MalId),
                            isCensored: true,
                            onDownload: () => DownloadEntryAsync(dbEntry, dbMalId),
                            onShowDetails: () => ShowReleaseDetailsAsync(dbEntry, dbResult, dbMalId)));
                        Interlocked.Increment(ref fromDb);
                        return;
                    }
                }

                // 2) JAV product code → DMM cover CDN (deterministic, keyless)
                var javCover = await GetJavCoverUrlAsync(entry.RawTitle, ct);
                if (!string.IsNullOrEmpty(javCover))
                {
                    var javResult = new AnimeResult
                    {
                        MalId  = entry.RawTitle.GetHashCode(),
                        Title  = entry.Title,
                        Status = SubLabel(entry),
                        Images = new AnimeImages { Jpg = new AnimeImageSet { LargeImageUrl = javCover } }
                    };
                    var jEntry = entry;
                    SwapRow(placeholderVm, new SeasonalAnimeVm(
                        javResult, _seriesService, _mainWindowVm, _onOpenDetails,
                        isInLibrary: false, isCensored: true,
                        onDownload: () => DownloadEntryAsync(jEntry, 0),
                        onShowDetails: () => ShowReleaseDetailsAsync(jEntry, javResult, 0)));
                    Interlocked.Increment(ref jav);
                    return;
                }

                // 3) Thumbnail scrape from the release's view page
                if (!string.IsNullOrEmpty(entry.ViewUrl)
                    && entry.ViewUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    var img = await GetPageThumbnailAsync(entry.ViewUrl, ct);
                    if (!string.IsNullOrEmpty(img))
                    {
                        var scrapedResult = new AnimeResult
                        {
                            MalId  = entry.RawTitle.GetHashCode(),
                            Title  = entry.Title,
                            Status = SubLabel(entry),
                            Images = new AnimeImages
                            {
                                Jpg = new AnimeImageSet { LargeImageUrl = img }
                            }
                        };
                        var scEntry = entry;
                        SwapRow(placeholderVm, new SeasonalAnimeVm(
                            scrapedResult, _seriesService, _mainWindowVm, _onOpenDetails,
                            isInLibrary: false, isCensored: true,
                            onDownload: () => DownloadEntryAsync(scEntry, 0),
                            onShowDetails: () => ShowReleaseDetailsAsync(scEntry, scrapedResult, 0)));
                        Interlocked.Increment(ref scraped);
                    }
                }
            }
            catch (OperationCanceledException) { /* page superseded */ }
            catch { /* non-fatal per-item */ }
            finally { gate.Release(); }
        }));

        // Persist what we learned so the next cold start doesn't re-scrape any of it.
        Sentrychan.UI.Services.ThumbUrlCache.Flush();

        Console.WriteLine($"[Latest] Secret resolution — {fromDb} offline DB, {jav} JAV covers, {scraped} scraped thumbnails");
    }

    private static readonly System.Text.RegularExpressions.Regex JavCodePattern =
        new(@"\b([A-Za-z]{2,6})-(\d{2,5})\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// JAV releases carry a product code (e.g. SSIS-001). DMM's image CDN serves the
    /// cover at a deterministic URL: {label}{number:D5} → pics.dmm.co.jp/.../{cid}pl.jpg.
    /// A content-length gate rejects DMM's ~19KB "now printing" placeholder for
    /// codes it doesn't have. No API key, no scraping. Cached per code.
    /// </summary>
    private static async Task<string?> GetJavCoverUrlAsync(string rawTitle, CancellationToken ct)
    {
        var m = JavCodePattern.Match(rawTitle);
        if (!m.Success) return null;

        var cid = m.Groups[1].Value.ToLowerInvariant() + m.Groups[2].Value.PadLeft(5, '0');
        if (Sentrychan.UI.Services.ThumbUrlCache.TryGet($"jav:{cid}", out var cached)) return cached;

        string? result = null;
        var url = $"https://pics.dmm.co.jp/digital/video/{cid}/{cid}pl.jpg";
        try
        {
            // Header-only fetch — check size without downloading the whole image.
            using var resp = await ThumbHttp.GetAsync(
                url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.IsSuccessStatusCode && (resp.Content.Headers.ContentLength ?? 0) >= 25000)
                result = url;
        }
        catch { /* CDN miss — leave null */ }

        Sentrychan.UI.Services.ThumbUrlCache.Set($"jav:{cid}", result);
        return result;
    }

    /// <summary>
    /// Grabs the first content image from a nyaa/sukebei view page (uploaders
    /// almost always lead the description with a cover). Cached per URL.
    /// </summary>
    private static async Task<string?> GetPageThumbnailAsync(string viewUrl, CancellationToken ct)
    {
        if (Sentrychan.UI.Services.ThumbUrlCache.TryGet($"page:{viewUrl}", out var cached)) return cached;

        string? result = null;
        try
        {
            var html = await ThumbHttp.GetStringAsync(viewUrl, ct);

            // Preferred: an Open Graph cover (doujin/manga uploads that set one)
            var og = System.Text.RegularExpressions.Regex.Match(
                html, "<meta[^>]+property=\"og:image\"[^>]+content=\"(?<u>https?://[^\"]+)\"",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (og.Success) result = og.Groups["u"].Value;

            // Then: an e-hentai gallery link → keyless metadata API → real cover.
            // This is the doujin path; those descriptions almost never contain a
            // direct image, so the regex fallback below can't help them.
            result ??= await TryEhentaiCoverAsync(html, ct);

            // Fallback: first content image embedded in the description
            if (result == null)
            {
                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(
                        html, "https?://[^\\s\"'<>]+\\.(?:jpg|jpeg|png|gif|webp)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    var u = m.Value;
                    if (u.Contains("/static/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (u.Contains("avatar", StringComparison.OrdinalIgnoreCase)) continue;
                    result = u;
                    break;
                }
            }
        }
        catch { /* page fetch is best-effort */ }

        Sentrychan.UI.Services.ThumbUrlCache.Set($"page:{viewUrl}", result);
        return result;
    }

    /// <summary>
    /// Doujin/manga naming: "[Circle (Artist)] Title (Event) [Language] [Digital]".
    /// Returns a readable title plus the circle for the card's sub-label.
    /// </summary>
    private static (string Display, string Circle) ParseDoujinTitle(string raw)
    {
        var s = raw.Trim();
        var circle = string.Empty;

        var lead = System.Text.RegularExpressions.Regex.Match(s, @"^\[(?<c>[^\]]+)\]\s*");
        if (lead.Success)
        {
            circle = lead.Groups["c"].Value.Trim();
            s = s[lead.Length..];
        }

        // Strip trailing tag clusters — (C103), [English], [Digital], (Fate/GO)…
        // but stop before the remaining text gets suspiciously short.
        string prev;
        do
        {
            prev = s;
            var stripped = System.Text.RegularExpressions.Regex.Replace(
                s, @"\s*[\(\[][^\)\]]*[\)\]]\s*$", string.Empty).Trim();
            if (stripped.Length >= 6) s = stripped;
            else break;
        } while (s != prev);

        s = s.Trim(' ', '-', '_');
        return (s.Length >= 3 ? s : raw.Trim(), circle);
    }

    /// <summary>
    /// Opens the torrent-details dialog for a Latest release (category, size,
    /// swarm, hash, date — from the RSS item) with the resolved cover.
    /// Its Download button reuses the same add-or-standalone flow as the card.
    /// </summary>
    private async Task ShowReleaseDetailsAsync(RssEntry entry, AnimeResult result, int malId)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
                is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow == null) return;

        var vm = new ReleaseDetailsViewModel(
            title: entry.Title,
            rawTitle: entry.RawTitle,
            posterUrl: result.LargeImageUrl,
            category: entry.Category,
            sizeDisplay: entry.SizeDisplay,
            seeders: entry.Seeders,
            leechers: entry.Leechers,
            downloads: entry.Downloads,
            infoHash: entry.InfoHash,
            published: entry.Published,
            trusted: entry.Trusted,
            viewUrl: entry.ViewUrl);

        vm.DownloadRequested += () => _ = DownloadEntryAsync(entry, malId);

        // SubsPlease's own RSS carries no seeder/leecher data, so this dialog would
        // show 0/0. Each such item still links to its nyaa page, where the real swarm
        // lives — fetch it in the background and fill the numbers in once available.
        if (entry.Seeders <= 0 && entry.Leechers <= 0)
            _ = EnrichSwarmFromNyaaAsync(vm, entry.Link, entry.ViewUrl);

        var dialog = new Views.Dialogs.ReleaseDetailsDialog(vm);
        await dialog.ShowDialog(desktop.MainWindow);
    }

    // nyaa view id from either a /view/{id} or /view/{id}/torrent URL.
    private static readonly System.Text.RegularExpressions.Regex NyaaIdInUrl =
        new(@"nyaa\.si/view/(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase
                                  | System.Text.RegularExpressions.RegexOptions.Compiled);
    // "Seeders:</div> ... <span ...>1234</span>" on a nyaa view page.
    private static readonly System.Text.RegularExpressions.Regex NyaaSwarmRow =
        new(@"(Seeders|Leechers|Completed)\s*:\s*</div>\s*<div[^>]*>\s*<span[^>]*>\s*([\d,]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
          | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Scrapes seeders/leechers/downloads off the release's nyaa view page and fills
    /// them into an already-open details dialog. Used for feeds (SubsPlease) that omit
    /// swarm data from their RSS. Best-effort: on any failure the dialog just keeps 0s.
    /// </summary>
    private async Task EnrichSwarmFromNyaaAsync(ReleaseDetailsViewModel vm, string? link, string? viewUrl)
    {
        var m = NyaaIdInUrl.Match(link ?? string.Empty);
        if (!m.Success) m = NyaaIdInUrl.Match(viewUrl ?? string.Empty);
        if (!m.Success) return;

        var pageUrl = $"https://nyaa.si/view/{m.Groups[1].Value}";
        try
        {
            var html = await ThumbHttp.GetStringAsync(pageUrl, CancellationToken.None);

            int seeders = 0, leechers = 0, downloads = 0;
            foreach (System.Text.RegularExpressions.Match row in NyaaSwarmRow.Matches(html))
            {
                if (!int.TryParse(row.Groups[2].Value.Replace(",", ""), out var n)) continue;
                switch (row.Groups[1].Value.ToLowerInvariant())
                {
                    case "seeders":   seeders   = n; break;
                    case "leechers":  leechers  = n; break;
                    case "completed": downloads = n; break;
                }
            }

            if (seeders > 0 || leechers > 0 || downloads > 0)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    vm.Seeders   = seeders;
                    vm.Leechers  = leechers;
                    vm.Downloads = downloads;
                });
            }
        }
        catch { /* leave the 0s — the page may be down or the layout changed */ }
    }

    private void SwapRow(SeasonalAnimeVm placeholder, SeasonalAnimeVm resolved)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var idx = AnimeList.IndexOf(placeholder);
            if (idx >= 0) AnimeList[idx] = resolved;
        });
    }

    private static string SubLabel(RssEntry entry)
    {
        var ep = entry.IsBatch ? "BATCH"
               : entry.Episode.HasValue ? $"EP {entry.Episode}" : "New";
        return string.IsNullOrEmpty(entry.Group) || entry.Group == "Unknown"
            ? ep
            : $"{ep} · {entry.Group}";
    }

    private static string ExtractGroup(string rssTitle)
    {
        var match = System.Text.RegularExpressions.Regex.Match(rssTitle, @"^\[(.*?)\]");
        return match.Success ? match.Groups[1].Value : "Unknown";
    }

    private string RemoveSourceGroupAndEpisode(string normalizedTitle)
    {
        // Simple heuristic to extract the base name from a normalized RSS title
        // e.g. "subsplease jujutsu kaisen 24 1080p" -> "jujutsu kaisen"

        var words = normalizedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        // Remove common release groups (often the first word if brackets were removed)
        var knownGroups = new[] { "subsplease", "erai", "commie", "horriblesubs", "nyaa" };
        if (words.Count > 0 && knownGroups.Any(g => words[0].StartsWith(g)))
        {
            words.RemoveAt(0);
        }

        // Remove resolution tags
        words.RemoveAll(w => w == "1080p" || w == "720p" || w == "480p" || w == "x265" || w == "x264");

        // Remove episode number (usually the last or second to last numeric word)
        for (int i = words.Count - 1; i >= 0; i--)
        {
            if (int.TryParse(words[i], out _))
            {
                words.RemoveAt(i);
                // Also remove everything after the episode number (like v2, etc)
                while (words.Count > i) words.RemoveAt(i);
                break;
            }
        }

        return string.Join(" ", words).Trim();
    }
}
