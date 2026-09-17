using ReactiveUI;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Sentrychan.Core.Models.Api;
using Sentrychan.Core.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.ObjectModel;
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
        }
    }

    public int CurrentEpisode => Series.LastEpisodeNumber;
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
    public ObservableCollection<EpisodeStatusVm> EpisodeGrid { get; } = new();

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
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

        _ = InitializeAsync(CancellationToken.None);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            await Task.WhenAll(
                LoadMetadataAsync(ct),
                LoadWatchHistoryAsync(ct),
                LoadDownloadHistoryAsync(ct),
                LoadAliasesAsync(ct)
            );

            // Fetch recommendations sequentially after metadata to avoid rate bounds
            await Task.Delay(400, ct);
            await LoadRecommendationsAsync(ct);

            await BuildEpisodeGridAsync(Series, DownloadHistory.ToList(), ct);
        }
        finally
        {
            IsLoading = false;
        }
    }

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
    }

    private async Task LoadDownloadHistoryAsync(CancellationToken ct)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var downloads = await db.DownloadJobs
            .Where(j => j.SeriesId == Series.Id)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(ct);
        
        DownloadHistory.Clear();
        foreach (var job in downloads) DownloadHistory.Add(job);
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

    private async Task BuildEpisodeGridAsync(Series series, List<DownloadJob> jobs, CancellationToken ct)
    {
        EpisodeGrid.Clear();
        int total = series.TotalEpisodes ?? Math.Max(series.LastEpisodeNumber, 1);
        total = Math.Min(total, 200);

        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var manuallyMarked = await db.WatchHistory
            .Where(w => w.SeriesId == series.Id && w.Source == WatchSource.Manual)
            .Select(w => w.EpisodeNumber)
            .ToHashSetAsync(ct);

        for (int ep = 1; ep <= total; ep++)
        {
            EpisodeStatus status;
            if (ep <= series.LastEpisodeNumber)
            {
                status = EpisodeStatus.Watched;
            }
            else if (manuallyMarked.Contains(ep))
            {
                status = EpisodeStatus.Downloaded;
            }
            else
            {
                var job = jobs.FirstOrDefault(j => j.EpisodeNumber == ep);
                if (job == null)
                {
                    status = EpisodeStatus.Missing;
                }
                else
                {
                    var filePath = await _fileLocator.FindVideoFileAsync(series.Title, ep, ct);
                    status = filePath != null ? EpisodeStatus.Downloaded : EpisodeStatus.Pending;
                }
            }

            EpisodeGrid.Add(new EpisodeStatusVm
            {
                EpisodeNumber = ep,
                Status = status,
                IsCurrentEpisode = ep == series.LastEpisodeNumber,
                DownloadedAt = jobs.FirstOrDefault(j => j.EpisodeNumber == ep)?.CompletedAt
            });
        }
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
        
        await BuildEpisodeGridAsync(Series, DownloadHistory.ToList(), ct);
    }
}
