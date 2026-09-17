using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services;
using Sentrychan.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unit = System.Reactive.Unit;

namespace Sentrychan.UI.ViewModels;

/// <summary>
/// One row in the Downloads tab: static job info from the DB plus live
/// progress merged in by the backend poll loop.
/// </summary>
public class DownloadJobRowVm : ViewModelBase
{
    public DownloadJob Job { get; }

    public DownloadJobRowVm(DownloadJob job)
    {
        Job = job;
        _status = job.Status;
    }

    public int Id => Job.Id;
    public string SeriesTitle => Job.Series?.Title
        ?? (string.IsNullOrEmpty(Job.RssTitle) ? "Unknown Series" : Job.RssTitle);
    public string EpisodeLabel => Job.EpisodeNumber == 0 ? "Batch" : $"Episode {Job.EpisodeNumber}";
    public DateTime CreatedAt => Job.CreatedAt;
    public string? Handle => Job.TorrentHash;

    private JobStatus _status;
    public JobStatus Status
    {
        get => _status;
        set
        {
            this.RaiseAndSetIfChanged(ref _status, value);
            this.RaisePropertyChanged(nameof(IsDownloading));
        }
    }

    public bool IsDownloading => Status == JobStatus.Downloading;

    // ── Live fields (populated by the poll loop) ──────────────────
    private bool _hasLiveStatus;
    public bool HasLiveStatus
    {
        get => _hasLiveStatus;
        set => this.RaiseAndSetIfChanged(ref _hasLiveStatus, value);
    }

    private double _progressPercent;
    public double ProgressPercent
    {
        get => _progressPercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _progressPercent, value);
            this.RaisePropertyChanged(nameof(ProgressFraction));
            this.RaisePropertyChanged(nameof(ProgressDisplay));
        }
    }

    public double ProgressFraction => _progressPercent / 100.0;
    public string ProgressDisplay => $"{_progressPercent:F0}%";

    private string _speedDisplay = string.Empty;
    public string SpeedDisplay
    {
        get => _speedDisplay;
        set => this.RaiseAndSetIfChanged(ref _speedDisplay, value);
    }

    private string _etaDisplay = string.Empty;
    public string EtaDisplay
    {
        get => _etaDisplay;
        set => this.RaiseAndSetIfChanged(ref _etaDisplay, value);
    }

    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            this.RaiseAndSetIfChanged(ref _isPaused, value);
            this.RaisePropertyChanged(nameof(PauseButtonText));
        }
    }

    public string PauseButtonText => IsPaused ? "Resume" : "Pause";

    /// <summary>Where the finished file landed — only set once the pipeline moved it.</summary>
    public string? FilePath => Job.FinalFilePath;

    /// <summary>The magnet/torrent URL this job was queued from.</summary>
    public string DownloadLink => Job.DownloadLink;

    public bool CanOpenFolder => !string.IsNullOrEmpty(Job.FinalFilePath);
}

public class DownloadsViewModel : ViewModelBase
{
    private readonly IDbContextFactory<AppDbContext>? _dbFactory;
    private readonly DownloadQueueManager? _downloadQueue;
    private readonly IDownloadBackendRouter? _router;

    private Avalonia.Threading.DispatcherTimer? _pollTimer;
    private bool _polling;

    public ObservableCollection<DownloadJobRowVm> Jobs { get; } = new();

    public List<string> StatusOptions { get; } = ["All", "Pending", "Downloading", "Completed", "Failed"];

    private string _statusFilter = "All";
    public string StatusFilter
    {
        get => _statusFilter;
        set
        {
            this.RaiseAndSetIfChanged(ref _statusFilter, value);
            ApplyFilter();
        }
    }

    private IReadOnlyList<DownloadJobRowVm> _filteredJobs = [];
    public IReadOnlyList<DownloadJobRowVm> FilteredJobs
    {
        get => _filteredJobs;
        private set => this.RaiseAndSetIfChanged(ref _filteredJobs, value);
    }

    public ReactiveCommand<Unit, Unit> LoadJobsCommand { get; }
    public ReactiveCommand<int, Unit> RetryCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearCompletedCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearFailedCommand { get; }
    public ReactiveCommand<DownloadJobRowVm, Unit> TogglePauseCommand { get; }
    public ReactiveCommand<DownloadJobRowVm, Unit> RemoveCommand { get; }
    public ReactiveCommand<Unit, Unit> PauseAllCommand { get; }
    public ReactiveCommand<Unit, Unit> ResumeAllCommand { get; }
    public ReactiveCommand<DownloadJobRowVm, Unit> OpenFolderCommand { get; }
    public ReactiveCommand<DownloadJobRowVm, Unit> OpenFileCommand { get; }
    public ReactiveCommand<DownloadJobRowVm, Unit> CopyLinkCommand { get; }

    // Aggregate throughput across every active download, refreshed by the poller.
    private string _totalSpeedDisplay = string.Empty;
    public string TotalSpeedDisplay
    {
        get => _totalSpeedDisplay;
        set => this.RaiseAndSetIfChanged(ref _totalSpeedDisplay, value);
    }

