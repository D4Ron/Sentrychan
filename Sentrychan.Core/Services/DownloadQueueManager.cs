using MediatR;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Sentrychan.Core.Events;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Sentrychan.Core.Data;

namespace Sentrychan.Core.Services;

/// <summary>
/// Single entry point for starting any download. Every trigger — RSS auto-download,
/// confirmation popup, source pick, the Download Hub, and retries — goes through here.
/// It hands the link to the active <see cref="IDownloadBackend"/> (MonoTorrent / qBittorrent /
/// System Default) and records ONE <see cref="DownloadJob"/> carrying the backend handle so the
/// <see cref="FileMovementPipeline"/> can later find and finalize it.
/// </summary>
public class DownloadQueueManager
{
    private readonly IDownloadBackendRouter _router;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IMediator _mediator;
    private readonly ILogger<DownloadQueueManager> _logger;

    // Serialises promotion so a burst of completions can't double-start the same
    // pending job.
    private readonly SemaphoreSlim _promoteLock = new(1, 1);

    /// <summary>AppConfigs key — max simultaneous downloads. 0/absent = unlimited.</summary>
    public const string MaxConcurrentKey = "MaxConcurrentDownloads";

    public DownloadQueueManager(
        IDownloadBackendRouter router,
        IDbContextFactory<AppDbContext> dbFactory,
        IMediator mediator,
        ILogger<DownloadQueueManager> logger)
    {
        _router = router;
        _dbFactory = dbFactory;
        _mediator = mediator;
        _logger = logger;
    }

    private async Task<int> ReadLimitAsync(AppDbContext db, CancellationToken ct)
    {
        var row = await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == MaxConcurrentKey, ct);
        return row != null && int.TryParse(row.Value, out var n) && n > 0 ? n : 0;
    }

    /// <summary>
    /// How many transfers the active backend is really running. Counted from the
    /// backend rather than the DB so a stale Downloading row (crashed app, removed
    /// torrent) can never wedge the queue shut. Backends that don't track transfers
    /// (FDM / system default) report none — the cap simply doesn't apply to them,
    /// which is accurate: we can't throttle an external download manager.
    /// </summary>
    private async Task<int> CountActiveAsync(CancellationToken ct)
    {
        try
        {
            var statuses = await _router.Active.GetAllStatusAsync(ct);
            return statuses.Count(s => s.State is BackendState.Downloading
                                              or BackendState.Checking
                                              or BackendState.Queued);
        }
        catch { return 0; }
    }

    /// <summary>
    /// Unified enqueue. Resolves the staging folder, hands the link to the active backend,
    /// and records a single Downloading job. The FileMovementPipeline completes it once the
    /// file finishes (via torrent-completion event or filesystem detection).
    /// </summary>
    public async Task EnqueueAsync(
        string url,
        int seriesId,
        int episodeNumber,
        string seriesTitle,
        string rssTitle,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.LogWarning("Enqueue ignored — empty download link for {Title}", seriesTitle);
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var savePath = await ResolveSavePathAsync(db, ct);

        var active = _router.Active;
        var expectedName = string.IsNullOrWhiteSpace(rssTitle) ? null : rssTitle;

        var backendEnum = Enum.TryParse<DownloadBackend>(active.BackendType, ignoreCase: true, out var be)
            ? be
            : DownloadBackend.SystemDefault;

        // At capacity? Record the job as Pending instead of starting it. It gets
        // promoted by PromotePendingAsync when a running download finishes.
        var limit = await ReadLimitAsync(db, ct);
        if (limit > 0 && await CountActiveAsync(ct) >= limit)
        {
            db.DownloadJobs.Add(new DownloadJob
            {
                SeriesId         = seriesId > 0 ? seriesId : null,
                EpisodeNumber    = episodeNumber,
                DownloadLink     = url,
                RssTitle         = rssTitle ?? string.Empty,
                ExpectedFileName = expectedName,
                Backend          = backendEnum,
                Status           = JobStatus.Pending,
                CreatedAt        = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Queued {Title} Ep {Ep} — at the {Limit}-download limit, will start when a slot frees",
                seriesTitle, episodeNumber, limit);
            return;
        }

        string? handle = null;
        try
        {
            handle = await active.AddAsync(url, savePath, expectedName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backend {Backend} failed to add {Url}", active.Name, url);
        }

        db.DownloadJobs.Add(new DownloadJob
        {
            // 0 = standalone (not in library) — store NULL, an FK to Series id 0
            // would violate the constraint and kill the insert.
            SeriesId        = seriesId > 0 ? seriesId : null,
            EpisodeNumber   = episodeNumber,
            DownloadLink    = url,
            RssTitle        = rssTitle ?? string.Empty,
            ExpectedFileName = expectedName,
            Backend         = backendEnum,
            TorrentHash     = handle,
            Status          = handle != null ? JobStatus.Downloading : JobStatus.Failed,
            CreatedAt       = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Enqueued {Title} Ep {Ep} via {Backend} (handle: {Handle})",
            seriesTitle, episodeNumber, active.Name, handle ?? "none");

        // Surface start failures — the user shouldn't have to discover a dead
        // job by checking the Downloads tab.
        if (handle == null)
        {
            await _mediator.Publish(new MonitorStatusEvent(
                MonitorStatus.Error,
                ErrorMessage: $"Download failed to start: {seriesTitle} Ep {episodeNumber} via {active.Name}"), ct);
        }
    }

    /// <summary>
    /// Starts held (Pending) jobs while there are free slots. Called when a download
    /// finishes, opportunistically after saves that may have raised the limit, and
    /// once at startup. Unlimited (0) promotes everything still pending.
    /// </summary>
    public async Task PromotePendingAsync(CancellationToken ct = default)
    {
        if (!await _promoteLock.WaitAsync(0, ct)) return; // a promotion pass is already running

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var pending = await db.DownloadJobs
                .Where(j => j.Status == JobStatus.Pending)
                .OrderBy(j => j.CreatedAt)
                .ToListAsync(ct);
            if (pending.Count == 0) return;

            var limit    = await ReadLimitAsync(db, ct);
            var active   = _router.Active;
            var savePath = await ResolveSavePathAsync(db, ct);
            var running  = await CountActiveAsync(ct);

            foreach (var job in pending)
            {
                if (limit > 0 && running >= limit) break;
                if (ct.IsCancellationRequested) break;

                string? handle = null;
                try
                {
                    handle = await active.AddAsync(job.DownloadLink, savePath, job.ExpectedFileName, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Backend {Backend} failed to start pending job {Id}",
                        active.Name, job.Id);
                }

                job.TorrentHash = handle;
                job.Status      = handle != null ? JobStatus.Downloading : JobStatus.Failed;
                if (handle != null)
                {
                    running++;
                    _logger.LogInformation("Promoted pending job {Id} ({Title})", job.Id, job.RssTitle);
                }
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pending-download promotion failed");
        }
        finally
        {
            _promoteLock.Release();
        }
    }

    /// <summary>Downloads land in the configured staging folder; the pipeline moves them to the library.</summary>
    private static async Task<string> ResolveSavePathAsync(AppDbContext db, CancellationToken ct)
    {
        var savePath = (await db.AppConfigs
            .FirstOrDefaultAsync(c => c.Key == "DownloadPath", ct))?.Value;
        if (string.IsNullOrWhiteSpace(savePath))
            savePath = Path.Combine(Path.GetTempPath(), "Sentrychan");
        try { Directory.CreateDirectory(savePath); } catch { /* best effort */ }
        return savePath;
    }
}
