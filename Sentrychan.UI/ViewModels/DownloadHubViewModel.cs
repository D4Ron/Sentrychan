using ReactiveUI;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using CodeHollow.FeedReader;

namespace Sentrychan.UI.ViewModels;

public class DownloadHubViewModel : ViewModelBase
{
    private readonly IReleaseProviders _releases;
    private readonly IDownloadPickerService _picker;
    private readonly IDbContextFactory<Sentrychan.Core.Data.AppDbContext> _dbFactory;
    private readonly IDownloadBackendRouter? _backendRouter;

    public ObservableCollection<ReleaseResultRowVm> SearchResults { get; } = new();
    public ObservableCollection<ReleaseResultRowVm> TrendingResults { get; } = new();

    // Search needs a release provider, and only a source pack supplies one.
    public bool HasSearchProvider => _releases?.HasSearch == true;
    public const string NoProviderMessage =
        "No release search provider installed. Import a source pack in Settings → Library.";

    private string _searchQuery = string.Empty;
    public string SearchQuery
    {
        get => _searchQuery;
        set => this.RaiseAndSetIfChanged(ref _searchQuery, value);
    }

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        set => this.RaiseAndSetIfChanged(ref _isSearching, value);
    }

    private bool _isLoadingTrending;
    public bool IsLoadingTrending
    {
        get => _isLoadingTrending;
        set => this.RaiseAndSetIfChanged(ref _isLoadingTrending, value);
    }

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    // True when the user hasn't searched yet — show trending instead of results
    private bool _isShowingTrending = true;
    public bool IsShowingTrending
    {
        get => _isShowingTrending;
        set
        {
            this.RaiseAndSetIfChanged(ref _isShowingTrending, value);
            this.RaisePropertyChanged(nameof(IsShowingResults));
        }
    }
    public bool IsShowingResults => !_isShowingTrending;

    // When search matches a library series, these are set
    private string _libraryMatchTitle = string.Empty;
    public string LibraryMatchTitle
    {
        get => _libraryMatchTitle;
        set
        {
            this.RaiseAndSetIfChanged(ref _libraryMatchTitle, value);
            this.RaisePropertyChanged(nameof(HasLibraryMatch));
        }
    }
    public bool HasLibraryMatch => !string.IsNullOrEmpty(_libraryMatchTitle);
    private int _libraryMatchSeriesId;

    private CancellationTokenSource? _searchCts;

    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<ReleaseResultRowVm, Unit> DownloadResultCommand { get; }
    public ReactiveCommand<Unit, Unit> ChangeDownloadMethodCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowTrendingCommand { get; }
    public ReactiveCommand<Unit, Unit> FillGapsForMatchCommand { get; }

    // Design-time ctor
    public DownloadHubViewModel()
    {
        _releases = null!;
        _picker = null!;
        _dbFactory = null!;
        SearchCommand = ReactiveCommand.Create(() => { });
        DownloadResultCommand = ReactiveCommand.Create<ReleaseResultRowVm>(_ => { });
        ChangeDownloadMethodCommand = ReactiveCommand.Create(() => { });
        ShowTrendingCommand = ReactiveCommand.Create(() => { });
        FillGapsForMatchCommand = ReactiveCommand.Create(() => { });
    }

    private readonly Sentrychan.UI.Services.IThemeService? _themeService;
    private bool SecretMode => _themeService?.IsSecretMode ?? false;

    public DownloadHubViewModel(
        IReleaseProviders releases,
        IDownloadPickerService picker,
        IDbContextFactory<Sentrychan.Core.Data.AppDbContext> dbFactory,
        IDownloadBackendRouter? backendRouter = null,
        Sentrychan.UI.Services.IThemeService? themeService = null)
    {
        _releases = releases;
        _picker = picker;
        _dbFactory = dbFactory;
        _backendRouter = backendRouter;
        _themeService = themeService;

        SearchCommand = ReactiveCommand.CreateFromTask(ExecuteSearchAsync);
        DownloadResultCommand = ReactiveCommand.CreateFromTask<ReleaseResultRowVm>(ExecuteDownloadResultAsync);

        ChangeDownloadMethodCommand = ReactiveCommand.CreateFromTask(async ct =>
        {
            await _picker.ClearRememberedChoiceAsync(DownloadContext.Hub, ct);
            await _picker.PickBackendAsync(DownloadContext.Hub, ct);
        });

        ShowTrendingCommand = ReactiveCommand.Create(() =>
        {
            IsShowingTrending = true;
            SearchResults.Clear();
            LibraryMatchTitle = string.Empty;
            StatusMessage = string.Empty;
        });

        FillGapsForMatchCommand = ReactiveCommand.Create(() =>
        {
            // Navigate to the matched series detail — delegate to MainWindowViewModel
            if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow?.DataContext is MainWindowViewModel mvm)
            {
                // Trigger fill gaps via the series ID
                mvm.OpenSeriesDetailByIdCommand?.Execute(_libraryMatchSeriesId).Subscribe();
            }
        });

        // Load trending in background — don't block UI
        _ = LoadTrendingAsync(CancellationToken.None);
    }

    /// <summary>Reload trending for the current mode. Called on nav + secret toggle.</summary>
    public void RefreshForMode()
    {
        IsShowingTrending = true;
        SearchResults.Clear();
        _ = LoadTrendingAsync(CancellationToken.None);
    }

    private async Task LoadTrendingAsync(CancellationToken ct)
    {
        this.RaisePropertyChanged(nameof(HasSearchProvider));
        if (!HasSearchProvider)
        {
            StatusMessage = NoProviderMessage;
            return;
        }

        IsLoadingTrending = true;
        try
        {
            // An empty query returns the provider's newest releases, sorted here by seeders.
            var results = await _releases.SearchAsync(string.Empty, quality: null, secretMode: SecretMode, ct: ct);
            var top = results.OrderByDescending(r => r.Seeders).Take(40);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                TrendingResults.Clear();
                foreach (var r in top)
                    TrendingResults.Add(new ReleaseResultRowVm(r, DownloadResultCommand));
            });
        }
        catch
        {
            // Trending is best-effort; silently ignore failures
        }
        finally
        {
            IsLoadingTrending = false;
        }
    }

    private async Task ExecuteSearchAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;

        if (!HasSearchProvider)
        {
            IsShowingTrending = false;
            SearchResults.Clear();
            StatusMessage = NoProviderMessage;
            return;
        }

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var linkedCt = CancellationTokenSource.CreateLinkedTokenSource(ct, _searchCts.Token).Token;

        IsSearching = true;
        IsShowingTrending = false;
        StatusMessage = "Searching...";
        SearchResults.Clear();
        LibraryMatchTitle = string.Empty;

        try
        {
            var results = await _releases.SearchAsync(SearchQuery, secretMode: SecretMode, ct: linkedCt);
            var sorted = results.OrderByDescending(r => r.Seeders).Take(50);

            foreach (var r in sorted)
                SearchResults.Add(new ReleaseResultRowVm(r, DownloadResultCommand));

            StatusMessage = results.Count == 0
                ? "No results found."
                : $"{results.Count} results";

            // Check if search query matches a library series
            await CheckLibraryMatchAsync(SearchQuery, linkedCt);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusMessage = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    private async Task CheckLibraryMatchAsync(string query, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var series = await db.Series
                .AsNoTracking()
                .Where(s => s.Title.Contains(query) || query.Contains(s.Title))
                .OrderBy(s => Math.Abs(s.Title.Length - query.Length))
                .FirstOrDefaultAsync(ct);

            if (series != null)
            {
                LibraryMatchTitle = series.Title;
                _libraryMatchSeriesId = series.Id;
            }
        }
        catch { }
    }

    private async Task ExecuteDownloadResultAsync(ReleaseResultRowVm row, CancellationToken ct)
    {
        var rawResult = row.GetUnderlyingResult();

        string downloadUrl = !string.IsNullOrEmpty(rawResult.MagnetLink)
            ? rawResult.MagnetLink
            : rawResult.TorrentUrl;

        if (string.IsNullOrEmpty(downloadUrl))
        {
            StatusMessage = "No download link available for this result.";
            return;
        }

        var backend = await _picker.PickBackendAsync(DownloadContext.Hub, ct);
        if (backend == null) return;

        StatusMessage = $"Sending to {backend.Name}...";

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var envPath = (await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == "DownloadPath", ct))?.Value ?? "";

            var safeName = string.Concat(rawResult.Title
                .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var savePath = string.IsNullOrEmpty(safeName)
                ? envPath
                : System.IO.Path.Combine(envPath, safeName);

            var handle = await backend.AddAsync(downloadUrl, savePath, rawResult.Title, ct);
            if (handle != null)
            {
                db.DownloadJobs.Add(new DownloadJob
                {
                    DownloadLink     = downloadUrl,
                    RssTitle         = rawResult.Title,
                    ExpectedFileName = rawResult.Title,
                    Backend          = Enum.TryParse<DownloadBackend>(backend.BackendType, true, out var b)
                                        ? b : DownloadBackend.FDM,
                    TorrentHash      = handle,
                    CreatedAt        = DateTime.UtcNow,
                    Status           = JobStatus.Downloading
                });
                await db.SaveChangesAsync(ct);
                row.MarkDownloaded();
                StatusMessage = $"Enqueued: {rawResult.Title}";
            }
            else
            {
                StatusMessage = $"Download failed via {backend.Name}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Download failed: {ex.Message}";
        }
    }
}
