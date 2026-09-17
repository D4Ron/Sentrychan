using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.Core.Services;

public record StatusRefreshReport(int Refreshed, int NowFinished, List<string> AutoRemoved);

/// <summary>
/// Keeps Series.AiringStatus in sync with reality. The status is written once at
/// add-time and never touched again, so a show that finishes airing stays parked in
/// "Currently Airing" forever — the library sections never move it. This service
/// re-checks the not-yet-finished entries against Jikan's by-id endpoint (the
/// reliable one; the search endpoint 504s) and updates what changed.
///
/// Optionally (config "AutoRemoveCompletedSeries", default off) it also removes
/// entries that are BOTH finished airing AND fully downloaded. Removal is DB-only —
/// downloaded files are never touched.
/// </summary>
public class AiringStatusRefreshService
{
    public const string AutoRemoveKey = "AutoRemoveCompletedSeries";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IAnimeApiService _api;
    private readonly ILogger<AiringStatusRefreshService> _logger;

    public AiringStatusRefreshService(
        IDbContextFactory<AppDbContext> dbFactory,
        IAnimeApiService api,
        ILogger<AiringStatusRefreshService> logger)
    {
        _dbFactory = dbFactory;
        _api = api;
        _logger = logger;
    }

    public async Task<StatusRefreshReport> RefreshAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var all = await db.Series.ToListAsync(ct);

        // Only shows that could still change: finished is a terminal state.
        var candidates = all
            .Where(s => s.MalId > 0
                     && AiringStatusNormalizer.Normalize(s.AiringStatus) != AiringStatusNormalizer.Finished)
            .ToList();

        int refreshed = 0, nowFinished = 0;
        foreach (var s in candidates)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var anime = await _api.GetAnimeByIdAsync(s.MalId, ct);
                var fresh = AiringStatusNormalizer.Normalize(anime?.Status);
                if (fresh == null) continue;

                if (fresh != AiringStatusNormalizer.Normalize(s.AiringStatus))
                {
                    _logger.LogInformation("[StatusRefresh] {Title}: '{Old}' → '{New}'",
                        s.Title, s.AiringStatus ?? "<null>", fresh);
                    s.AiringStatus = fresh;
                    refreshed++;
                    if (fresh == AiringStatusNormalizer.Finished) nowFinished++;
                }

                // Fill total episodes when MAL finally knows it (null while airing).
                if (s.TotalEpisodes == null && anime?.Episodes > 0)
                    s.TotalEpisodes = anime.Episodes;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[StatusRefresh] Failed for {Title}", s.Title);
            }

            // Pace Jikan — its rate limit punishes bursts hard (429 cascades).
            await Task.Delay(1200, ct);
        }

        // ── Opt-in auto-removal: finished airing AND every episode downloaded ──
        var removedTitles = new List<string>();
        var autoRemove = (await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == AutoRemoveKey, ct))
            ?.Value == "true";
        if (autoRemove)
        {
            var done = all.Where(s =>
                    AiringStatusNormalizer.Normalize(s.AiringStatus) == AiringStatusNormalizer.Finished
                    && s.TotalEpisodes is > 0
                    && s.LastEpisodeNumber >= s.TotalEpisodes)
                .ToList();

            foreach (var s in done)
            {
                _logger.LogInformation(
                    "[StatusRefresh] Auto-removing completed series '{Title}' ({Have}/{Total}) — files untouched",
                    s.Title, s.LastEpisodeNumber, s.TotalEpisodes);
                removedTitles.Add(s.Title);
                db.Series.Remove(s);
            }
        }

        await db.SaveChangesAsync(ct);
        return new StatusRefreshReport(refreshed, nowFinished, removedTitles);
    }
}
