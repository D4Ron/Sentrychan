using ReactiveUI;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Sentrychan.Core.Models.Api;
using Sentrychan.Core.Data;
using Sentrychan.Core.Library;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Sentrychan.UI.Services;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;

namespace Sentrychan.UI.ViewModels;

public class SeriesDetailViewModel : ViewModelBase
{
    private readonly ISeriesService _seriesService;
    private readonly IAnimeApiService _animeApiService;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ITitleAliasService _titleAliasService;
    private readonly IRssMonitorService _rssMonitorService;
    private readonly IVideoFileLocator _fileLocator;

    private Series _series;
    public Series Series
    {
        get => _series;
        set 
        {
            this.RaiseAndSetIfChanged(ref _series, value);
            this.RaisePropertyChanged(nameof(CurrentEpisode));
            this.RaisePropertyChanged(nameof(TotalEpisodesDisplay));
            this.RaisePropertyChanged(nameof(SeparateParts));
            this.RaisePropertyChanged(nameof(HasParts));
        }
    }

    // Per-series exceptions to the library naming template; saved as soon as they change.
    public bool TidyExcluded
    {
        get => Series.TidyExcluded;
        set
        {
            if (Series.TidyExcluded == value) return;
            Series.TidyExcluded = value;
            this.RaisePropertyChanged();
            _ = SaveLibraryOptionsAsync();
        }
    }

    public bool SeparateParts
    {
        get => Series.SeparateParts;
        set
        {
            if (Series.SeparateParts == value) return;
            Series.SeparateParts = value;
            this.RaisePropertyChanged();
            _ = SaveLibraryOptionsAsync();
        }
    }

    /// <summary>Only a show whose parts/cours share a season has anything to keep apart.</summary>
    public bool HasParts => SeasonLayout.Resolver is { IsReady: true } r && Series is { MalId: > 0 } s
        && SeasonLayout.Place(r, s.MalId, s.Title, false) != SeasonLayout.Place(r, s.MalId, s.Title, true);

    public bool KeepFileNames
    {
        get => Series.KeepFileNames;
        set
        {
            if (Series.KeepFileNames == value) return;
            Series.KeepFileNames = value;
            this.RaisePropertyChanged();
            _ = SaveLibraryOptionsAsync();
        }
    }