    private bool _hasActiveDownloads;
    public bool HasActiveDownloads
    {
        get => _hasActiveDownloads;
        set => this.RaiseAndSetIfChanged(ref _hasActiveDownloads, value);
    }

    public DownloadsViewModel()
    {
        LoadJobsCommand = ReactiveCommand.CreateFromTask(async ct => await LoadJobsAsync(ct));
        RetryCommand = ReactiveCommand.CreateFromTask<int>(async (id, ct) => await RetryJobAsync(id, ct));
        ClearCompletedCommand = ReactiveCommand.CreateFromTask(async ct => await ClearByStatusAsync(JobStatus.Completed, ct));
        ClearFailedCommand = ReactiveCommand.CreateFromTask(async ct => await ClearByStatusAsync(JobStatus.Failed, ct));
        TogglePauseCommand = ReactiveCommand.CreateFromTask<DownloadJobRowVm>(TogglePauseAsync);
        PauseAllCommand    = ReactiveCommand.CreateFromTask(() => SetAllPausedAsync(true));
        ResumeAllCommand   = ReactiveCommand.CreateFromTask(() => SetAllPausedAsync(false));
        OpenFolderCommand  = ReactiveCommand.Create<DownloadJobRowVm>(OpenFolder);
        OpenFileCommand    = ReactiveCommand.Create<DownloadJobRowVm>(OpenFile);
        CopyLinkCommand    = ReactiveCommand.CreateFromTask<DownloadJobRowVm>(CopyLinkAsync);
        RemoveCommand = ReactiveCommand.CreateFromTask<DownloadJobRowVm>(RemoveJobAsync);
    }

    public DownloadsViewModel(
        IDbContextFactory<AppDbContext> dbFactory,
        DownloadQueueManager downloadQueue,
        IDownloadBackendRouter? router = null) : this()
    {
        _dbFactory = dbFactory;
        _downloadQueue = downloadQueue;
        _router = router;

        // Live-progress poll. The VM is created once at startup, so this also
        // powers the tray tooltip while the window is hidden. GetAllStatusAsync
        // is in-memory for MonoTorrent — cheap when nothing is active.
        if (_router != null)
        {
            _pollTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _pollTimer.Tick += async (_, _) => await PollBackendAsync();
            _pollTimer.Start();
        }
    }

