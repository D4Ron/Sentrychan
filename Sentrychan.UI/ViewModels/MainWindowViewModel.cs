using Avalonia.Controls;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using Sentrychan.Core.Data;
using Sentrychan.Core.Events;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Sentrychan.UI.Views.Dialogs;
using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unit = System.Reactive.Unit;
using Sentrychan.Core.Services;
using System.IO;
using Sentrychan.UI.Services;
using Sentrychan.UI.Interfaces;
using System.Windows.Input;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace Sentrychan.UI.ViewModels;
 
public enum AppView
{
    Library, Seasonal, Downloads,
    SeriesDetail, DownloadHub, WatchParty, Files, Latest, News,
    Manga, MangaDetail, MangaReader, Novels, Quiz, Battle
}

public class MainWindowViewModel : ViewModelBase,
    INotificationHandler<NewEpisodeFoundEvent>,
    INotificationHandler<MonitorStatusEvent>,
    INotificationHandler<NewFileArrivedEvent>,
    INotificationHandler<UndoableEpisodeUpdateEvent>,
    INotificationHandler<UnmatchedFileEvent>,
    INotificationHandler<MultiSourceEpisodeEvent>,
    INotificationHandler<DownloadConfirmationEvent>
{
    private readonly ISeriesService _seriesService;
    private readonly IRssMonitorService _rssMonitor;
    private readonly IAnimeApiService? _apiService;
    private readonly IAniDbApiService? _aniDbService;
    private readonly Sentrychan.Core.Services.AniDb.AniDbUdpClient? _aniDbClient;
    private readonly IThemeService? _themeService;
    private readonly QuoteService? _quoteService;
    private readonly IDbContextFactory<AppDbContext>? _dbContextFactory;
    private readonly DownloadQueueManager? _downloadQueue;
    private readonly IVideoFileLocator? _fileLocator;
    private readonly ITitleAliasService? _titleAliasService;
    private readonly Sentrychan.Core.Interfaces.IAccountService? _accountService;
    private readonly Sentrychan.Core.Services.AniDbCoverService? _aniDbCoverService;

    // Set to true once the Avalonia dispatcher is ready.
    // MediatR handlers must not touch Dispatcher.UIThread before this is set,
    // because background services (RSS monitor) can publish events before
    // Avalonia has initialized its platform backend.
    private volatile bool _uiReady;
    public void MarkUiReady() => _uiReady = true;

    // Undo state for episode number updates triggered by file movement
    private UndoableEpisodeUpdateEvent? _pendingUndo;

    // Observable that fires when an undo-able toast should be shown.
    private readonly System.Reactive.Subjects.Subject<(string Title, string Body, string ActionText, ICommand ActionCommand)> _undoToastRequest = new();
    public IObservable<(string Title, string Body, string ActionText, ICommand ActionCommand)> UndoToastRequest => _undoToastRequest.AsObservable();

    // General (non-undo) toast channel: lightweight, auto-dismissing feedback.
    private readonly System.Reactive.Subjects.Subject<(string Title, string Body)> _toastRequest = new();
    public IObservable<(string Title, string Body)> ToastRequest => _toastRequest.AsObservable();

    /// <summary>Show a transient toast notification. Safe to call from any thread.</summary>
    public void ShowToast(string title, string body)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _toastRequest.OnNext((title, body)));
    }

    /// <summary>
    /// Central notification gate. Respects the user's notification level (Off / Important
    /// / All) and Windows-vs-in-app preference. "Important" events (episode/chapter added,
    /// download complete) can fire Windows toasts; routine events stay in-app and only at
    /// the "All" level. Everything used to call TrayService.ShowNotification directly,
    /// which — combined with multiple instances — is what made notifications feel spammy.
    /// </summary>
    public void NotifyUser(string title, string body, bool important)
    {
        if (!Sentrychan.Core.Services.NotificationSettings.ShouldShow(important)) return;

        if (Sentrychan.Core.Services.NotificationSettings.ToWindows(important))
            TrayService.ShowNotification(title, body);
        else
            ShowToast(title, body);
    }

    // ── Observable Properties ──────────────────────────────────────
    private string _statusText = "● Idle";
    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private string _statusColor = "#808080";
    public string StatusColor
    {
        get => _statusColor;
        set => this.RaiseAndSetIfChanged(ref _statusColor, value);
    }

    private string _lastCheckText = "Last check: Never";
    public string LastCheckText
    {
        get => _lastCheckText;
        set => this.RaiseAndSetIfChanged(ref _lastCheckText, value);
    }

    private bool _isSidebarExpanded = true;
    public bool IsSidebarExpanded
    {
        get => _isSidebarExpanded;
        set
        {
            this.RaiseAndSetIfChanged(ref _isSidebarExpanded, value);
            this.RaisePropertyChanged(nameof(SidebarWidth));
        }
    }

    // ── Layout ─────────────────────────────────────────────────────
    private bool _isSidebarHidden;
    public bool IsSidebarHidden
    {
        get => _isSidebarHidden;
        set
        {
            this.RaiseAndSetIfChanged(ref _isSidebarHidden, value);
            this.RaisePropertyChanged(nameof(SidebarWidth));
            this.RaisePropertyChanged(nameof(ShowSidebarReveal));
        }
    }

    /// <summary>214 when shown, 0 when hidden or in Top Bar layout.</summary>
    public double SidebarWidth =>
        IsSidebarLayout && !IsSidebarHidden ? 214 : 0;

    public bool ShowSidebarReveal => IsSidebarLayout && IsSidebarHidden;

    public string[] LayoutModes { get; } = ["Sidebar", "Top Bar"];

    private string _layoutMode = "Sidebar";
    public string LayoutMode
    {
        get => _layoutMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _layoutMode, value);
            this.RaisePropertyChanged(nameof(IsSidebarLayout));
            this.RaisePropertyChanged(nameof(IsTopBarLayout));
            this.RaisePropertyChanged(nameof(SidebarWidth));
            this.RaisePropertyChanged(nameof(ShowSidebarReveal));
        }
    }

    public bool IsSidebarLayout => LayoutMode == "Sidebar";
    public bool IsTopBarLayout => LayoutMode == "Top Bar";

    private bool _isMonitoring;
    public bool IsMonitoring
    {
        get => _isMonitoring;
        set
        {
            this.RaiseAndSetIfChanged(ref _isMonitoring, value);
            this.RaisePropertyChanged(nameof(MonitorButtonText));
            this.RaisePropertyChanged(nameof(MonitorButtonColor));
            this.RaisePropertyChanged(nameof(MonitorStatusText));
        }
    }

    /// <summary>Sidebar label. Used to be the hardcoded literal "Monitoring Active".</summary>
    public string MonitorStatusText => IsMonitoring ? "Monitoring active" : "Monitoring paused";

    private bool _hasPendingEpisodes;
    public bool HasPendingEpisodes
    {
        get => _hasPendingEpisodes;
        set => this.RaiseAndSetIfChanged(ref _hasPendingEpisodes, value);
    }

    private int _pendingCount;
    public int PendingCount
    {
        get => _pendingCount;
        set
        {
            this.RaiseAndSetIfChanged(ref _pendingCount, value);
            HasPendingEpisodes = value > 0;
            this.RaisePropertyChanged(nameof(PendingButtonText));
        }
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    private DownloadsViewModel? _downloadsVm;
    public DownloadsViewModel? DownloadsVm
    {
        get => _downloadsVm;
        set => this.RaiseAndSetIfChanged(ref _downloadsVm, value);
    }

    private DownloadHubViewModel? _downloadHubVm;
    public DownloadHubViewModel? DownloadHubVm
    {
        get => _downloadHubVm;
        set => this.RaiseAndSetIfChanged(ref _downloadHubVm, value);
    }

    private bool _isShowingDownloads;
    public bool IsShowingDownloads
    {
        get => _isShowingDownloads;
        set
        {
            this.RaiseAndSetIfChanged(ref _isShowingDownloads, value);
            this.RaisePropertyChanged(nameof(CurrentPageTitle));
        }
    }

    private bool _isShowingDownloadHub;
    public bool IsShowingDownloadHub
    {
        get => _isShowingDownloadHub;
        set
        {
            this.RaiseAndSetIfChanged(ref _isShowingDownloadHub, value);
            this.RaisePropertyChanged(nameof(CurrentPageTitle));
        }
    }

    private UnmatchedResolverViewModel? _unmatchedResolverVm;
    public UnmatchedResolverViewModel? UnmatchedResolverVm
    {
        get => _unmatchedResolverVm;
        set => this.RaiseAndSetIfChanged(ref _unmatchedResolverVm, value);
    }

    private bool _isShowingUnmatchedResolver;
    public bool IsShowingUnmatchedResolver
    {
        get => _isShowingUnmatchedResolver;
        set
        {
            this.RaiseAndSetIfChanged(ref _isShowingUnmatchedResolver, value);
            this.RaisePropertyChanged(nameof(CurrentPageTitle));
            this.RaisePropertyChanged(nameof(IsShowingLibrary));
        }
    }

    private FileManagementViewModel? _fileManagementVm;
    public FileManagementViewModel? FileManagementVm
    {
        get => _fileManagementVm;
        set => this.RaiseAndSetIfChanged(ref _fileManagementVm, value);
    }

    private bool _isShowingFiles;
    public bool IsShowingFiles
    {
        get => _isShowingFiles;
        set
        {
            this.RaiseAndSetIfChanged(ref _isShowingFiles, value);
            this.RaisePropertyChanged(nameof(CurrentPageTitle));
        }
    }

    // ── Background image ───────────────────────────────────────────
    private string _backgroundImagePath = string.Empty;
    public string BackgroundImagePath
    {
        get => _backgroundImagePath;
        set
        {
            this.RaiseAndSetIfChanged(ref _backgroundImagePath, value);
            this.RaisePropertyChanged(nameof(HasBackgroundImage));
        }
    }
    public bool HasBackgroundImage => !string.IsNullOrEmpty(_backgroundImagePath)
                                      && File.Exists(_backgroundImagePath);

    // ── Unmatched-file notification banner ────────────────────────
    private bool _showUnmatchedBanner;
    public bool ShowUnmatchedBanner
    {
        get => _showUnmatchedBanner;
        set => this.RaiseAndSetIfChanged(ref _showUnmatchedBanner, value);
    }

    private string _unmatchedBannerText = string.Empty;
    public string UnmatchedBannerText
    {
        get => _unmatchedBannerText;
        set => this.RaiseAndSetIfChanged(ref _unmatchedBannerText, value);
    }

    private string _unmatchedFolderPath = string.Empty;
    public string UnmatchedFolderPath
    {
        get => _unmatchedFolderPath;
        set => this.RaiseAndSetIfChanged(ref _unmatchedFolderPath, value);
    }

    public ReactiveCommand<Unit, Unit> DismissUnmatchedBannerCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenUnmatchedFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowUnmatchedResolverCommand { get; }

    // ── Watch Party ────────────────────────────────────────────────
    private WatchPartyViewModel? _watchPartyVm;
    public WatchPartyViewModel? WatchPartyVm
    {
        get => _watchPartyVm;
        set => this.RaiseAndSetIfChanged(ref _watchPartyVm, value);
    }

    private bool _isShowingWatchParty;
    public bool IsShowingWatchParty
    {
        get => _isShowingWatchParty;
        set
        {
            this.RaiseAndSetIfChanged(ref _isShowingWatchParty, value);
            this.RaisePropertyChanged(nameof(CurrentPageTitle));
        }
    }

    private SeasonalViewModel? _seasonalVm;
    public SeasonalViewModel? SeasonalVm
    {
        get => _seasonalVm;
        set => this.RaiseAndSetIfChanged(ref _seasonalVm, value);
    }

    private LatestArrivalsViewModel? _latestArrivalsVm;
    public LatestArrivalsViewModel? LatestArrivalsVm
    {
        get => _latestArrivalsVm;
        set => this.RaiseAndSetIfChanged(ref _latestArrivalsVm, value);
    }

    private bool _isShowingLatest;
    public bool IsShowingLatest
    {
        get => _isShowingLatest;
        set
        {
            this.RaiseAndSetIfChanged(ref _isShowingLatest, value);
            this.RaisePropertyChanged(nameof(CurrentPageTitle));
        }
    }

    private NewsViewModel? _newsVm;
    public NewsViewModel? NewsVm
    {
        get => _newsVm;
        set => this.RaiseAndSetIfChanged(ref _newsVm, value);
    }

    private bool _isShowingNews;
    public bool IsShowingNews
    {
        get => _isShowingNews;
        set
        {
            this.RaiseAndSetIfChanged(ref _isShowingNews, value);
            this.RaisePropertyChanged(nameof(CurrentPageTitle));
        }
    }

    // ── Manga (Stage 1) ────────────────────────────────────────────
    private MangaLibraryViewModel? _mangaLibraryVm;
    public MangaLibraryViewModel? MangaLibraryVm
    {
        get => _mangaLibraryVm;
        set => this.RaiseAndSetIfChanged(ref _mangaLibraryVm, value);
    }

    // Novels reuse the manga library view, but in "novel mode" (own sources + library),
    // so light/web-novel sources aren't crowded in with the comic sources.
    private MangaLibraryViewModel? _novelLibraryVm;
    public MangaLibraryViewModel? NovelLibraryVm
    {
        get => _novelLibraryVm;
        set => this.RaiseAndSetIfChanged(ref _novelLibraryVm, value);
    }

    private bool _isShowingNovels;
    public bool IsShowingNovels
    {
        get => _isShowingNovels;
        set
        {
            this.RaiseAndSetIfChanged(ref _isShowingNovels, value);
            this.RaisePropertyChanged(nameof(CurrentPageTitle));
        }
    }

    private AnimeQuizViewModel? _animeQuizVm;
    public AnimeQuizViewModel? AnimeQuizVm
    {
        get => _animeQuizVm;
        set => this.RaiseAndSetIfChanged(ref _animeQuizVm, value);
    }

    private bool _isShowingQuiz;
    public bool IsShowingQuiz
    {
        get => _isShowingQuiz;
        set
        {
            this.RaiseAndSetIfChanged(ref _isShowingQuiz, value);
            this.RaisePropertyChanged(nameof(CurrentPageTitle));
            this.RaisePropertyChanged(nameof(InboxVisible));
            this.RaisePropertyChanged(nameof(InboxPillVisible));
        }
    }

    private BattleRoyaleViewModel? _battleRoyaleVm;
    public BattleRoyaleViewModel? BattleRoyaleVm
    {
        get => _battleRoyaleVm;
        set => this.RaiseAndSetIfChanged(ref _battleRoyaleVm, value);
    }

    private bool _isShowingBattle;
    public bool IsShowingBattle
    {
        get => _isShowingBattle;
        set
        {
            this.RaiseAndSetIfChanged(ref _isShowingBattle, value);
            this.RaisePropertyChanged(nameof(CurrentPageTitle));
            this.RaisePropertyChanged(nameof(InboxVisible));
            this.RaisePropertyChanged(nameof(InboxPillVisible));
        }
    }

    private MangaDetailViewModel? _mangaDetailVm;
    public MangaDetailViewModel? MangaDetailVm
    {
        get => _mangaDetailVm;
        set => this.RaiseAndSetIfChanged(ref _mangaDetailVm, value);
    }

    private bool _isShowingManga;
    public bool IsShowingManga
    {
        get => _isShowingManga;
        set
        {
            this.RaiseAndSetIfChanged(ref _isShowingManga, value);
            this.RaisePropertyChanged(nameof(CurrentPageTitle));
        }
    }

    private bool _isShowingMangaDetail;
    public bool IsShowingMangaDetail
    {
        get => _isShowingMangaDetail;
        set
        {
            this.RaiseAndSetIfChanged(ref _isShowingMangaDetail, value);
            this.RaisePropertyChanged(nameof(CurrentPageTitle));
        }
    }

    private MangaReaderViewModel? _mangaReaderVm;
    public MangaReaderViewModel? MangaReaderVm
    {
        get => _mangaReaderVm;
        set => this.RaiseAndSetIfChanged(ref _mangaReaderVm, value);
    }

    private bool _isShowingMangaReader;
    public bool IsShowingMangaReader
    {
        get => _isShowingMangaReader;
        set
        {
            this.RaiseAndSetIfChanged(ref _isShowingMangaReader, value);
            this.RaisePropertyChanged(nameof(CurrentPageTitle));
            // Chrome (sidebar/topbar) is hidden while reading — see IsChromeVisible.
            this.RaisePropertyChanged(nameof(IsChromeVisible));
            this.RaisePropertyChanged(nameof(InboxVisible));
            this.RaisePropertyChanged(nameof(InboxPillVisible));
        }
    }

    /// <summary>The reader takes over the whole window, so the nav chrome hides for it.</summary>
    public bool IsChromeVisible => !IsShowingMangaReader;

    private bool _isShowingSeasonal;
    public bool IsShowingSeasonal
    {
        get => _isShowingSeasonal;
        set
        {
            this.RaiseAndSetIfChanged(ref _isShowingSeasonal, value);
            this.RaisePropertyChanged(nameof(CurrentPageTitle));
        }
    }

    private SeriesDetailViewModel? _seriesDetailVm;
    public SeriesDetailViewModel? SeriesDetailVm
    {
        get => _seriesDetailVm;
        set => this.RaiseAndSetIfChanged(ref _seriesDetailVm, value);
    }

    private bool _isShowingSeriesDetail;
    public bool IsShowingSeriesDetail
    {
        get => _isShowingSeriesDetail;
        set
        {
            this.RaiseAndSetIfChanged(ref _isShowingSeriesDetail, value);
            this.RaisePropertyChanged(nameof(CurrentPageTitle));
        }
    }

    private bool _isLibraryEmpty;
    public bool IsLibraryEmpty
    {
        get => _isLibraryEmpty;
        set
        {
            this.RaiseAndSetIfChanged(ref _isLibraryEmpty, value);
            this.RaisePropertyChanged(nameof(IsShowingLibraryEmptyState));
        }
    }

    public bool IsShowingLibraryEmptyState => IsShowingLibrary && IsLibraryEmpty;

    public bool HasAiring => AiringList.Any();
    public bool HasCompleted => CompletedList.Any();
    public bool HasRecent => RecentList.Any();
    public bool HasCensored => CensoredList.Any() && (_themeService?.IsSecretMode ?? false);
    public bool HasOther => OtherList.Any();
    public bool ShowAllSeriesFallback => !HasAiring && !HasRecent && !HasCompleted && !HasCensored && !HasOther && FilteredSeriesList.Any();

    // ── Library category filter ────────────────────────────────────
    public string[] LibraryCategories { get; } = ["All", "Recently Added", "Currently Airing", "Completed", "Other"];

    private string _libraryCategory = "All";
    public string LibraryCategory
    {
        get => _libraryCategory;
        set
        {
            this.RaiseAndSetIfChanged(ref _libraryCategory, value);
            RaiseSectionVisibility();
        }
    }

    private bool CatShows(string cat) =>
        LibraryCategory == "All" || LibraryCategory == cat;

    // Sections respect both content presence AND the active category chip.
    public bool ShowRecentSection    => HasRecent    && CatShows("Recently Added");
    public bool ShowAiringSection    => HasAiring    && CatShows("Currently Airing");
    public bool ShowCompletedSection => HasCompleted && CatShows("Completed");
    public bool ShowOtherSection     => HasOther     && CatShows("Other");
    public bool ShowCensoredSection  => HasCensored  && LibraryCategory == "All";

    private void RaiseSectionVisibility()
    {
        this.RaisePropertyChanged(nameof(ShowRecentSection));
        this.RaisePropertyChanged(nameof(ShowAiringSection));
        this.RaisePropertyChanged(nameof(ShowCompletedSection));
        this.RaisePropertyChanged(nameof(ShowOtherSection));
        this.RaisePropertyChanged(nameof(ShowCensoredSection));
    }

    private bool _isShowingLibrary;
    public bool IsShowingLibrary
    {
        get => _isShowingLibrary;
        set
        {
            this.RaiseAndSetIfChanged(ref _isShowingLibrary, value);
            this.RaisePropertyChanged(nameof(CurrentPageTitle));
            this.RaisePropertyChanged(nameof(CurrentPageCountText));
        }
    }

    // ── Computed Properties ────────────────────────────────────────
    public string MonitorButtonText => IsMonitoring ? "Stop Monitoring" : "Start Monitoring";
    public string MonitorButtonColor => IsMonitoring ? "#F44336" : "#00C853";
    public string PendingButtonText => $"⚠️ Pending ({PendingCount})";

    public string CurrentPageTitle => IsShowingFiles ? "Files"
        : IsShowingDownloads ? "Downloads"
        : IsShowingDownloadHub ? "Download Hub"
        : IsShowingLatest ? "Latest"
        : IsShowingManga ? "Manga"
        : IsShowingNovels ? "Novels"
        : IsShowingQuiz ? "Anime Quiz"
        : IsShowingBattle ? "OP Battle Royale"
        : IsShowingMangaDetail ? "Manga Details"
        : IsShowingNews ? "News"
        : IsShowingSeasonal ? "Discover"
        : IsShowingSeriesDetail ? "Series Details"
        : IsShowingWatchParty ? "Watch Party"
        : IsShowingUnmatchedResolver ? "Unmatched Files"
        : "Library";

    // ── Collections ────────────────────────────────────────────────
    public ObservableCollection<Series> SeriesList { get; } = [];
    public ObservableCollection<Series> AiringList { get; } = [];
    public ObservableCollection<Series> CompletedList { get; } = [];
    public ObservableCollection<Series> RecentList { get; } = [];
    public ObservableCollection<Series> CensoredList { get; } = [];

    /// <summary>Catch-all so nothing is ever dropped: unknown/missing/upcoming statuses.</summary>
    public ObservableCollection<Series> OtherList { get; } = [];

    /// <summary>Filtered "all series" set backing the fallback grid and the search results.</summary>
    public ObservableCollection<Series> FilteredSeriesList { get; } = [];

    // ── Airing Today (Library right panel) ─────────────────────────
    public ObservableCollection<AiringTodayRowVm> AiringTodayList { get; } = [];

    private bool _hasAiringToday;
    public bool HasAiringToday
    {
        get => _hasAiringToday;
        set => this.RaiseAndSetIfChanged(ref _hasAiringToday, value);
    }

    private string _airingTodayStatus = "Loading…";
    public string AiringTodayStatus
    {
        get => _airingTodayStatus;
        set => this.RaiseAndSetIfChanged(ref _airingTodayStatus, value);
    }

    public string TodayLabel => DateTime.Now.ToString("dddd");

    /// <summary>
    /// Loads today's release schedule from SubsPlease (their live calendar, times
    /// already localized). The user's own library shows are matched via the title
    /// resolver, pinned to the top and badged. Best-effort, cached, background.
    /// </summary>
    private async Task LoadAiringTodayAsync()
    {
        var schedule = App.Services?.GetService(typeof(IAiringScheduleService)) as IAiringScheduleService;
        if (schedule == null) return;

        try
        {
            var entries = await schedule.GetTodayAsync();
            var resolver = App.Services?.GetService(typeof(ITitleResolverService)) as ITitleResolverService;

            var rows = new List<AiringTodayRowVm>();
            foreach (var e in entries)
            {
                // Resolve the SubsPlease title to a canonical anime (MAL id + poster).
                ResolvedAnime? res = resolver?.IsReady == true ? resolver.ResolveTitle(e.Title) : null;
                int malId = res?.MalId ?? 0;
                bool inLib = malId > 0 && _allSeries.Any(s => s.MalId == malId);

                var entry = e;
                var resolved = res;
                rows.Add(new AiringTodayRowVm(
                    e.Title, e.PosterUrl, e.Time, inLibrary: inLib,
                    onOpen: () => _ = ShowAnimePreviewAsync(
                        malId,
                        resolved?.CanonicalTitle is { Length: > 0 } ct ? ct : entry.Title,
                        string.IsNullOrEmpty(resolved?.PictureUrl) ? entry.PosterUrl : resolved!.PictureUrl!,
                        resolved?.Episodes,
                        MapStatus(resolved?.Status),
                        resolved?.Year,
                        entry.PageUrl)));
            }

            var ordered = rows.OrderByDescending(r => r.InLibrary).ToList();

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                AiringTodayList.Clear();
                foreach (var row in ordered) AiringTodayList.Add(row);
                HasAiringToday = AiringTodayList.Count > 0;
                AiringTodayStatus = AiringTodayList.Count > 0 ? string.Empty : "Nothing releasing today.";
                this.RaisePropertyChanged(nameof(TodayLabel));
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AiringToday] load failed: {ex.Message}");
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                AiringTodayStatus = "Couldn't load the schedule.");
        }
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private static string MapStatus(string? offlineStatus) => offlineStatus switch
    {
        "ONGOING"  => "Currently Airing",
        "UPCOMING" => "Not yet aired",
        "FINISHED" => "Finished Airing",
        _          => offlineStatus ?? string.Empty
    };

    /// <summary>
    /// Opens the lightweight anime preview popup with a direct "Add to Library"
    /// action. Enriches the synopsis from Jikan by-id (that endpoint works even
    /// when search is down). malId 0 → falls back to opening the source page.
    /// </summary>
    public async Task ShowAnimePreviewAsync(
        int malId, string title, string posterUrl, int? episodes,
        string status, int? year, string pageUrl)
    {
        var owner = GetMainWindow();
        if (owner == null || _seriesService == null) return;

        bool inLibrary = malId > 0 && _allSeries.Any(s => s.MalId == malId);
        var vm = new AnimePreviewViewModel(
            _seriesService, this, malId, title, posterUrl, episodes, status, year, inLibrary, pageUrl);

        var dialog = new Sentrychan.UI.Views.Dialogs.AnimePreviewDialog(vm);

        // Fetch the synopsis in the background — the by-id endpoint is reliable.
        if (malId > 0 && _apiService != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var details = await _apiService.GetAnimeByIdAsync(malId);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => vm.SetSynopsis(details?.Synopsis));
                }
                catch { Avalonia.Threading.Dispatcher.UIThread.Post(() => vm.SetSynopsis(null)); }
            });
        }
        else vm.SetSynopsis(null);

        await dialog.ShowDialog(owner);
    }

    // Full, unfiltered library — the source of truth that search filters against.
    private readonly List<Series> _allSeries = [];

    /// <summary>Live text from the topbar search box; re-filters the library as you type.</summary>
    private string _librarySearchText = string.Empty;
    public string LibrarySearchText
    {
        get => _librarySearchText;
        set
        {
            this.RaiseAndSetIfChanged(ref _librarySearchText, value);
            RebuildLibraryLists();
        }
    }

    /// <summary>Topbar count — page-aware, and shows match count while searching.
    /// Censored series are hidden outside secret mode, so exclude them from the count too.</summary>
    public string CurrentPageCountText
    {
        get
        {
            if (!IsShowingLibrary) return string.Empty;
            bool secret = _themeService?.IsSecretMode ?? false;
            int total = secret ? _allSeries.Count : _allSeries.Count(s => !s.IsCensored);
            if (!string.IsNullOrWhiteSpace(LibrarySearchText))
            {
                int visible = secret
                    ? FilteredSeriesList.Count
                    : FilteredSeriesList.Count(s => !s.IsCensored);
                return $"{visible} of {total}";
            }
            return total == 1 ? "1 series" : $"{total} series";
        }
    }
    public ObservableCollection<Series> ContinueWatchingList { get; } = [];
    private bool _hasContinueWatching;
    public bool HasContinueWatching
    {
        get => _hasContinueWatching;
        set => this.RaiseAndSetIfChanged(ref _hasContinueWatching, value);
    }
    public ObservableCollection<NewEpisodeFoundEvent> PendingEpisodes { get; } = [];

    /// <summary>
    /// Episodes where the monitor found multiple release groups and needs the user to pick one.
    /// Bound to the source-picker notification strip in MainWindow.axaml.
    /// </summary>
    public ObservableCollection<SourcePickVm> SourcePicks { get; } = [];

    private bool _hasSourcePicks;
    public bool HasSourcePicks
    {
        get => _hasSourcePicks;
        set => this.RaiseAndSetIfChanged(ref _hasSourcePicks, value);
    }

    /// <summary>
    /// Episodes awaiting user confirmation before downloading.
    /// Populated when AutoDownload=false on the series.
    /// </summary>
    public ObservableCollection<DownloadConfirmVm> PendingConfirms { get; } = [];

    public bool HasPendingConfirms => PendingConfirms.Count > 0;

    /// <summary>
    /// Whether the floating "New Episodes" inbox shows its full list (true) or is
    /// collapsed to just its header bar (false). Toggled from the inbox header.
    /// </summary>
    private bool _isPendingInboxExpanded = true;
    public bool IsPendingInboxExpanded
    {
        get => _isPendingInboxExpanded;
        set => this.RaiseAndSetIfChanged(ref _isPendingInboxExpanded, value);
    }

    /// <summary>User hid the inbox entirely; a small pill offers to reopen it.</summary>
    private bool _isInboxHidden;
    public bool IsInboxHidden
    {
        get => _isInboxHidden;
        set
        {
            this.RaiseAndSetIfChanged(ref _isInboxHidden, value);
            this.RaisePropertyChanged(nameof(InboxVisible));
            this.RaisePropertyChanged(nameof(InboxPillVisible));
        }
    }

    // The floating inbox is bottom-right where the quiz/reader put their controls — hide it there.
    public bool InboxVisible => HasPendingConfirms && !IsInboxHidden && !IsShowingQuiz && !IsShowingBattle && !IsShowingMangaReader;
    public bool InboxPillVisible => HasPendingConfirms && IsInboxHidden && !IsShowingQuiz && !IsShowingBattle && !IsShowingMangaReader;

    // ── Commands ───────────────────────────────────────────────────
    public ReactiveCommand<Unit, Unit> ToggleMonitoringCommand { get; }
    public ReactiveCommand<Unit, Unit> ManualCheckCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshSeriesCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenPendingCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowSeriesCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowSeasonalCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowLatestCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowNewsCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowMangaCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowNovelsCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowQuizCommand { get; }
    public ReactiveCommand<Unit, Unit> GoBackToMangaCommand { get; }
    public ReactiveCommand<Unit, Unit> RescanLibraryCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowDownloadsCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowDownloadHubCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowFilesCommand { get; }
    public ReactiveCommand<Unit, Unit> AddSeriesCommand { get; }
    public ReactiveCommand<Unit, Unit> AddFeedCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowSettingsCommand { get; }
    public ReactiveCommand<Series, Unit> OpenSeriesDetailsCommand { get; }
    public ReactiveCommand<Series, Unit> ChangePosterCommand { get; }
    public ReactiveCommand<Series, Unit> DownloadPosterCommand { get; }
    public ReactiveCommand<Series, Unit> LookForEpisodeCommand { get; }
    public ReactiveCommand<Series, Unit> DownloadAllMissingCommand { get; }
    public ReactiveCommand<Series, Unit> RemoveSeriesCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleSidebarCommand { get; }
    public ReactiveCommand<Unit, Unit> PlayWelcomeCommand { get; }
    public ReactiveCommand<Unit, Unit> GoBackToLibraryCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowAccountCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowWatchPartyCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowCreatePartyCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowJoinPartyCommand { get; }
    public ReactiveCommand<int, Unit>? OpenSeriesDetailByIdCommand { get; private set; }

    // ── Account ────────────────────────────────────────────────────
    private AccountViewModel? _accountVm;
    public AccountViewModel? AccountVm
    {
        get => _accountVm;
        set => this.RaiseAndSetIfChanged(ref _accountVm, value);
    }

    // ── Design-time constructor ────────────────────────────────────
    public MainWindowViewModel()
    {
        _seriesService = null!;
        _rssMonitor = null!;
        
        ToggleMonitoringCommand = ReactiveCommand.CreateFromTask(ToggleMonitoringAsync);
        ManualCheckCommand = ReactiveCommand.CreateFromTask(ManualCheckAsync);
        RefreshSeriesCommand = ReactiveCommand.CreateFromTask(LoadSeriesAsync);
        OpenPendingCommand = ReactiveCommand.Create(() => { });
        ShowSeriesCommand = ReactiveCommand.Create(() => SetCurrentView(AppView.Library));
        ShowSeasonalCommand = ReactiveCommand.Create(ShowSeasonal);
        ShowLatestCommand = ReactiveCommand.Create(ShowLatest);
        ShowNewsCommand = ReactiveCommand.Create(ShowNews);
        ShowMangaCommand = ReactiveCommand.Create(ShowManga);
        ShowNovelsCommand = ReactiveCommand.Create(ShowNovels);
        ShowQuizCommand = ReactiveCommand.Create(ShowQuiz);
        GoBackToMangaCommand = ReactiveCommand.Create(ShowManga);
        RescanLibraryCommand = ReactiveCommand.CreateFromTask(() => RescanLibraryAsync(silent: false));
        ShowDownloadsCommand = ReactiveCommand.Create(() => {
            SetCurrentView(AppView.Downloads);
            DownloadsVm?.LoadJobsCommand.Execute().Subscribe();
        });
        ShowDownloadHubCommand = ReactiveCommand.Create(() =>
        {
            SetCurrentView(AppView.DownloadHub);
            DownloadHubVm?.RefreshForMode();
        });
        AddSeriesCommand = ReactiveCommand.CreateFromTask(OpenAddSeriesAsync);
        AddFeedCommand = ReactiveCommand.CreateFromTask(OpenAddFeedAsync);
        ShowSettingsCommand = ReactiveCommand.CreateFromTask(OpenSettingsAsync);
        OpenSeriesDetailsCommand = ReactiveCommand.CreateFromTask<Series>(OpenSeriesDetailsAsync);
        ChangePosterCommand = ReactiveCommand.CreateFromTask<Series>(OpenPosterOptionsAsync);
        DownloadPosterCommand = ReactiveCommand.CreateFromTask<Series>(DownloadPosterAsync);
        LookForEpisodeCommand = ReactiveCommand.CreateFromTask<Series>(LookForEpisodeAsync);
        DownloadAllMissingCommand = ReactiveCommand.Create<Series>(_ => { });
        RemoveSeriesCommand = ReactiveCommand.CreateFromTask<Series>(RemoveSeriesAsync);
        ToggleSidebarCommand = ReactiveCommand.Create(() => { IsSidebarExpanded = !IsSidebarExpanded; });
        PlayWelcomeCommand = ReactiveCommand.Create(() => { });
        GoBackToLibraryCommand = ReactiveCommand.Create(() => SetCurrentView(AppView.Library));
        DismissUnmatchedBannerCommand = ReactiveCommand.Create(() => { ShowUnmatchedBanner = false; });
        OpenUnmatchedFolderCommand = ReactiveCommand.Create(() =>
        {
            if (!string.IsNullOrEmpty(UnmatchedFolderPath) && Directory.Exists(UnmatchedFolderPath))
                System.Diagnostics.Process.Start("explorer.exe", UnmatchedFolderPath);
        });
        ShowAccountCommand      = ReactiveCommand.Create(() => { });
        ShowWatchPartyCommand  = ReactiveCommand.Create(() => { });
        ShowCreatePartyCommand = ReactiveCommand.Create(() => { });
        ShowJoinPartyCommand   = ReactiveCommand.Create(() => { });
        OpenSeriesDetailByIdCommand = ReactiveCommand.Create<int>(_ => { });
        ShowUnmatchedResolverCommand = ReactiveCommand.Create(() => { });
        ShowFilesCommand = ReactiveCommand.Create(() => SetCurrentView(AppView.Files));
        SetCurrentView(AppView.Library);
    }

    // ── Runtime constructor ────────────────────────────────────────
    public MainWindowViewModel(
        ISeriesService seriesService,
        IRssMonitorService rssMonitor,
        IAnimeApiService apiService,
        IAniDbApiService aniDbService,
        IDbContextFactory<AppDbContext> dbContextFactory,
        DownloadQueueManager downloadQueue,
        IWatchPartyHostService watchPartyHost,
        IWatchPartyClientService watchPartyClient,
        IPlayerBridgeService playerBridge,
        ISyncEngine syncEngine,
        IReactionOverlayService reactionOverlay,
        IVoiceChatService voiceChat,
        ITunnelService tunnelService,
        ILanDiscoveryService lanDiscovery,
        Sentrychan.Core.Services.AniDb.AniDbUdpClient aniDbClient,
        IThemeService themeService,
        QuoteService quoteService,
        IVideoFileLocator fileLocator,
        ITitleAliasService titleAliasService)
    {
        _seriesService  = seriesService;
        _rssMonitor     = rssMonitor;
        _apiService     = apiService;
        _aniDbService   = aniDbService;
        _dbContextFactory = dbContextFactory;
        _downloadQueue  = downloadQueue;
        _aniDbClient    = aniDbClient;
        _themeService   = themeService;
        _quoteService   = quoteService;
        _fileLocator    = fileLocator;
        _titleAliasService = titleAliasService;
        _accountService = App.Services?.GetService<Sentrychan.Core.Interfaces.IAccountService>();
        _aniDbCoverService = App.Services?.GetService<Sentrychan.Core.Services.AniDbCoverService>();

        ToggleMonitoringCommand = ReactiveCommand.CreateFromTask(ToggleMonitoringAsync);
        ManualCheckCommand = ReactiveCommand.CreateFromTask(ManualCheckAsync);
        RefreshSeriesCommand = ReactiveCommand.CreateFromTask(LoadSeriesAsync);
        OpenPendingCommand = ReactiveCommand.Create(() => { });
        ShowSeriesCommand = ReactiveCommand.Create(() => SetCurrentView(AppView.Library));
        ShowSeasonalCommand = ReactiveCommand.Create(ShowSeasonal);
        ShowLatestCommand = ReactiveCommand.Create(ShowLatest);
        ShowNewsCommand = ReactiveCommand.Create(ShowNews);
        ShowMangaCommand = ReactiveCommand.Create(ShowManga);
        ShowNovelsCommand = ReactiveCommand.Create(ShowNovels);
        ShowQuizCommand = ReactiveCommand.Create(ShowQuiz);
        GoBackToMangaCommand = ReactiveCommand.Create(ShowManga);
        RescanLibraryCommand = ReactiveCommand.CreateFromTask(() => RescanLibraryAsync(silent: false));
        ShowDownloadsCommand = ReactiveCommand.Create(() => {
            SetCurrentView(AppView.Downloads);
            DownloadsVm?.LoadJobsCommand.Execute().Subscribe();
        });
        ShowDownloadHubCommand = ReactiveCommand.Create(() =>
        {
            SetCurrentView(AppView.DownloadHub);
            DownloadHubVm?.RefreshForMode();
        });
        AddSeriesCommand = ReactiveCommand.CreateFromTask(OpenAddSeriesAsync);
        AddFeedCommand = ReactiveCommand.CreateFromTask(OpenAddFeedAsync);
        ShowSettingsCommand = ReactiveCommand.CreateFromTask(OpenSettingsAsync);
        OpenSeriesDetailsCommand = ReactiveCommand.CreateFromTask<Series>(OpenSeriesDetailsAsync);
        ChangePosterCommand = ReactiveCommand.CreateFromTask<Series>(OpenPosterOptionsAsync);
        DownloadPosterCommand = ReactiveCommand.CreateFromTask<Series>(DownloadPosterAsync);
        LookForEpisodeCommand = ReactiveCommand.CreateFromTask<Series>(LookForEpisodeAsync);
        DownloadAllMissingCommand = ReactiveCommand.CreateFromTask<Series>(DownloadAllEpisodesAsync);
        RemoveSeriesCommand = ReactiveCommand.CreateFromTask<Series>(RemoveSeriesAsync);
        ToggleSidebarCommand = ReactiveCommand.Create(() => { IsSidebarExpanded = !IsSidebarExpanded; });
        PlayWelcomeCommand = ReactiveCommand.Create(() => { });
        GoBackToLibraryCommand = ReactiveCommand.Create(() => SetCurrentView(AppView.Library));
        DismissUnmatchedBannerCommand = ReactiveCommand.Create(() => { ShowUnmatchedBanner = false; });
        OpenUnmatchedFolderCommand = ReactiveCommand.Create(() =>
        {
            if (!string.IsNullOrEmpty(UnmatchedFolderPath) && Directory.Exists(UnmatchedFolderPath))
                System.Diagnostics.Process.Start("explorer.exe", UnmatchedFolderPath);
        });
        ShowAccountCommand    = ReactiveCommand.CreateFromTask(OpenAccountDialogAsync);
        ShowWatchPartyCommand  = ReactiveCommand.Create(ShowWatchParty);
        ShowCreatePartyCommand = ReactiveCommand.Create(ShowWatchParty);
        ShowJoinPartyCommand   = ReactiveCommand.Create(ShowWatchParty);
        OpenSeriesDetailByIdCommand = ReactiveCommand.CreateFromTask<int>(async (id, ct) =>
        {
            await using var db = await _dbContextFactory!.CreateDbContextAsync(ct);
            var series = await db.Series.FindAsync(new object[] { id }, ct);
            if (series != null) OpenSeriesDetails(series);
        });
        ShowUnmatchedResolverCommand = ReactiveCommand.Create(() =>
        {
            if (_dbContextFactory == null) return;
            var normalizer = App.Services?.GetService(typeof(IEpisodeNormalizer)) as IEpisodeNormalizer;
            var pipeline   = App.Services?.GetService(typeof(IFileMovementPipeline)) as IFileMovementPipeline;
            if (normalizer == null || pipeline == null) return;

            UnmatchedResolverVm = new UnmatchedResolverViewModel(_dbContextFactory, normalizer, pipeline);
            SetCurrentView(AppView.Library); // reset all flags via SetCurrentView, then override
            IsShowingLibrary = false;
            IsShowingUnmatchedResolver = true;
            ShowUnmatchedBanner = false;
        });
        ShowFilesCommand = ReactiveCommand.Create(() =>
        {
            SetCurrentView(AppView.Files);
            FileManagementVm?.Downloads.LoadJobsCommand.Execute().Subscribe();
        });

        _themeService.ThemeChanged += (isSecret) =>
        {
            if (isSecret)
            {
                // Play the overlay FIRST — it fades in over ~500ms and covers the
                // whole window. Deferring the censored-section reveal until the
                // screen is covered stops the mass SeriesCard materialization
                // (and its poster decodes) from fighting the overlay animation
                // for the UI thread, which made the animation stutter.
                PlayWelcomeCommand.Execute().Subscribe();

                _ = Task.Delay(800).ContinueWith(_ =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        this.RaisePropertyChanged(nameof(HasCensored));
                        this.RaisePropertyChanged(nameof(CurrentPageCountText));
                    });
                    // Fetch AniDB covers for censored series in the background
                    _ = FetchAniDbCoversAsync(CancellationToken.None);
                });
            }
            else
            {
                // Deactivation is cheap (section collapses) — update immediately
                this.RaisePropertyChanged(nameof(HasCensored));
                this.RaisePropertyChanged(nameof(CurrentPageCountText));
            }

            // Latest + Search feeds differ by mode (secret uses sukebei) — reload
            // so the user doesn't have to refresh manually after toggling.
            LatestArrivalsVm?.RefreshCommand.Execute().Subscribe();
            DownloadHubVm?.RefreshForMode();
        };

        // Build AccountVm using the injected service
        AccountVm = new AccountViewModel(_accountService!, seriesService);

        DownloadsVm = new DownloadsViewModel(
            dbContextFactory,
            downloadQueue,
            App.Services.GetRequiredService<IDownloadBackendRouter>());
        // Populate immediately so the poll loop has rows to merge progress into
        DownloadsVm.LoadJobsCommand.Execute().Subscribe();
        DownloadHubVm = new DownloadHubViewModel(
            App.Services.GetRequiredService<INyaaSearchService>(),
            App.Services.GetRequiredService<IDownloadPickerService>(),
            dbContextFactory,
            App.Services.GetRequiredService<IDownloadBackendRouter>(),
            _themeService
        );

        // Wire up the Files tab — UnmatchedResolverVm is created eagerly here so the
        // FileManagementView can always show both sub-tabs without requiring the banner flow.
        var filesNormalizer = App.Services?.GetService(typeof(IEpisodeNormalizer)) as IEpisodeNormalizer;
        var filesPipeline   = App.Services?.GetService(typeof(IFileMovementPipeline)) as IFileMovementPipeline;
        if (DownloadsVm != null && filesNormalizer != null && filesPipeline != null)
        {
            UnmatchedResolverVm = new UnmatchedResolverViewModel(dbContextFactory, filesNormalizer, filesPipeline);
            FileManagementVm = new FileManagementViewModel(DownloadsVm, UnmatchedResolverVm);
        }

        SetCurrentView(AppView.Library);
        _ = InitializeAsync();

        // Silent startup scan (30s in, so the title resolver is warm): syncs
        // progress with what's actually on disk after out-of-app changes.
        _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(async _ =>
        {
            try { await RescanLibraryAsync(silent: true); }
            catch (Exception ex) { Console.WriteLine($"[LibraryScan] startup scan failed: {ex.Message}"); }
        });

        // Re-check airing statuses against MAL shortly after startup — a show that
        // finished airing since it was added moves to Completed on its own instead
        // of sitting in Currently Airing forever. Delayed so it never competes with
        // the startup burst (poster loads, RSS check) for Jikan's rate limit.
        _ = Task.Delay(TimeSpan.FromSeconds(45)).ContinueWith(async _ =>
        {
            try { await RefreshAiringStatusesAsync(); }
            catch { }
        });

        // Check followed manga for new chapters a bit later, so it never competes with
        // the airing-status pass for API bandwidth. Notifications fire per updated title.
        _ = Task.Delay(TimeSpan.FromSeconds(90)).ContinueWith(async _ =>
        {
            try { await CheckMangaUpdatesAsync(); }
            catch { }
        });
    }

    private async Task CheckMangaUpdatesAsync()
    {
        var svc = App.Services?.GetService(typeof(Sentrychan.Core.Services.MangaUpdateService))
            as Sentrychan.Core.Services.MangaUpdateService;
        if (svc == null) return;

        var report = await svc.CheckForUpdatesAsync(CancellationToken.None);
        if (report.WithNewChapters == 0) return;

        // New chapters on a followed manga are "important".
        foreach (var (title, message) in report.Updates)
            NotifyUser(title, message, important: true);

        // If the manga library is open, refresh it so new-chapter counts show.
        if (IsShowingManga) await (MangaLibraryVm?.LoadAsync() ?? Task.CompletedTask);
        if (IsShowingNovels) await (NovelLibraryVm?.LoadAsync() ?? Task.CompletedTask);
    }

    private async Task RefreshAiringStatusesAsync()
    {
        var refresher = App.Services?.GetService(typeof(Sentrychan.Core.Services.AiringStatusRefreshService))
            as Sentrychan.Core.Services.AiringStatusRefreshService;
        if (refresher == null) return;

        var report = await refresher.RefreshAsync(CancellationToken.None);
        if (report.Refreshed == 0 && report.AutoRemoved.Count == 0) return;

        await LoadSeriesAsync(); // re-reads the DB and rebuilds the section lists

        if (report.NowFinished > 0)
            ShowToast("Library updated",
                $"{report.NowFinished} series finished airing — moved to Completed");
        if (report.AutoRemoved.Count > 0)
            ShowToast("Auto-removed",
                $"{report.AutoRemoved.Count} completed series removed from library (files kept)");
    }

    /// <summary>
    /// Diffs the anime folder against the library. Progress advances apply
    /// automatically (cursor only ever rises); deleted/unknown folders open the
    /// decisions dialog. Silent mode (startup) applies advances but never
    /// opens UI — issues wait for a manual scan.
    /// </summary>
    private async Task RescanLibraryAsync(bool silent)
    {
        if (App.Services == null) return;
        var scanner = App.Services.GetService(typeof(Sentrychan.Core.Interfaces.ILibraryScanService))
            as Sentrychan.Core.Interfaces.ILibraryScanService;
        if (scanner == null || _apiService == null) return;

        if (!silent) ShowToast("Library scan", "Scanning your anime folder…");

        var report = await scanner.ScanAsync();
        if (report == null)
        {
            if (!silent) ShowToast("Library scan", "Library path not configured (Settings → General).");
            return;
        }

        int advanced = await scanner.ApplyProgressAdvancesAsync(report);
        if (advanced > 0)
        {
            await LoadSeriesAsync();
            ShowToast("Progress synced", $"{advanced} series advanced to match files on disk");
        }

        if (silent) return;

        if (report.HasIssues)
        {
            var vm = new LibraryScanViewModel(report, _seriesService, _apiService, this);
            var dialog = new Sentrychan.UI.Views.Dialogs.LibraryScanDialog { DataContext = vm };
            await dialog.ShowDialog(GetMainWindow()!);
            await LoadSeriesAsync();
        }
        else if (advanced == 0)
        {
            ShowToast("Library scan", "Everything in sync ✓");
        }
    }

    private async Task OpenAddFeedAsync()
    {
        if (_dbContextFactory == null) return;
        var vm = new AddFeedViewModel(_dbContextFactory);
        var dialog = new AddFeedDialog { DataContext = vm };
        await dialog.ShowDialog(GetMainWindow()!);
    }

    private async Task OpenSettingsAsync()
    {
        if (_dbContextFactory == null || _apiService == null || _seriesService == null) return;
        var vm = new SettingsViewModel(
            _dbContextFactory, _apiService, _seriesService,
            _themeService!, GetMainWindow()!,
            App.Services.GetRequiredService<IDownloadBackendRouter>(),
            _rssMonitor,
            App.Services.GetRequiredService<IDownloadFolderWatcher>());
        await vm.LoadAsync();
        var vm2 = vm; // for RssFeedsVm init after LoadAsync
        vm2.RssFeedsVm?.LoadFeedsCommand.Execute().Subscribe();
        var dialog = new SettingsDialog { DataContext = vm };
        await dialog.ShowDialog(GetMainWindow()!);

        // Propagate appearance settings that affect the main window
        BackgroundImagePath = vm.BackgroundImagePath;
    }

    private async Task OpenAccountDialogAsync()
    {
        if (AccountVm == null) return;
        var dialog = new Sentrychan.UI.Views.Dialogs.AccountDialog { DataContext = AccountVm };
        await dialog.ShowDialog(GetMainWindow()!);
    }

    private static Window? GetMainWindow() =>
        Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow : null;

    public async Task InitializeAsync()
    {
        await LoadSeriesAsync();
        await LoadAppearanceAsync();
        await CheckFirstRunAsync();
        await MaybeShowTutorialAsync();
        await SyncMonitoringStateAsync();
    }

    /// <summary>Reflect the real monitor state at startup so the button/label are honest.</summary>
    private async Task SyncMonitoringStateAsync()
    {
        try
        {
            var paused = false;
            if (_dbContextFactory != null)
            {
                await using var db = await _dbContextFactory.CreateDbContextAsync();
                paused = (await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == "MonitoringPaused"))?.Value == "true";
            }
            IsMonitoring = !paused;
            UpdateStatus(IsMonitoring ? "● Monitoring" : "● Idle", IsMonitoring ? "#00FF00" : "#808080");
        }
        catch { /* leave the default state */ }
    }

    /// <summary>
    /// First-run setup: if the download/library folders aren't configured yet,
    /// prompt for them so monitoring and organizing actually do something.
    /// </summary>
    /// <summary>Shows the skippable how-to tour once, after the folder setup on first launch.</summary>
    private async Task MaybeShowTutorialAsync()
    {
        if (_dbContextFactory == null) return;
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            var shown = (await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == "TutorialShown"))?.Value;
            if (shown == "true") return;

            var owner = GetMainWindow();
            if (owner == null) return;

            await new Sentrychan.UI.Views.Dialogs.TutorialDialog().ShowDialog(owner);

            var e = await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == "TutorialShown");
            if (e != null) e.Value = "true";
            else db.AppConfigs.Add(new Sentrychan.Core.Models.AppConfig { Key = "TutorialShown", Value = "true" });
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { Console.WriteLine($"[Tutorial] {ex.Message}"); }
    }

    /// <summary>Re-open the tour on demand (from Settings → About).</summary>
    public async Task ShowTutorialAsync()
    {
        var owner = GetMainWindow();
        if (owner == null) return;
        await new Sentrychan.UI.Views.Dialogs.TutorialDialog().ShowDialog(owner);
    }

    private async Task CheckFirstRunAsync()
    {
        if (_dbContextFactory == null) return;
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            async Task<string?> Get(string k) => (await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == k))?.Value;

            var download = await Get("DownloadPath");
            var library  = await Get("LibraryPath");

            // Both already set → nothing to do.
            if (!string.IsNullOrWhiteSpace(download) && !string.IsNullOrWhiteSpace(library)) return;

            // Only prompt once — a user who skipped shouldn't be nagged every launch.
            if (await Get("FirstRunPromptShown") == "true") return;

            var owner = GetMainWindow();
            if (owner == null) return;

            var dialog = new Sentrychan.UI.Views.Dialogs.FirstRunDialog();
            var result = await dialog.ShowDialog<(string Download, string Library)?>(owner);

            async Task Set(string k, string v)
            {
                var e = await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == k);
                if (e != null) e.Value = v;
                else db.AppConfigs.Add(new Sentrychan.Core.Models.AppConfig { Key = k, Value = v });
            }

            await Set("FirstRunPromptShown", "true");
            if (result is { } r)
            {
                if (!string.IsNullOrWhiteSpace(r.Download)) await Set("DownloadPath", r.Download);
                if (!string.IsNullOrWhiteSpace(r.Library)) await Set("LibraryPath", r.Library);
                ShowToast("Setup complete", "Folders saved — you're ready to add series.");
            }
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FirstRun] {ex.Message}");
        }
    }

    private async Task LoadAppearanceAsync()
    {
        if (_dbContextFactory == null) return;
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            var bgEntry = await db.AppConfigs
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Key == "BackgroundImagePath");
            BackgroundImagePath = bgEntry?.Value ?? string.Empty;

            var themeEntry = await db.AppConfigs
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Key == "SelectedTheme");
            var themeName = themeEntry?.Value;
            if (!string.IsNullOrEmpty(themeName))
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _themeService?.ApplyNamedTheme(themeName));

            var layout = (await db.AppConfigs.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Key == "LayoutMode"))?.Value;
            if (!string.IsNullOrEmpty(layout))
                Avalonia.Threading.Dispatcher.UIThread.Post(() => LayoutMode = layout);
        }
        catch { /* non-fatal */ }
    }

    /// <summary>
    /// Fires when Secret Mode is activated. Fetches AniDB cover URLs for every censored series
    /// that doesn't already have one, then raises PropertyChanged so SeriesCard picks them up.
    /// </summary>
    private async Task FetchAniDbCoversAsync(CancellationToken ct)
    {
        if (_aniDbCoverService == null) return;

        // Snapshot the list — avoid holding the collection across awaits
        var censored = CensoredList.Where(s => s.AniDbCoverUrl == null).ToList();
        foreach (var series in censored)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var url = await _aniDbCoverService.GetCoverUrlAsync(series.Title, ct);
                if (!string.IsNullOrEmpty(url))
                {
                    series.AniDbCoverUrl = url;
                    // Series doesn't implement INPC, so refresh the card by replacing
                    // the item in CensoredList — ObservableCollection fires CollectionChanged
                    // which causes the ItemsControl to re-create the SeriesCard.
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var idx = CensoredList.IndexOf(series);
                        if (idx >= 0)
                        {
                            CensoredList.RemoveAt(idx);
                            CensoredList.Insert(idx, series);
                        }
                    });
                }
            }
            catch { /* non-fatal per-series */ }
        }
    }

    public async Task LoadSeriesAsync()
    {
        if (_seriesService == null) return;
        IsLoading = true;
        try
        {
            var series = await _seriesService.GetAllAsync();

            // ALL collection mutations must happen on the UI thread. Callers include
            // background continuations (status refresh, silent library scan); letting
            // them Clear()/Add() off-thread interleaves with UI-thread rebuilds and
            // renders duplicated cards / half-updated sections.
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                SeriesList.Clear();
                ContinueWatchingList.Clear();
                _allSeries.Clear();
                foreach (var s in series)
                {
                    SeriesList.Add(s);
                    _allSeries.Add(s);
                }

                // Build the category lists honoring the current search filter.
                RebuildLibraryLists();

                // Populate Continue Watching: active series the user started but hasn't finished
                var continueWatching = series
                    .Where(s => s.LastEpisodeNumber > 0
                             && s.TotalEpisodes != null
                             && s.LastEpisodeNumber < s.TotalEpisodes
                             && s.MonitoringState == MonitoringState.Active)
                    .OrderByDescending(s => s.LastCheckedAt ?? DateTime.MinValue)
                    .Take(10);
                foreach (var s in continueWatching)
                    ContinueWatchingList.Add(s);
                HasContinueWatching = ContinueWatchingList.Count > 0;

                IsLibraryEmpty = !SeriesList.Any();
                this.RaisePropertyChanged(nameof(HasCensored));
            });
        }
        finally { IsLoading = false; }

        // Refresh the "Airing Today" panel against the new library (background)
        _ = LoadAiringTodayAsync();
    }

    /// <summary>
    /// Rebuilds the Airing/Recent/Completed/Censored sections and the fallback grid
    /// from <see cref="_allSeries"/>, applying the current <see cref="LibrarySearchText"/>.
    /// </summary>
    private enum LibraryBucket { Censored, Recent, Airing, Completed, Other }

    /// <summary>
    /// Decides the one section a series belongs to. Returns Other rather than nothing
    /// for unrecognised statuses, so a series can never silently disappear from the
    /// library the way null / "Not yet aired" / junk statuses used to.
    /// </summary>
    private static LibraryBucket Classify(Series s)
    {
        if (s.IsCensored) return LibraryBucket.Censored;
        if ((DateTime.UtcNow - s.AddedAt).TotalDays <= 7) return LibraryBucket.Recent;

        var status = (s.AiringStatus ?? string.Empty).Trim();
        if (IsAiringStatus(status))   return LibraryBucket.Airing;
        if (IsFinishedStatus(status)) return LibraryBucket.Completed;

        // null, "Not yet aired", or anything unexpected.
        return LibraryBucket.Other;
    }

    private static bool IsAiringStatus(string s) =>
        s.Equals("Airing", StringComparison.OrdinalIgnoreCase)
        || s.Equals("Currently Airing", StringComparison.OrdinalIgnoreCase)
        || s.Equals("Ongoing", StringComparison.OrdinalIgnoreCase);

    private static bool IsFinishedStatus(string s) =>
        s.Equals("Finished Airing", StringComparison.OrdinalIgnoreCase)
        || s.Equals("Finished", StringComparison.OrdinalIgnoreCase)
        || s.Equals("Completed", StringComparison.OrdinalIgnoreCase);

    private void RebuildLibraryLists()
    {
        var q = (_librarySearchText ?? string.Empty).Trim();

        bool Matches(Series s)
        {
            if (q.Length == 0) return true;
            return (s.Title?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (s.OriginalTitle?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        AiringList.Clear();
        CompletedList.Clear();
        RecentList.Clear();
        CensoredList.Clear();
        OtherList.Clear();
        FilteredSeriesList.Clear();

        foreach (var s in _allSeries)
        {
            if (!Matches(s)) continue;
            FilteredSeriesList.Add(s);

            // Exclusive buckets — each series appears in exactly one section (no
            // duplication), and CRUCIALLY every series lands in exactly one. The old
            // chain ended at "Finished Airing" with no else, so anything with a null,
            // unexpected, or junk AiringStatus was counted but never rendered.
            switch (Classify(s))
            {
                case LibraryBucket.Censored:  CensoredList.Add(s);  break;
                case LibraryBucket.Recent:    RecentList.Add(s);    break;
                case LibraryBucket.Airing:    AiringList.Add(s);    break;
                case LibraryBucket.Completed: CompletedList.Add(s); break;
                default:                      OtherList.Add(s);     break;
            }
        }

        this.RaisePropertyChanged(nameof(HasAiring));
        this.RaisePropertyChanged(nameof(HasCompleted));
        this.RaisePropertyChanged(nameof(HasRecent));
        this.RaisePropertyChanged(nameof(HasCensored));
        this.RaisePropertyChanged(nameof(HasOther));
        this.RaisePropertyChanged(nameof(ShowAllSeriesFallback));
        this.RaisePropertyChanged(nameof(CurrentPageCountText));
        RaiseSectionVisibility();
    }

    public void AddSeriesToLibrary(Series series)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (SeriesList.Any(s => s.MalId == series.MalId)) return;
            SeriesList.Add(series);
            _allSeries.Add(series);

            // Re-derive the category/fallback lists (honors the active search filter).
            RebuildLibraryLists();

            IsLibraryEmpty = !SeriesList.Any();
            ShowToast("Series added", series.Title);

            // Sweep _Unmatched — the new series may claim files waiting there
            // (reload recomputes suggestions and auto-assigns confirmed matches).
            UnmatchedResolverVm?.LoadCommand.Execute().Subscribe();
        });
    }

    private async Task ToggleMonitoringAsync()
    {
        if (_rssMonitor == null) return;
        if (IsMonitoring)
        {
            _rssMonitor.Pause();               // actually halt the background checks
            IsMonitoring = false;
            UpdateStatus("● Idle", "#808080");
        }
        else
        {
            _rssMonitor.Resume();
            IsMonitoring = true;
            UpdateStatus("● Monitoring", "#00FF00");
            await _rssMonitor.ManualCheckAsync();
        }
    }

    private async Task OpenAddSeriesAsync()
    {
        if (_apiService == null || _seriesService == null) return;
        var vm = new AddSeriesViewModel(_apiService, _seriesService);
        var dialog = new AddSeriesDialog { DataContext = vm };
        await dialog.ShowDialog(GetMainWindow()!);
        if (vm.AddedSeries != null)
        {
            await LoadSeriesAsync();
            // Sweep _Unmatched for files belonging to the new series
            UnmatchedResolverVm?.LoadCommand.Execute().Subscribe();
        }
    }

    private async Task ManualCheckAsync()
    {
        if (_rssMonitor == null) return;
        UpdateStatus("● Checking...", "#FFA726");
        await _rssMonitor.ManualCheckAsync();
    }

    private async Task LookForEpisodeAsync(Series series)
    {
        if (series == null || App.Services == null) return;

        int targetEpisode = 0;
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var dialog = new EpisodePickerDialog();
            var epInput = dialog.FindControl<Avalonia.Controls.NumericUpDown>("EpisodeInput");
            if (epInput != null) epInput.Value = series.LastEpisodeNumber + 1;
            var result = await dialog.ShowDialog<int>(desktop.MainWindow);
            if (result == -1) return; // Cancelled
            targetEpisode = result;
        }

        // Targeted Nyaa search for this exact episode. The old approach (roll back
        // LastEpisodeNumber and re-run the RSS check) only worked when the episode
        // happened to still be inside the feed's ~75-item window.
        UpdateStatus($"● Looking for Ep {targetEpisode}...", "#FFA726");
        ShowToast("Searching", $"Looking for {series.Title} · Episode {targetEpisode}");

        try
        {
            var nyaa = App.Services.GetRequiredService<INyaaSearchService>();

            // Try the primary title, then original/alternative titles — release
            // groups often use a different title than MAL's romaji. Strip the season
            // out of each query title so every season's releases come back, then keep
            // only those matching THIS series' season (otherwise Season 2 grabs S1).
            var titlesToTry = new List<string> { series.Title };
            if (!string.IsNullOrEmpty(series.OriginalTitle) && series.OriginalTitle != series.Title)
                titlesToTry.Add(series.OriginalTitle);
            if (!string.IsNullOrEmpty(series.AlternativeTitlesJson))
            {
                try
                {
                    var alts = System.Text.Json.JsonSerializer
                        .Deserialize<List<string>>(series.AlternativeTitlesJson);
                    if (alts != null)
                        titlesToTry.AddRange(alts.Where(t =>
                            !string.IsNullOrWhiteSpace(t) && !titlesToTry.Contains(t)));
                }
                catch { /* malformed alt titles — ignore */ }
            }

            var season = Sentrychan.Core.Services.SeasonSearch.EffectiveSeason(series.Title, series.SeasonNumber);
            Sentrychan.Core.Models.NyaaResult? best = null;
            foreach (var title in titlesToTry)
            {
                var searchTitle = Sentrychan.Core.Services.SeasonSearch.StripSeason(title);
                var results = await nyaa.FindEpisodeAsync(searchTitle, targetEpisode, null, CancellationToken.None);
                best = results.FirstOrDefault(r =>
                    Sentrychan.Core.Services.SeasonSearch.MatchesSeason(r.Title, season));
                if (best != null) break;
            }

            if (best == null)
            {
                ShowToast("Not found",
                    $"No release found for {series.Title} · Episode {targetEpisode}");
                return;
            }

            var link = !string.IsNullOrEmpty(best.MagnetLink) ? best.MagnetLink : best.TorrentUrl;

            // Route through the New Episodes inbox so the user confirms before download.
            await Handle(new DownloadConfirmationEvent(
                SeriesId:      series.Id,
                MalId:         series.MalId,
                SeriesTitle:   series.Title,
                PosterPath:    series.PosterPath ?? string.Empty,
                EpisodeNumber: targetEpisode,
                ReleaseGroup:  best.ReleaseGroup,
                Resolution:    best.Resolution,
                SizeDisplay:   best.SizeDisplay,
                Seeders:       best.Seeders,
                DownloadLink:  link,
                RssTitle:      best.Title), CancellationToken.None);

            ShowToast("Found", $"{series.Title} · Episode {targetEpisode} — check the inbox");
        }
        catch (Exception ex)
        {
            ShowToast("Search failed", ex.Message);
        }
        finally
        {
            UpdateStatus(IsMonitoring ? "● Monitoring" : "● Idle", IsMonitoring ? "#00FF00" : "#808080");
        }
    }

    /// <summary>
    /// Batch download: finds every episode of a series that isn't on disk and queues
    /// them all. Prefers a single batch torrent when one exists (offered as the first,
    /// pre-selected row of the review dialog); falls back to per-episode releases.
    /// </summary>
    private async Task DownloadAllEpisodesAsync(Series series)
    {
        if (series == null || App.Services == null || _downloadQueue == null) return;

        var fillGaps = App.Services.GetRequiredService<IFillGapsService>();
        ShowToast("Batch download", $"Scanning {series.Title} for missing episodes…");
        UpdateStatus("● Scanning gaps...", "#FFA726");

        try
        {
            // Run both searches: a batch torrent (one download for everything) and
            // the per-episode gap scan. Both go into one review dialog.
            var batch = await fillGaps.SearchBatchTorrentAsync(series);
            var gaps  = await fillGaps.FindMissingEpisodesAsync(series);

            if (batch == null && gaps.Count == 0)
            {
                ShowToast("Nothing to download", $"{series.Title} — no missing episodes found.");
                return;
            }

            var rows = new List<Sentrychan.Core.Interfaces.FillGapResult>();
            if (batch != null)
            {
                // Batch first, pre-selected. Per-episode rows start deselected so the
                // user doesn't accidentally double-download everything.
                rows.Add(new Sentrychan.Core.Interfaces.FillGapResult
                {
                    EpisodeNumber = 0, // 0 = batch sentinel; shows as "BATCH" in the dialog
                    BestMatch     = batch,
                    IsSelected    = true
                });
                foreach (var g in gaps) g.IsSelected = false;
            }
            rows.AddRange(gaps);

            var vm = new FillGapsViewModel(rows);
            var dialog = new Sentrychan.UI.Views.Dialogs.FillGapsDialog { DataContext = vm };
            var confirmed = await dialog.ShowDialog<List<Sentrychan.Core.Interfaces.FillGapResult>?>(GetMainWindow()!);
            if (confirmed == null) return;

            int queued = 0;
            foreach (var pick in confirmed.Where(r => r.IsSelected && r.BestMatch != null))
            {
                var link = !string.IsNullOrEmpty(pick.BestMatch!.MagnetLink)
                    ? pick.BestMatch.MagnetLink
                    : pick.BestMatch.TorrentUrl;
                if (string.IsNullOrEmpty(link)) continue;

                await _downloadQueue.EnqueueAsync(
                    link, series.Id, pick.EpisodeNumber, series.Title,
                    pick.BestMatch.Title, CancellationToken.None);
                queued++;
            }

            if (queued > 0)
                ShowToast("Batch download started",
                    $"{series.Title} · {queued} download(s) queued");
        }
        catch (Exception ex)
        {
            ShowToast("Batch download failed", ex.Message);
        }
        finally
        {
            UpdateStatus(IsMonitoring ? "● Monitoring" : "● Idle", IsMonitoring ? "#00FF00" : "#808080");
        }
    }

    private async Task RemoveSeriesAsync(Series series)
    {
        if (_seriesService == null || series == null) return;
        bool success = await _seriesService.RemoveAsync(series.MalId);
        if (success)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                SeriesList.Remove(series);
                _allSeries.RemoveAll(s => s.MalId == series.MalId);

                RebuildLibraryLists();
                IsLibraryEmpty = !SeriesList.Any();
                ShowToast("Series removed", series.Title);
            });
        }
    }

    public Task Handle(NewEpisodeFoundEvent notification, CancellationToken ct)
    {
        if (!notification.IsSecondary) return Task.CompletedTask;
        if (!_uiReady) return Task.CompletedTask;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            PendingEpisodes.Add(notification);
            PendingCount = PendingEpisodes.Count;
            if (!notification.IsSilent) NotifyUser("New Episode Found", $"{notification.SeriesTitle} - Episode {notification.EpisodeNumber}", important: false);
        });
        return Task.CompletedTask;
    }

    public Task Handle(MonitorStatusEvent notification, CancellationToken ct)
    {
        if (!_uiReady) return Task.CompletedTask;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            switch (notification.Status)
            {
                case MonitorStatus.CheckStarted: UpdateStatus("● Checking...", "#FFA726"); break;
                case MonitorStatus.CheckCompleted:
                    var now = DateTime.Now;
                    LastCheckText = $"Last check: {now:HH:mm} ({GetRelativeTime(now)})";
                    UpdateStatus(notification.NewEpisodesCount > 0 || notification.PendingCount > 0 ? $"● Found: {notification.NewEpisodesCount} new, {notification.PendingCount} pending" : IsMonitoring ? "● Monitoring" : "● Idle", notification.NewEpisodesCount > 0 || notification.PendingCount > 0 || IsMonitoring ? "#00FF00" : "#808080");
                    if (notification.NewEpisodesCount + notification.PendingCount > 1) NotifyUser("Check Completed", $"Found {notification.NewEpisodesCount} new and {notification.PendingCount} pending episodes.", important: false);
                    _ = LoadSeriesAsync();
                    break;
                case MonitorStatus.Error:
                    UpdateStatus("● Error", "#F44336");
                    ShowToast("Monitor error", notification.ErrorMessage ?? "A feed check failed.");
                    break;
            }
        });
        return Task.CompletedTask;
    }

    public Task Handle(NewFileArrivedEvent notification, CancellationToken ct)
    {
        if (!_uiReady) return Task.CompletedTask;
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            // Refresh the library list so the series card shows the updated episode count
            await LoadSeriesAsync();

            // Keep the Downloads tab in sync with the just-completed job
            DownloadsVm?.LoadJobsCommand.Execute().Subscribe();

            if (notification.SeriesId == 0)
            {
                // Standalone download — not tracked in the library
                NotifyUser("Download complete", $"{notification.SeriesTitle} → _Standalone", important: true);
            }
            else
            {
                NotifyUser("Episode added", $"{notification.SeriesTitle} · Episode {notification.EpisodeNumber}", important: true);
            }

            // If the currently open series detail is for this series, refresh it too
            if (SeriesDetailVm?.Series.Id == notification.SeriesId)
            {
                await SeriesDetailVm.InitializeAsync();
            }
        });
        return Task.CompletedTask;
    }

    public Task Handle(UndoableEpisodeUpdateEvent notification, CancellationToken ct)
    {
        if (!_uiReady) return Task.CompletedTask;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _pendingUndo = notification;

            // Build the undo command — stored so the toast can reference it
            var undoCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                if (_pendingUndo == null) return;
                if (DateTime.UtcNow > _pendingUndo.ExpiresAt)
                {
                    _pendingUndo = null;
                    return;
                }
                // Revert the LastEpisodeNumber
                if (_seriesService != null)
                {
                    await _seriesService.UpdateLastEpisodeAsync(
                        _pendingUndo.MalId,
                        _pendingUndo.PreviousEpisodeNumber);
                    await LoadSeriesAsync();
                }
                _pendingUndo = null;
            });

            // Post activity to cloud feed (fire-and-forget, non-fatal)
            if (_accountService != null)
                _ = _accountService.PostActivityAsync(
                    "watched_episode",
                    notification.SeriesTitle,
                    notification.MalId,
                    notification.NewEpisodeNumber);

            _undoToastRequest.OnNext((
                $"Updated: {notification.SeriesTitle}",
                $"Advanced to Episode {notification.NewEpisodeNumber}",
                "Undo",
                undoCommand));
        });
        return Task.CompletedTask;
    }

    public Task Handle(UnmatchedFileEvent notification, CancellationToken ct)
    {
        if (!_uiReady) return Task.CompletedTask;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            UnmatchedFolderPath  = notification.UnmatchedFolderPath;
            UnmatchedBannerText  = $"\"{notification.FileName}\" wasn't matched — moved to _Unmatched";
            ShowUnmatchedBanner  = true;
            NotifyUser("Unmatched file", $"\"{notification.FileName}\" — resolve it in Files → Unmatched", important: false);
        });
        return Task.CompletedTask;
    }

    public Task Handle(MultiSourceEpisodeEvent notification, CancellationToken ct)
    {
        if (!_uiReady) return Task.CompletedTask;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var pick = new SourcePickVm(notification);

            // When user picks a source, enqueue the download and remove the card
            pick.SourceChosen += async candidate =>
            {
                if (_downloadQueue == null || _dbContextFactory == null) return;

                string seriesTitle = notification.SeriesTitle;

                try
                {
                    // Resolve MalId → SeriesId so the job carries the right series.
                    int seriesId = 0;
                    await using (var db = await _dbContextFactory.CreateDbContextAsync(CancellationToken.None))
                    {
                        var s = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                            .FirstOrDefaultAsync(db.Series, x => x.MalId == notification.MalId);
                        if (s != null) seriesId = s.Id;
                    }

                    await _downloadQueue.EnqueueAsync(
                        candidate.DownloadLink,
                        seriesId,
                        notification.EpisodeNumber,
                        seriesTitle,
                        candidate.RssTitle,
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"[SourcePick] Download enqueue failed: {ex.Message}");
                }

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    SourcePicks.Remove(pick);
                    HasSourcePicks = SourcePicks.Count > 0;
                });
            };

            pick.Dismissed += () =>
            {
                SourcePicks.Remove(pick);
                HasSourcePicks = SourcePicks.Count > 0;
            };

            SourcePicks.Add(pick);
            HasSourcePicks = true;
        });

        return Task.CompletedTask;
    }

    private void UpdateStatus(string text, string color) { StatusText = text; StatusColor = color; }

    private async Task OpenPosterOptionsAsync(Series series)
    {
        var files = await GetMainWindow()!.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions { Title = $"Select Poster for {series.Title}", AllowMultiple = false, FileTypeFilter = new[] { Avalonia.Platform.Storage.FilePickerFileTypes.ImageAll } });
        if (files != null && files.Count > 0)
        {
            var coversDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sentrychan", "Covers");
            Directory.CreateDirectory(coversDir);
            var destPath = Path.Combine(coversDir, $"{series.Id}_{DateTime.Now.Ticks}{Path.GetExtension(files[0].Path.LocalPath)}");
            File.Copy(files[0].Path.LocalPath, destPath, true);
            series.PosterPath = destPath;
            await _seriesService.UpdatePosterPathAsync(series.Id, destPath);
            await LoadSeriesAsync();
        }
    }

    private async Task DownloadPosterAsync(Series series)
    {
        try
        {
            var details = await _apiService!.GetAnimeByIdAsync(series.MalId);
            if (details != null && !string.IsNullOrEmpty(details.LargeImageUrl))
            {
                series.PosterPath = details.LargeImageUrl;
                await _seriesService.UpdatePosterPathAsync(series.Id, details.LargeImageUrl);
                await LoadSeriesAsync();
            }
        }
        catch { }
    }

    public void SetCurrentView(AppView view)
    {
        IsShowingLibrary = view == AppView.Library;
        IsShowingSeasonal = view == AppView.Seasonal;
        IsShowingDownloads = view == AppView.Downloads;
        IsShowingDownloadHub = view == AppView.DownloadHub;
        IsShowingSeriesDetail = view == AppView.SeriesDetail;
        IsShowingWatchParty = view == AppView.WatchParty;
        IsShowingFiles = view == AppView.Files;
        IsShowingLatest = view == AppView.Latest;
        IsShowingNews = view == AppView.News;
        IsShowingManga = view == AppView.Manga;
        IsShowingNovels = view == AppView.Novels;
        IsShowingQuiz = view == AppView.Quiz;
        IsShowingBattle = view == AppView.Battle;
        IsShowingMangaDetail = view == AppView.MangaDetail;
        IsShowingMangaReader = view == AppView.MangaReader;
        // These keep playing audio while merely hidden — stop them when navigating away.
        if (view != AppView.Quiz) AnimeQuizVm?.StopPlayback();
        if (view != AppView.Battle) BattleRoyaleVm?.StopPlayback();
        IsShowingUnmatchedResolver = false;
    }

    /// <summary>
    /// Opens the reader for a manga at the given chapter (by DB id). Called from the
    /// detail view's chapter rows and the Continue button.
    /// </summary>
    public async Task OpenReaderAsync(Manga manga, int chapterId)
    {
        if (App.Services == null) return;
        var mangaService = App.Services.GetService(typeof(IMangaService)) as IMangaService;
        var registry = App.Services.GetService(typeof(IMangaSourceRegistry)) as IMangaSourceRegistry;
        if (mangaService == null || registry == null) return;
        var source = registry.Get(manga.Source);

        // Load the manga's chapters in ascending order so the reader can flow between them.
        var full = await mangaService.GetByIdAsync(manga.Id);
        if (full == null) return;
        var ordered = full.Chapters.OrderBy(c => c.ChapterSort ?? double.MaxValue).ToList();
        var startIndex = ordered.FindIndex(c => c.Id == chapterId);
        if (startIndex < 0) startIndex = 0;

        var config = App.Services.GetService(typeof(IConfigService)) as IConfigService;
        var webtoon = config != null && await config.GetValueAsync(MangaReaderViewModel.ReaderModeKey, false);
        var novelFont = config != null ? await config.GetValueAsync(MangaReaderViewModel.NovelFontKey, 17.0) : 17.0;

        MangaReaderVm = new MangaReaderViewModel(full, ordered, startIndex, mangaService, source,
            onClose: () => _ = ReturnFromReaderAsync(full),
            startLongStrip: webtoon, config: config, novelFontSize: novelFont);
        SetCurrentView(AppView.MangaReader);
        await MangaReaderVm.InitializeAsync();
    }

    private async Task ReturnFromReaderAsync(Manga manga)
    {
        // Re-open the detail view so freshly-read chapters and progress show.
        await OpenMangaDetailAsync(manga);
    }

    private void ShowManga()
    {
        if (MangaLibraryVm == null && App.Services != null)
        {
            var mangaService = App.Services.GetService(typeof(IMangaService)) as IMangaService;
            var registry = App.Services.GetService(typeof(IMangaSourceRegistry)) as IMangaSourceRegistry;
            if (mangaService == null || registry == null) return;
            var secret = App.Services.GetService(typeof(Sentrychan.Core.Interfaces.ISecretModeService))
                as Sentrychan.Core.Interfaces.ISecretModeService;
            MangaLibraryVm = new MangaLibraryViewModel(mangaService, registry, secret,
                m => _ = OpenMangaDetailAsync(m));
            // Let a search/browse result be read without adding it to the library.
            MangaLibraryVm.PreviewReadHandler = (result, source) => OpenPreviewReaderAsync(result, source);
        }
        SetCurrentView(AppView.Manga);
        _ = MangaLibraryVm?.LoadAsync();
    }

    private void ShowNovels()
    {
        if (NovelLibraryVm == null && App.Services != null)
        {
            var mangaService = App.Services.GetService(typeof(IMangaService)) as IMangaService;
            var registry = App.Services.GetService(typeof(IMangaSourceRegistry)) as IMangaSourceRegistry;
            if (mangaService == null || registry == null) return;
            var secret = App.Services.GetService(typeof(Sentrychan.Core.Interfaces.ISecretModeService))
                as Sentrychan.Core.Interfaces.ISecretModeService;
            NovelLibraryVm = new MangaLibraryViewModel(mangaService, registry, secret,
                m => _ = OpenMangaDetailAsync(m), novelMode: true);
            NovelLibraryVm.PreviewReadHandler = (result, source) => OpenPreviewReaderAsync(result, source);
        }
        SetCurrentView(AppView.Novels);
        _ = NovelLibraryVm?.LoadAsync();
    }

    private void ShowQuiz()
    {
        if (AnimeQuizVm == null && App.Services != null
            && App.Services.GetService(typeof(IAnimeQuizService)) is IAnimeQuizService quiz)
        {
            AnimeQuizVm = new AnimeQuizViewModel(quiz) { OnOpenBattleRoyale = ShowBattle };
            // Opens on the setup screen — no network work on nav, so clicking Quiz is instant.
        }
        SetCurrentView(AppView.Quiz);
    }

    private void ShowBattle()
    {
        if (BattleRoyaleVm == null && App.Services != null
            && App.Services.GetService(typeof(IAnimeQuizService)) is IAnimeQuizService quiz)
        {
            BattleRoyaleVm = new BattleRoyaleViewModel(quiz) { OnOpenQuiz = ShowQuiz };
        }
        SetCurrentView(AppView.Battle);
    }

    /// <summary>
    /// Opens the reader for a search/browse result WITHOUT adding it to the library.
    /// Chapters are fetched straight from the source; nothing is persisted. The reader
    /// shows a reminder offering to add it, which then starts tracking progress.
    /// </summary>
    public async Task OpenPreviewReaderAsync(MangaResultVm resultVm, IMangaSourceService source)
    {
        if (App.Services == null) return;
        if (App.Services.GetService(typeof(IMangaService)) is not IMangaService mangaService) return;

        var r = resultVm.Result;
        var manga = new Manga
        {
            Source                = source.SourceName,
            SourceId              = r.SourceId,
            Title                 = r.Title,
            OriginalTitle         = r.OriginalTitle,
            AlternativeTitlesJson = System.Text.Json.JsonSerializer.Serialize(r.AltTitles),
            Description           = r.Description,
            CoverPath             = r.CoverUrl,
            Status                = r.Status,
            Year                  = r.Year,
            TotalChapters         = r.LastChapter,
            IsCensored            = r.IsAdult,
            IsNovel               = source.IsNovel
        };

        System.Collections.Generic.List<Sentrychan.Core.Interfaces.MangaChapterInfo> infos;
        try { infos = await source.GetChaptersAsync(r.SourceId, "en"); }
        catch { infos = []; }

        var ordered = infos.OrderBy(i => i.ChapterSort ?? double.MaxValue).ToList();
        var chapters = ordered.Select(i => new MangaChapter
        {
            SourceId        = i.SourceId,
            ChapterNumber   = i.ChapterNumber,
            ChapterSort     = i.ChapterSort,
            Volume          = i.Volume,
            Title           = i.Title,
            Language        = i.Language,
            ScanlationGroup = i.ScanlationGroup,
            Pages           = i.Pages,
            PublishedAt     = i.PublishedAt
        }).ToList();

        var config = App.Services.GetService(typeof(IConfigService)) as IConfigService;
        var webtoon = config != null && await config.GetValueAsync(MangaReaderViewModel.ReaderModeKey, false);
        var novelFont = config != null ? await config.GetValueAsync(MangaReaderViewModel.NovelFontKey, 17.0) : 17.0;

        MangaReaderVm = new MangaReaderViewModel(
            manga, chapters, 0, mangaService, source,
            onClose: source.IsNovel ? ShowNovels : ShowManga,
            isPreview: !resultVm.IsInLibrary,
            previewInfos: ordered,
            onAdded: () =>
            {
                resultVm.IsInLibrary = true;
                if (source.IsNovel) _ = NovelLibraryVm?.LoadAsync();
                else _ = MangaLibraryVm?.LoadAsync();
            },
            startLongStrip: webtoon, config: config, novelFontSize: novelFont);
        SetCurrentView(AppView.MangaReader);
        await MangaReaderVm.InitializeAsync();
    }

    private async Task OpenMangaDetailAsync(Manga manga)
    {
        if (App.Services == null) return;
        var mangaService = App.Services.GetService(typeof(IMangaService)) as IMangaService;
        var registry = App.Services.GetService(typeof(IMangaSourceRegistry)) as IMangaSourceRegistry;
        var downloader = App.Services.GetService(typeof(IMangaDownloadService)) as IMangaDownloadService;
        if (mangaService == null || registry == null || downloader == null) return;

        var source = registry.Get(manga.Source); // route to the manga's own source

        // Back returns to whichever section this title lives in (Manga vs Novels).
        Action back = manga.IsNovel ? ShowNovels : ShowManga;
        MangaDetailVm = new MangaDetailViewModel(manga, mangaService, source, downloader, back,
            chapterId => _ = OpenReaderAsync(manga, chapterId));
        SetCurrentView(AppView.MangaDetail);
        await MangaDetailVm.InitializeAsync();
    }

    public void ShowWatchParty()
    {
        if (WatchPartyVm == null)
        {
            var services = App.Services;
            if (services == null) return;
            WatchPartyVm = new WatchPartyViewModel(
                services.GetRequiredService<Sentrychan.Core.Interfaces.IWatchPartyClientService>(),
                services.GetRequiredService<Sentrychan.Core.Interfaces.IWatchPartyHostService>(),
                services.GetRequiredService<Sentrychan.Core.Interfaces.IPlayerBridgeService>(),
                services.GetRequiredService<Sentrychan.Core.Interfaces.ISyncEngine>(),
                services.GetRequiredService<Sentrychan.UI.Interfaces.IReactionOverlayService>(),
                services.GetRequiredService<Sentrychan.Core.Interfaces.IVoiceChatService>(),
                services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Sentrychan.Core.Data.AppDbContext>>(),
                services.GetRequiredService<Sentrychan.Core.Interfaces.ILanDiscoveryService>());
        }
        SetCurrentView(AppView.WatchParty);
    }

    public async Task OpenSeriesDetailsAsync(Series series)
    {
        var locator = App.Services.GetRequiredService<IVideoFileLocator>();
        SeriesDetailVm = new SeriesDetailViewModel(series, _seriesService, _apiService!, _dbContextFactory!, _titleAliasService!, _rssMonitor, locator);
        SetCurrentView(AppView.SeriesDetail);
        await SeriesDetailVm.InitializeAsync();
    }

    /// <summary>
    /// Synchronously navigates to the series detail view (without awaiting initialization).
    /// Used by DownloadAllMissingCommand to navigate before triggering the download.
    /// </summary>
    private void OpenSeriesDetails(Series series)
    {
        if (App.Services == null) return;
        var locator = App.Services.GetRequiredService<IVideoFileLocator>();
        SeriesDetailVm = new SeriesDetailViewModel(series, _seriesService, _apiService!, _dbContextFactory!, _titleAliasService!, _rssMonitor, locator);
        SetCurrentView(AppView.SeriesDetail);
        _ = SeriesDetailVm.InitializeAsync();
    }

    private void ShowNews()
    {
        NewsVm ??= new NewsViewModel();
        SetCurrentView(AppView.News);
        _ = NewsVm.InitializeAsync();
    }

    private void ShowLatest()
    {
        if (LatestArrivalsVm == null && _apiService != null && _dbContextFactory != null)
        {
            var normalizer = App.Services?.GetService(typeof(IEpisodeNormalizer)) as IEpisodeNormalizer;
            if (normalizer == null || _themeService == null) return;

            LatestArrivalsVm = new LatestArrivalsViewModel(
                _dbContextFactory, _apiService, _seriesService, normalizer,
                this, _themeService, async anime =>
                {
                    var series = new Series
                    {
                        MalId = anime.MalId,
                        Title = anime.Title,
                        PosterPath = anime.LargeImageUrl,
                        TotalEpisodes = anime.Episodes,
                        AiringStatus = Sentrychan.Core.Services.AiringStatusNormalizer.Normalize(anime.Status)
                    };
                    await OpenSeriesDetailsAsync(series);
                });
        }
        SetCurrentView(AppView.Latest);
        _ = LatestArrivalsVm?.InitializeAsync();
    }

    private void ShowSeasonal()
    {
        if (SeasonalVm == null && _apiService != null && _dbContextFactory != null)
        {
            var picker = App.Services?.GetService(typeof(IDownloadPickerService)) as IDownloadPickerService;
            SeasonalVm = new SeasonalViewModel(_apiService, _seriesService, this, async anime =>
            {
                var series = new Series { MalId = anime.MalId, Title = anime.Title, PosterPath = anime.LargeImageUrl, TotalEpisodes = anime.Episodes, AiringStatus = Sentrychan.Core.Services.AiringStatusNormalizer.Normalize(anime.Status) };
                await OpenSeriesDetailsAsync(series);
            });
        }
        SeasonalVm?.LoadCommand.Execute().Subscribe();
        SetCurrentView(AppView.Seasonal);
    }

    private static string GetRelativeTime(DateTime dt)
    {
        var diff = DateTime.Now - dt;
        if (diff.TotalMinutes < 1) return "just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        return $"{(int)diff.TotalHours}h ago";
    }

    public Task Handle(DownloadConfirmationEvent notification, CancellationToken ct)
    {
        if (!_uiReady) return Task.CompletedTask;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Dedupe — the monitor can surface the same episode across checks;
            // don't stack identical cards in the inbox.
            if (PendingConfirms.Any(c => c.SeriesId == notification.SeriesId
                                      && c.EpisodeNumber == notification.EpisodeNumber))
                return;

            var vm = new DownloadConfirmVm(notification);

            vm.DownloadRequested += async card =>
            {
                // Remove immediately so the card disappears on click, regardless
                // of how the async enqueue goes.
                RemovePendingConfirm(card);
                if (_downloadQueue == null) return;
                try
                {
                    await _downloadQueue.EnqueueAsync(
                        card.DownloadLink,
                        card.SeriesId,
                        card.EpisodeNumber,
                        card.SeriesTitle,
                        card.RssTitle,
                        CancellationToken.None);
                    // Advance the cursor so the monitor won't re-offer this episode.
                    await AdvanceSeriesCursorAsync(card.SeriesId, card.EpisodeNumber);
                    ShowToast("Download started", $"{card.SeriesTitle} · Episode {card.EpisodeNumber}");
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"[DownloadConfirm] Download enqueue failed: {ex.Message}");
                }
            };

            vm.SkipRequested += async (card, isPermanent) =>
            {
                RemovePendingConfirm(card);
                if (_dbContextFactory == null) return;
                try
                {
                    await using var db = await _dbContextFactory.CreateDbContextAsync(CancellationToken.None);
                    db.SkippedDownloads.Add(new Sentrychan.Core.Models.SkippedDownload
                    {
                        SeriesId      = card.SeriesId,
                        EpisodeNumber = card.EpisodeNumber,
                        IsPermanent   = isPermanent,
                        SkippedAt     = DateTime.UtcNow,
                    });
                    await db.SaveChangesAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"[DownloadConfirm] Skip save failed: {ex.Message}");
                }
            };

            vm.AutoDownloadRequested += async card =>
            {
                // Remove first so the card always disappears on click.
                RemovePendingConfirm(card);

                // Also drop any OTHER pending cards for this series — the user
                // just said "always download this show", so no more asking.
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    foreach (var other in PendingConfirms.Where(c => c.SeriesId == card.SeriesId).ToList())
                        PendingConfirms.Remove(other);
                    this.RaisePropertyChanged(nameof(HasPendingConfirms));
                });

                if (_dbContextFactory != null)
                {
                    try
                    {
                        await using var db = await _dbContextFactory.CreateDbContextAsync(CancellationToken.None);
                        var series = await db.Series.FindAsync(new object[] { card.SeriesId }, CancellationToken.None);
                        if (series != null)
                        {
                            series.AutoDownload = true;
                            if (card.EpisodeNumber > series.LastEpisodeNumber)
                                series.LastEpisodeNumber = card.EpisodeNumber;
                            await db.SaveChangesAsync(CancellationToken.None);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Console.WriteLine($"[DownloadConfirm] AutoDownload update failed: {ex.Message}");
                    }
                }

                if (_downloadQueue != null)
                {
                    try
                    {
                        await _downloadQueue.EnqueueAsync(
                            card.DownloadLink, card.SeriesId, card.EpisodeNumber,
                            card.SeriesTitle, card.RssTitle, CancellationToken.None);
                        ShowToast("Auto-download on", $"{card.SeriesTitle} — future episodes download automatically");
                    }
                    catch (Exception ex)
                    {
                        System.Console.WriteLine($"[DownloadConfirm] AutoDownload enqueue failed: {ex.Message}");
                    }
                }
            };

            PendingConfirms.Add(vm);
            IsInboxHidden = false;   // auto-unhide when a new episode arrives
            this.RaisePropertyChanged(nameof(HasPendingConfirms));
            this.RaisePropertyChanged(nameof(InboxVisible));
            this.RaisePropertyChanged(nameof(InboxPillVisible));
        });

        return Task.CompletedTask;
    }

    private void RemovePendingConfirm(DownloadConfirmVm card)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            PendingConfirms.Remove(card);
            this.RaisePropertyChanged(nameof(HasPendingConfirms));
            this.RaisePropertyChanged(nameof(InboxVisible));
            this.RaisePropertyChanged(nameof(InboxPillVisible));
        });
    }

    /// <summary>Raise a series' watched cursor so the RSS monitor won't re-offer an episode already actioned.</summary>
    private async Task AdvanceSeriesCursorAsync(int seriesId, int episodeNumber)
    {
        if (_dbContextFactory == null || seriesId <= 0 || episodeNumber <= 0) return;
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(CancellationToken.None);
            var series = await db.Series.FindAsync(new object[] { seriesId }, CancellationToken.None);
            if (series != null && episodeNumber > series.LastEpisodeNumber)
            {
                series.LastEpisodeNumber = episodeNumber;
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"[DownloadConfirm] cursor advance failed: {ex.Message}");
        }
    }
}