    private async Task SaveLibraryOptionsAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.Series.Where(s => s.Id == Series.Id).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.TidyExcluded, Series.TidyExcluded)
                .SetProperty(s => s.KeepFileNames, Series.KeepFileNames)
                .SetProperty(s => s.SeparateParts, Series.SeparateParts));
        }
        catch (Exception ex) { Console.WriteLine($"[SeriesDetail] library options not saved: {ex.Message}"); }
    }

    // This show's own release-group rule; saved as soon as it changes. Empty/"Use my setting"
    // follows Settings → General.
    public const string GroupModeDefault = "Use my setting";
    public string[] GroupModeOptions { get; } =
        [GroupModeDefault, SettingsViewModel.GroupModePrefer, SettingsViewModel.GroupModeOnly, SettingsViewModel.GroupModeAny];

    public string GroupMode
    {
        get => string.IsNullOrWhiteSpace(Series.GroupMode)
            ? GroupModeDefault
            : SettingsViewModel.GroupModeLabel(Sentrychan.Core.Services.ReleaseGroupPolicy.ParseMode(Series.GroupMode));
        set
        {
            var stored = value == GroupModeDefault || string.IsNullOrEmpty(value)
                ? null
                : SettingsViewModel.GroupModeFromLabel(value).ToString();
            if (Series.GroupMode == stored) return;
            Series.GroupMode = stored;
            this.RaisePropertyChanged();
            _ = SaveGroupRuleAsync();
        }
    }

    public string SeriesGroups
    {
        get => Series.PreferredGroups ?? string.Empty;
        set
        {
            var stored = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (Series.PreferredGroups == stored) return;
            Series.PreferredGroups = stored;
            this.RaisePropertyChanged();
            _ = SaveGroupRuleAsync();
        }
    }

    private async Task SaveGroupRuleAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.Series.Where(s => s.Id == Series.Id).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.GroupMode, Series.GroupMode)
                .SetProperty(s => s.PreferredGroups, Series.PreferredGroups));
        }
        catch (Exception ex) { Console.WriteLine($"[SeriesDetail] release-group rule not saved: {ex.Message}"); }
    }

    /// <summary>
    /// The number groups give this season's episode 1, as the user typed it (Series.EpisodeNumberOffset + 1);
    /// empty works it out from the season chain.
    /// </summary>
    public string EpisodeOneNumber
    {
        get => Series.EpisodeNumberOffset is { } offset ? (offset + 1).ToString() : string.Empty;
        set
        {
            int? offset = int.TryParse(value?.Trim().TrimStart('#'), out var n) && n >= 1 ? n - 1 : null;
            if (Series.EpisodeNumberOffset == offset) return;
            Series.EpisodeNumberOffset = offset;
            this.RaisePropertyChanged();
            _ = SaveNumberingAsync();
        }
    }

    /// <summary>What an empty box means for this show.</summary>
    public string EpisodeOneWatermark => _automaticFirst is { } first ? $"{first} (worked out)" : "1 (same as here)";

    private async Task SaveNumberingAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.Series.Where(s => s.Id == Series.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.EpisodeNumberOffset, Series.EpisodeNumberOffset));
            BuildFamilyLine();
            await BuildEpisodeGridAsync(Series, DownloadHistory.ToList(), CancellationToken.None);
        }
        catch (Exception ex) { Console.WriteLine($"[SeriesDetail] numbering not saved: {ex.Message}"); }
    }

    public int CurrentEpisode => Series.LastEpisodeNumber;
    /// <summary>" (#18)": the current episode's number counted straight through, when the show has one.</summary>
    public string CurrentAbsoluteText => _absoluteOffset is { } offset && Series.LastEpisodeNumber > 0 ? $" (#{offset + Series.LastEpisodeNumber})" : string.Empty;
    public string TotalEpisodesDisplay => Series?.TotalEpisodes?.ToString() ?? "?";

    private AnimeResult? _metadata;
    public AnimeResult? Metadata
    {
        get => _metadata;
        set => this.RaiseAndSetIfChanged(ref _metadata, value);
    }

    public ObservableCollection<AnimeResult> Recommendations { get; } = new();
    public ObservableCollection<WatchHistoryEntry> WatchHistory { get; } = new();
    public ObservableCollection<DownloadJob> DownloadHistory { get; } = new();
    public ObservableCollection<TitleAlias> Aliases { get; } = new();

    private ObservableCollection<EpisodeStatusVm> _episodeGrid = new();
    /// <summary>Replaced whole once built: adding 170 tiles one by one laid the page out 170 times.</summary>
    public ObservableCollection<EpisodeStatusVm> EpisodeGrid { get => _episodeGrid; private set => this.RaiseAndSetIfChanged(ref _episodeGrid, value); }

    // Long histories show their latest entries; the rest one click away.
    private const int HistoryShown = 8;
    private bool _showAllHistory;
    public bool ShowAllHistory
    {
        get => _showAllHistory;
        set
        {
            this.RaiseAndSetIfChanged(ref _showAllHistory, value);
            this.RaisePropertyChanged(nameof(WatchHistoryShown));
            this.RaisePropertyChanged(nameof(DownloadHistoryShown));
        }
    }
    public IEnumerable<WatchHistoryEntry> WatchHistoryShown => ShowAllHistory ? WatchHistory : WatchHistory.Take(HistoryShown);
    public IEnumerable<DownloadJob> DownloadHistoryShown => ShowAllHistory ? DownloadHistory : DownloadHistory.Take(HistoryShown);
    public bool HasMoreHistory => !ShowAllHistory && (WatchHistory.Count > HistoryShown || DownloadHistory.Count > HistoryShown);
    public string ShowAllHistoryText => $"Show all ({WatchHistory.Count} watched, {DownloadHistory.Count} downloads)";

    private bool _isLoading;
    /// <summary>Only while the page's own data loads — a fraction of a second.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    private string? _detailsStatus;
    /// <summary>Shown where the synopsis goes while details load from MyAnimeList, or when they can't.</summary>
    public string? DetailsStatus { get => _detailsStatus; private set => this.RaiseAndSetIfChanged(ref _detailsStatus, value); }

    /// <summary>
    /// Where this season sits in its show, and the numbers groups counting straight through give it:
    /// "Season 3 of Jujutsu Kaisen · #48–59 counted straight through". Empty for a single season.
    /// </summary>
    public string? FamilyLine { get; private set; }

    public string QualityPreferenceChoice
    {
        get => string.IsNullOrEmpty(Series.QualityPreference) ? "Any" : Series.QualityPreference;
        set
        {
            Series.QualityPreference = value == "Any" ? null : value;
            this.RaisePropertyChanged();
            _ = SaveQualityAsync();
        }
    }
    public string[] QualityOptions { get; } = ["Any", "1080p", "720p", "480p"];

    private async Task SaveQualityAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.Series.Where(s => s.Id == Series.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.QualityPreference, Series.QualityPreference));
        }
        catch (Exception ex) { Console.WriteLine($"[SeriesDetail] quality not saved: {ex.Message}"); }
    }

    public ReactiveCommand<Unit, Unit> BackCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleMonitoringCommand { get; }
    public ReactiveCommand<string, Unit> AddAliasCommand { get; }
    public ReactiveCommand<TitleAlias, Unit> RemoveAliasCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshMetadataCommand { get; }

    public SeriesDetailViewModel(
        Series series,
        ISeriesService seriesService,
        IAnimeApiService animeApiService,
        IDbContextFactory<AppDbContext> dbFactory,
        ITitleAliasService titleAliasService,
        IRssMonitorService rssMonitorService,
        IVideoFileLocator fileLocator)
    {
        _series = series;
        _seriesService = seriesService;
        _animeApiService = animeApiService;
        _dbFactory = dbFactory;
        _titleAliasService = titleAliasService;
        _rssMonitorService = rssMonitorService;
        _fileLocator = fileLocator;

        BackCommand = ReactiveCommand.Create(() => 
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
            {
                mainVm.SetCurrentView(AppView.Library);
            }
        });

        ToggleMonitoringCommand = ReactiveCommand.CreateFromTask(async ct => 
        {
            Series.MonitoringState = Series.MonitoringState == MonitoringState.Active ? MonitoringState.Paused : MonitoringState.Active;
            using var db = await _dbFactory.CreateDbContextAsync(ct);
            db.Series.Update(Series);
            await db.SaveChangesAsync(ct);
            this.RaisePropertyChanged(nameof(Series));
        });

        AddAliasCommand = ReactiveCommand.CreateFromTask<string>(async (alias, ct) => 
        {
            if (string.IsNullOrWhiteSpace(alias)) return;
            await _titleAliasService.AddAliasAsync(Series.Id, alias, ct);
            await LoadAliasesAsync(ct);
        });

        RemoveAliasCommand = ReactiveCommand.CreateFromTask<TitleAlias>(async (alias, ct) => 
        {
            await _titleAliasService.RemoveAliasAsync(alias.Id, ct);
            await LoadAliasesAsync(ct);
        });

        RefreshMetadataCommand = ReactiveCommand.CreateFromTask(async ct => await LoadMetadataAsync(ct));

        MarkEpisodeDownloadedCommand = ReactiveCommand.CreateFromTask<int>(MarkEpisodeDownloadedAsync);
        MarkEpisodeMissingCommand = ReactiveCommand.CreateFromTask<int>(MarkEpisodeMissingAsync);
        SetCurrentEpisodeCommand = ReactiveCommand.CreateFromTask<int>(SetCurrentEpisodeAsync);
        PlayEpisodeCommand = ReactiveCommand.CreateFromTask<int>(PlayEpisodeAsync);

        _ = InitializeAsync(CancellationToken.None);
    }

    /// <summary>
    /// The page's own data first — episodes, history, aliases, all local — then the details from
    /// MyAnimeList in the background. It used to wait for MyAnimeList (details, then recommendations)
    /// before even building the episode grid, behind a dark overlay: minutes when the API is slow or
    /// down, as it was on 2026-10-06.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            BuildFamilyLine();
            await Task.WhenAll(LoadWatchHistoryAsync(ct), LoadDownloadHistoryAsync(ct), LoadAliasesAsync(ct));
            await BuildEpisodeGridAsync(Series, DownloadHistory.ToList(), ct);
        }
        finally
        {
            IsLoading = false;
        }
        _ = LoadOnlineDetailsAsync(ct);
    }

    private async Task LoadOnlineDetailsAsync(CancellationToken ct)
    {
        DetailsStatus = "Loading details from MyAnimeList…";
        try
        {
            await LoadMetadataAsync(ct);
            DetailsStatus = Metadata == null ? "MyAnimeList isn't answering right now — details will show when it's back." : null;
            if (Metadata == null) return;
            // Spaced from the details request: the API allows a few requests a second.
            await Task.Delay(400, ct);
            await LoadRecommendationsAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DetailsStatus = "Details couldn't be loaded: " + ex.Message;
        }
    }

    private void BuildFamilyLine()
    {
        string? line = null;
        _automaticFirst = null;
        if (App.Services?.GetService(typeof(ITitleResolverService)) is ITitleResolverService { IsReady: true } resolver)
        {
            var chain = resolver.GetSeasonChain(Series.MalId);
            var mine = chain.ToList().FindIndex(a => a.MalId == Series.MalId);
            if (chain.Count >= 2 && mine >= 0)
                line = $"Season {Sentrychan.Core.Services.ReleaseMatcher.SeasonsOf(chain)[mine]} of {chain[0].CanonicalTitle}";
            // What the chain says, whatever the user has set — the box's watermark.
            var automatic = new Series { MalId = Series.MalId, Title = Series.Title, TotalEpisodes = Series.TotalEpisodes };
            if (Sentrychan.Core.Services.ReleaseMatcher.AbsoluteForms(resolver, automatic, 1) is [var first, ..])
                _automaticFirst = first.Number;
        }

        _absoluteOffset = Series.EpisodeNumberOffset is { } user ? (user > 0 ? user : null) : _automaticFirst - 1;
        if (_absoluteOffset is { } offset)
        {
            var numbers = Series.TotalEpisodes is > 0 and var total ? $"#{offset + 1}–{offset + total}" : $"from #{offset + 1}";
            line = (line == null ? "" : line + " · ") + numbers + " counted straight through"
                   + (Series.EpisodeNumberOffset != null ? " (your setting)" : "");
        }
        FamilyLine = line;
        this.RaisePropertyChanged(nameof(FamilyLine));
        this.RaisePropertyChanged(nameof(EpisodeOneWatermark));
        this.RaisePropertyChanged(nameof(CurrentAbsoluteText));
    }

    private int? _absoluteOffset;
    private int? _automaticFirst;

    private async Task LoadMetadataAsync(CancellationToken ct)
    {
        Metadata = await _animeApiService.GetAnimeByIdAsync(Series.MalId, ct);
    }

    private async Task LoadWatchHistoryAsync(CancellationToken ct)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var history = await db.WatchHistory
            .Where(w => w.SeriesId == Series.Id)
            .OrderByDescending(w => w.WatchedAt)
            .ToListAsync(ct);
        
        WatchHistory.Clear();
        foreach (var entry in history) WatchHistory.Add(entry);
        RaiseHistory();
    }

    private void RaiseHistory()
    {
        this.RaisePropertyChanged(nameof(WatchHistoryShown));
        this.RaisePropertyChanged(nameof(DownloadHistoryShown));
        this.RaisePropertyChanged(nameof(HasMoreHistory));
        this.RaisePropertyChanged(nameof(ShowAllHistoryText));
    }

    public ReactiveCommand<Unit, Unit> ShowAllHistoryCommand => _showAllHistoryCommand ??=
        ReactiveCommand.Create(() => { ShowAllHistory = true; RaiseHistory(); });
    private ReactiveCommand<Unit, Unit>? _showAllHistoryCommand;

    private async Task LoadDownloadHistoryAsync(CancellationToken ct)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var downloads = await db.DownloadJobs
            .Where(j => j.SeriesId == Series.Id)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(ct);
        
        DownloadHistory.Clear();
        foreach (var job in downloads) DownloadHistory.Add(job);
        RaiseHistory();
    }

    private async Task LoadAliasesAsync(CancellationToken ct)
    {
        var aliases = await _titleAliasService.GetForSeriesAsync(Series.Id, ct);
        Aliases.Clear();
        foreach (var a in aliases) Aliases.Add(a);
    }

    private async Task LoadRecommendationsAsync(CancellationToken ct)
    {
        var recs = await _animeApiService.GetAnimeRecommendationsAsync(Series.MalId, ct);
        Recommendations.Clear();
        foreach (var r in recs.Take(6)) Recommendations.Add(r);
    }

    public ReactiveCommand<int, System.Reactive.Unit> MarkEpisodeDownloadedCommand { get; }
    public ReactiveCommand<int, System.Reactive.Unit> MarkEpisodeMissingCommand { get; }
    public ReactiveCommand<int, System.Reactive.Unit> SetCurrentEpisodeCommand { get; }
    public ReactiveCommand<int, System.Reactive.Unit> PlayEpisodeCommand { get; }

    private string? _playStatus;
    public string? PlayStatus { get => _playStatus; set => this.RaiseAndSetIfChanged(ref _playStatus, value); }

    /// <summary>
    /// Plays an episode, queueing every later episode that's on disk behind it so "next"
    /// carries on through the season.
    /// </summary>
    private async Task PlayEpisodeAsync(int episode, CancellationToken ct)
    {
        var total = Math.Min(Series.TotalEpisodes ?? Math.Max(Series.LastEpisodeNumber, episode), MaxEpisodes);
        var playlist = new List<PlaybackItem>();
        var start = -1;
        var files = await _fileLocator.FindVideoFilesAsync(Series.Title, Math.Max(total, episode), ct);
        for (int ep = 1; ep <= Math.Max(total, episode); ep++)
        {
            if (!files.TryGetValue(ep, out var path)) continue;
            if (ep == episode) start = playlist.Count;
            playlist.Add(new PlaybackItem($"{Series.Title} · Episode {ep}", FilePath: path));
        }

        if (start < 0)
        {
            PlayStatus = $"Episode {episode} isn't on disk.";
            return;
        }
        PlayStatus = null;
        await PlayerLauncher.PlayAsync(playlist, start);
    }

    /// <summary>The grid's upper bound: a 1,000-episode show would lay out a thousand tiles.</summary>
    private const int MaxEpisodes = 500;

    private async Task BuildEpisodeGridAsync(Series series, List<DownloadJob> jobs, CancellationToken ct)
    {
        int total = series.TotalEpisodes ?? Math.Max(series.LastEpisodeNumber, 1);
        total = Math.Min(total, MaxEpisodes);

        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var manuallyMarked = await db.WatchHistory
            .Where(w => w.SeriesId == series.Id && w.Source == WatchSource.Manual)
            .Select(w => w.EpisodeNumber)
            .ToHashSetAsync(ct);

        // Everything past the progress mark is looked up on disk once, not once per episode.
        var onDisk = total > series.LastEpisodeNumber
            ? await _fileLocator.FindVideoFilesAsync(series.Title, total, ct)
            : new Dictionary<int, string>();
        var jobByEpisode = jobs.GroupBy(j => j.EpisodeNumber).ToDictionary(g => g.Key, g => g.First());

        var tiles = new List<EpisodeStatusVm>(total);
        for (int ep = 1; ep <= total; ep++)
        {
            jobByEpisode.TryGetValue(ep, out var job);
            var status =
                ep <= series.LastEpisodeNumber ? EpisodeStatus.Watched
                : manuallyMarked.Contains(ep) || onDisk.ContainsKey(ep) ? EpisodeStatus.Downloaded
                : job != null ? EpisodeStatus.Pending
                : EpisodeStatus.Missing;

            tiles.Add(new EpisodeStatusVm
            {
                EpisodeNumber = ep,
                Status = status,
                IsCurrentEpisode = ep == series.LastEpisodeNumber,
                DownloadedAt = job?.CompletedAt,
                Absolute = _absoluteOffset is { } offset ? offset + ep : null,
            });
        }
        EpisodeGrid = new ObservableCollection<EpisodeStatusVm>(tiles);
    }

    private async Task MarkEpisodeDownloadedAsync(int ep, CancellationToken ct)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        bool exists = await db.WatchHistory.AnyAsync(
            w => w.SeriesId == Series.Id && w.EpisodeNumber == ep && w.Source == WatchSource.Manual, ct);
            
        if (!exists)
        {
            db.WatchHistory.Add(new WatchHistoryEntry
            {
                SeriesId = Series.Id,
                EpisodeNumber = ep,
                WatchedAt = DateTime.UtcNow,
                Source = WatchSource.Manual
            });
            await db.SaveChangesAsync(ct);
        }

        var tile = EpisodeGrid.FirstOrDefault(e => e.EpisodeNumber == ep);
        if (tile != null) tile.Status = EpisodeStatus.Downloaded;
    }

    private async Task MarkEpisodeMissingAsync(int ep, CancellationToken ct)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entries = await db.WatchHistory
            .Where(w => w.SeriesId == Series.Id && w.EpisodeNumber == ep && w.Source == WatchSource.Manual)
            .ToListAsync(ct);
            
        if (entries.Any())
        {
            db.WatchHistory.RemoveRange(entries);
            await db.SaveChangesAsync(ct);
        }

        var tile = EpisodeGrid.FirstOrDefault(e => e.EpisodeNumber == ep);
        if (tile != null) 
        {
            var job = DownloadHistory.FirstOrDefault(j => j.EpisodeNumber == ep);
            if (job == null) tile.Status = EpisodeStatus.Missing;
            else
            {
                var filePath = await _fileLocator.FindVideoFileAsync(Series.Title, ep, ct);
                tile.Status = filePath != null ? EpisodeStatus.Downloaded : EpisodeStatus.Pending;
            }
        }
    }

    private async Task SetCurrentEpisodeAsync(int clickedEp, CancellationToken ct)
    {
        int newLastEpisode = clickedEp <= Series.LastEpisodeNumber ? clickedEp - 1 : clickedEp;
        newLastEpisode = Math.Max(0, newLastEpisode);

        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var series = await db.Series.FindAsync(Series.Id);
        if (series == null) return;
        
        series.LastEpisodeNumber = newLastEpisode;
        await db.SaveChangesAsync(ct);

        Series.LastEpisodeNumber = newLastEpisode;
        this.RaisePropertyChanged(nameof(CurrentEpisode));
        this.RaisePropertyChanged(nameof(CurrentAbsoluteText));
        
        await BuildEpisodeGridAsync(Series, DownloadHistory.ToList(), ct);
    }
}