    private async Task LoadJobsAsync(CancellationToken ct)
    {
        if (_dbFactory == null) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var jobs = await db.DownloadJobs
            .Include(j => j.Series)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(ct);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Jobs.Clear();
            foreach (var job in jobs) Jobs.Add(new DownloadJobRowVm(job));
            ApplyFilter();
        });
    }

    private void ApplyFilter()
    {
        if (StatusFilter != "All" && Enum.TryParse<JobStatus>(StatusFilter, out var statusEnum))
            FilteredJobs = Jobs.Where(j => j.Status == statusEnum).ToList();
        else
            FilteredJobs = Jobs.ToList();
    }

    // ── Live backend polling ───────────────────────────────────────

    private async Task PollBackendAsync()
    {
        if (_polling || _router == null) return;
        _polling = true;
        try
        {
            var statuses = await _router.Active.GetAllStatusAsync();
            var byHandle = statuses
                .Where(s => !string.IsNullOrEmpty(s.Handle))
                .ToDictionary(s => s.Handle, StringComparer.OrdinalIgnoreCase);

            int activeCount = 0;
            double progressSum = 0;
            long totalSpeedBps = 0;
            bool anyDisappeared = false;

            foreach (var row in Jobs)
            {
                if (string.IsNullOrEmpty(row.Handle)) continue;

                if (byHandle.TryGetValue(row.Handle, out var st))
                {
                    row.HasLiveStatus = true;
                    row.ProgressPercent = st.ProgressPercent;
                    row.SpeedDisplay = FormatSpeed(st.SpeedBps);
                    row.EtaDisplay = FormatEta(st.EtaSeconds);
                    row.IsPaused = st.State == BackendState.Paused;

                    if (st.State is BackendState.Downloading or BackendState.Checking or BackendState.Queued)
                    {
                        activeCount++;
                        progressSum += st.ProgressPercent;
                        totalSpeedBps += st.SpeedBps;
                    }
                }
                else if (row.HasLiveStatus && row.Status == JobStatus.Downloading)
                {
                    // Was live, now gone from the backend → finished (or removed).
                    // Reload from DB so the completed status shows.
                    row.HasLiveStatus = false;
                    anyDisappeared = true;
                }
            }

            HasActiveDownloads = activeCount > 0;
            TotalSpeedDisplay  = activeCount > 0
                ? $"{activeCount} active · {FormatSpeed(totalSpeedBps)}"
                : string.Empty;

            TrayService.SetDownloadStatus(activeCount,
                activeCount > 0 ? progressSum / activeCount : 0);

            if (anyDisappeared)
                await LoadJobsAsync(CancellationToken.None);
        }
        catch { /* poll is best-effort */ }
        finally { _polling = false; }
    }

    private static string FormatSpeed(long bps)
    {
        if (bps <= 0) return string.Empty;
        return bps switch
        {
            >= 1_048_576 => $"{bps / 1_048_576.0:F1} MB/s",
            >= 1_024     => $"{bps / 1_024.0:F0} KB/s",
            _            => $"{bps} B/s"
        };
    }

    private static string FormatEta(int seconds)
    {
        if (seconds <= 0) return string.Empty;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}h {ts.Minutes}m"
             : ts.TotalMinutes >= 1 ? $"{ts.Minutes}m {ts.Seconds}s"
             : $"{ts.Seconds}s";
    }

    // ── Actions ────────────────────────────────────────────────────

    private async Task RetryJobAsync(int id, CancellationToken ct)
    {
        if (_dbFactory == null || _downloadQueue == null) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var job = await db.DownloadJobs.Include(j => j.Series).FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job == null) return;

        // The re-enqueue creates a fresh job row; drop the failed one.
        db.DownloadJobs.Remove(job);
        await db.SaveChangesAsync(ct);

        await _downloadQueue.EnqueueAsync(
            job.DownloadLink, job.SeriesId ?? 0, job.EpisodeNumber,
            job.Series?.Title ?? "Unknown", job.RssTitle, ct);

        await LoadJobsAsync(ct);
    }

    private async Task TogglePauseAsync(DownloadJobRowVm row)
    {
        if (_router == null || string.IsNullOrEmpty(row.Handle)) return;
        try
        {
            if (row.IsPaused) await _router.Active.ResumeAsync(row.Handle);
            else await _router.Active.PauseAsync(row.Handle);
            row.IsPaused = !row.IsPaused;
        }
        catch { /* backend may not support it */ }
    }

    /// <summary>Copies the job's magnet/torrent link so it can be handed to another client.</summary>
    private async Task CopyLinkAsync(DownloadJobRowVm row)
    {
        if (string.IsNullOrEmpty(row.DownloadLink)) return;

        var clipboard = (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            ? desktop.MainWindow?.Clipboard
            : null;

        if (clipboard == null) return;
        try { await clipboard.SetTextAsync(row.DownloadLink); }
        catch { /* clipboard can be locked by another app */ }
    }

    /// <summary>Pauses or resumes every active download in one go.</summary>
    private async Task SetAllPausedAsync(bool paused)
    {
        if (_router == null) return;

        foreach (var row in Jobs.ToList())
        {
            if (string.IsNullOrEmpty(row.Handle) || row.IsPaused == paused) continue;
            try
            {
                if (paused) await _router.Active.PauseAsync(row.Handle);
                else        await _router.Active.ResumeAsync(row.Handle);
                row.IsPaused = paused;
            }
            catch { /* skip rows the backend no longer knows about */ }
        }
    }

    /// <summary>
    /// Reveals the finished file in Explorer (selected), or falls back to opening the
    /// containing folder when we only know the directory.
    /// </summary>
    private void OpenFolder(DownloadJobRowVm row)
    {
        try
        {
            var path = row.FilePath;
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
                return;
            }

            var dir = !string.IsNullOrEmpty(path)
                ? System.IO.Path.GetDirectoryName(path)
                : null;
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir, UseShellExecute = true
                });
            }
        }
        catch { /* nothing useful to show the user if Explorer refuses */ }
    }

    /// <summary>Opens the finished file with the OS default application for its type.</summary>
    private void OpenFile(DownloadJobRowVm row)
    {
        try
        {
            var path = row.FilePath;
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { /* nothing useful to show if the OS refuses to open it */ }
    }

    private async Task RemoveJobAsync(DownloadJobRowVm row)
    {
        if (_dbFactory == null) return;

        // Stop the backend download too (keep partially-downloaded data off disk).
        if (_router != null && !string.IsNullOrEmpty(row.Handle) && row.Status == JobStatus.Downloading)
        {
            try { await _router.Active.RemoveAsync(row.Handle, deleteData: true); }
            catch { /* best effort */ }
        }

        await using var db = await _dbFactory.CreateDbContextAsync(CancellationToken.None);
        var job = await db.DownloadJobs.FindAsync(row.Id);
        if (job != null)
        {
            db.DownloadJobs.Remove(job);
            await db.SaveChangesAsync();
        }

        // Removing an active download frees a slot under the concurrency limit.
        var queue = App.Services?.GetService(typeof(Sentrychan.Core.Services.DownloadQueueManager))
            as Sentrychan.Core.Services.DownloadQueueManager;
        if (queue != null) _ = queue.PromotePendingAsync(CancellationToken.None);

        await LoadJobsAsync(CancellationToken.None);
    }

    private async Task ClearByStatusAsync(JobStatus status, CancellationToken ct)
    {
        if (_dbFactory == null) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var matching = await db.DownloadJobs
            .Where(j => j.Status == status)
            .ToListAsync(ct);

        if (matching.Count == 0) return;

        db.DownloadJobs.RemoveRange(matching);
        await db.SaveChangesAsync(ct);

        await LoadJobsAsync(ct);
    }
}
