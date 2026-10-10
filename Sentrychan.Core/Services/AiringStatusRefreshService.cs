using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services.AniList;

namespace Sentrychan.Core.Services;

/// <param name="Refreshed">Shows whose airing status changed.</param>
/// <param name="Corrected">Shows whose episode count, year or kind was filled in or corrected.</param>
public record StatusRefreshReport(int Refreshed, int NowFinished, List<string> AutoRemoved, int Corrected = 0, int Checked = 0);

/// <summary>
/// Keeps the library's show info current: airing status, episode count, and the year and kind when
/// they were never known. A show's info used to be written once, when it was added — so a show
/// added before it aired kept "Not yet aired" and MAL's one-episode placeholder for good, and
/// "stop when all episodes are in" stopped it after episode 1.
///
/// AniList answers for the whole library in one or two requests; Jikan (one request per show,
/// slow and often timing out) is only asked about the shows AniList couldn't place, and only those
/// that could still change.
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
    private readonly AniListShows? _aniList;
    private readonly ILogger<AiringStatusRefreshService> _logger;

    public AiringStatusRefreshService(
        IDbContextFactory<AppDbContext> dbFactory,
        IAnimeApiService api,
        ILogger<AiringStatusRefreshService> logger,
        AniListShows? aniList = null)
    {
        _dbFactory = dbFactory;
        _api = api;
        _aniList = aniList;
        _logger = logger;
    }

    /// <param name="seriesIds">Only these series (the show page's refresh); null for the whole library.</param>
    public async Task<StatusRefreshReport> RefreshAsync(CancellationToken ct = default, IReadOnlyCollection<int>? seriesIds = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var all = await db.Series.ToListAsync(ct);
        var candidates = all.Where(s => s.MalId > 0 && (seriesIds == null || seriesIds.Contains(s.Id))).ToList();

        var fromAniList = new Dictionary<int, AniListMedia>();
        if (_aniList != null && candidates.Count > 0)
        {
            try
            {
                fromAniList = await _aniList.ForMalIdsAsync(candidates.Select(s => (s.MalId, (string?)s.Title)).ToList(), ct: ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogWarning("[StatusRefresh] AniList unavailable, asking MyAnimeList: {Message}", ex.Message); }
        }

        int refreshed = 0, nowFinished = 0, corrected = 0;
        foreach (var s in candidates)
        {
            if (ct.IsCancellationRequested) break;

            string? status;
            int? episodes;
            var fixedInfo = false;
            if (fromAniList.TryGetValue(s.MalId, out var media))
            {
                status = AniListMapper.JikanStatus(media.Status);
                episodes = media.Episodes;
                fixedInfo = Fill(s, media);
            }
            else
            {
                // Finished is a terminal state: not worth a slow per-show request.
                if (AiringStatusNormalizer.Normalize(s.AiringStatus) == AiringStatusNormalizer.Finished) continue;
                try
                {
                    var anime = await _api.GetAnimeByIdAsync(s.MalId, ct);
                    status = anime?.Status;
                    episodes = anime?.Episodes;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[StatusRefresh] Failed for {Title}", s.Title);
                    continue;
                }
                // Pace Jikan — its rate limit punishes bursts hard (429 cascades).
                await Task.Delay(1200, ct);
            }

            var fresh = AiringStatusNormalizer.Normalize(status);
            var wasFinished = AiringStatusNormalizer.Normalize(s.AiringStatus) == AiringStatusNormalizer.Finished;
            if (fresh != null && fresh != AiringStatusNormalizer.Normalize(s.AiringStatus))
            {
                _logger.LogInformation("[StatusRefresh] {Title}: '{Old}' → '{New}'", s.Title, s.AiringStatus ?? "<null>", fresh);
                s.AiringStatus = fresh;
                refreshed++;
                if (fresh == AiringStatusNormalizer.Finished) nowFinished++;
            }

            // A count from before the show finished is a guess (MAL shows "1" for a season in its
            // first week); a finished show's is settled — and may be MAL's split, which AniList
            // doesn't always share, so it isn't overwritten.
            if (episodes is > 0 and var count && count != s.TotalEpisodes && (!wasFinished || s.TotalEpisodes is null or <= 0))
            {
                _logger.LogInformation("[StatusRefresh] {Title}: {Old} → {New} episodes", s.Title, s.TotalEpisodes?.ToString() ?? "?", count);
                s.TotalEpisodes = count;
                fixedInfo = true;
            }
            if (fixedInfo) corrected++;
        }

        // ── Opt-in auto-removal: finished airing AND every episode downloaded ──
        var removedTitles = new List<string>();
        var autoRemove = (await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == AutoRemoveKey, ct))
            ?.Value == "true";
        if (autoRemove && seriesIds == null)
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
        _logger.LogInformation("[StatusRefresh] {Checked} shows checked ({AniList} from AniList): {Refreshed} statuses changed, {Corrected} corrected",
            candidates.Count, fromAniList.Count, refreshed, corrected);
        return new StatusRefreshReport(refreshed, nowFinished, removedTitles, corrected, candidates.Count);
    }

    /// <summary>
    /// Year and kind, only where they were never known: both name the show's library folder, and
    /// changing them would move a show out of the folder it's filed in.
    /// </summary>
    private static bool Fill(Series s, AniListMedia media)
    {
        var changed = false;
        if (s.Year is null or <= 0 && media.Year is { } year) { s.Year = year; changed = true; }
        if (string.IsNullOrEmpty(s.MediaType) && AniListMapper.JikanType(media.Format) is { } type) { s.MediaType = type; changed = true; }
        return changed;
    }
}
