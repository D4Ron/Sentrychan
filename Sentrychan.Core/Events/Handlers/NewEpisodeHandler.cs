using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services;

namespace Sentrychan.Core.Events.Handlers;

public class NewEpisodeHandler : INotificationHandler<NewEpisodeFoundEvent>
{
    private readonly DownloadQueueManager _downloadQueue;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<NewEpisodeHandler> _logger;

    public NewEpisodeHandler(
        DownloadQueueManager downloadQueue,
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<NewEpisodeHandler> logger)
    {
        _downloadQueue = downloadQueue;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task Handle(
        NewEpisodeFoundEvent notification, CancellationToken ct)
    {
        // Secondary episodes are handled by the UI — don't auto-download
        if (notification.IsSecondary) return;

        _logger.LogInformation(
            "Enqueuing new episode: {Title} Ep {Ep}",
            notification.SeriesTitle, notification.EpisodeNumber);

        // Resolve the series first so we can attach the SeriesId to the job and
        // eagerly advance LastEpisodeNumber (guards against the RSS monitor
        // re-finding and re-downloading the same episode on the next tick).
        int seriesId = 0;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var series = await db.Series
                .FirstOrDefaultAsync(s => s.MalId == notification.MalId, ct);

            if (series != null)
            {
                seriesId = series.Id;
                if (notification.EpisodeNumber > series.LastEpisodeNumber)
                    series.LastEpisodeNumber = notification.EpisodeNumber;
                series.LastCheckedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to update episode state for {Title}", notification.SeriesTitle);
        }

        // Single enqueue path: hands the link to the active backend and records the job.
        await _downloadQueue.EnqueueAsync(
            notification.DownloadLink,
            seriesId,
            notification.EpisodeNumber,
            notification.SeriesTitle,
            notification.RssTitle,
            ct);
    }
}