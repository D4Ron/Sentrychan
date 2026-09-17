using CodeHollow.FeedReader;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.Events;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using System.Text.RegularExpressions;
using ModelFeedType = Sentrychan.Core.Models.FeedType;

namespace Sentrychan.Core.Services;

public class RssMonitorService : BackgroundService, IRssMonitorService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IMediator _mediator;
    private readonly DownloadQueueManager _downloadQueue;
    private readonly ITitleResolverService _titleResolver;
    private readonly ILogger<RssMonitorService> _logger;

    private volatile bool _running;
    private volatile bool _paused;
    private const string PausedKey = "MonitoringPaused";

    // Monitoring is "on" only while the loop is alive AND not paused by the user.
    public bool IsMonitoring => _running && !_paused;

    // Regex and normalization moved to IEpisodeNormalizer
    private readonly IEpisodeNormalizer _normalizer;
    private static readonly Regex SourceGroupPattern = new(@"^\[(.*?)\]", RegexOptions.Compiled);

    public RssMonitorService(
        IDbContextFactory<AppDbContext> dbFactory,
        IMediator mediator,
        DownloadQueueManager downloadQueue,
        IEpisodeNormalizer normalizer,
        ITitleResolverService titleResolver,
        ILogger<RssMonitorService> logger)
    {
        _dbFactory = dbFactory;
        _mediator = mediator;
        _downloadQueue = downloadQueue;
        _normalizer = normalizer;
        _titleResolver = titleResolver;
        _logger = logger;
    }

    // ── BackgroundService ──────────────────────────────────────────
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _running = true;
        // Honour a "stopped" state chosen before the last shutdown.
        _paused = await GetConfigValueAsync(PausedKey, false, ct);
        _logger.LogInformation("RSS Monitor started (paused={Paused})", _paused);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Improvement 1 — re-read interval on every tick so settings
                // changes take effect without restart
                var intervalMinutes = await GetConfigValueAsync(
                    "CheckIntervalMinutes", 15, ct);

                // Paused = user pressed Stop; keep the loop alive but skip checks.
                if (!_paused) await RunCheckAsync(isManual: false, ct);

                // Wait the configured interval, but wake up early on cancellation
                await Task.Delay(
                    TimeSpan.FromMinutes(intervalMinutes), ct)
                    .ContinueWith(_ => { }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        finally
        {
            _running = false;
            _logger.LogInformation("RSS Monitor stopped");
        }
    }

    // ── IRssMonitorService ─────────────────────────────────────────
    public async Task ManualCheckAsync(CancellationToken ct = default)
        => await RunCheckAsync(isManual: true, ct);

    public void Pause()  { _paused = true;  _ = PersistPausedAsync(true); }
    public void Resume() { _paused = false; _ = PersistPausedAsync(false); }

    private async Task PersistPausedAsync(bool paused)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == PausedKey);
            if (row == null) db.AppConfigs.Add(new AppConfig { Key = PausedKey, Value = paused ? "true" : "false" });
            else row.Value = paused ? "true" : "false";
            await db.SaveChangesAsync();
        }
        catch { /* best-effort — the in-memory pause still takes effect this session */ }
    }

    public async Task CheckSingleFeedAsync(int feedId, CancellationToken ct = default)
        => await RunCheckAsync(isManual: true, ct, singleFeedId: feedId);

    // ── Core Check Logic ───────────────────────────────────────────
    private async Task RunCheckAsync(bool isManual, CancellationToken ct, int? singleFeedId = null)
    {
        await _mediator.Publish(
            new MonitorStatusEvent(MonitorStatus.CheckStarted, IsManual: isManual), ct);

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // Expire "skip once" (non-permanent) SkippedDownload records from the previous cycle
            var intervalMinutesForCleanup = await GetConfigValueAsync("CheckIntervalMinutes", 15, ct);
            var expiryCutoff = DateTime.UtcNow - TimeSpan.FromMinutes(intervalMinutesForCleanup);
            var expiredSkips = await db.SkippedDownloads
                .Where(sd => !sd.IsPermanent && sd.SkippedAt < expiryCutoff)
                .ToListAsync(ct);
            if (expiredSkips.Count > 0)
            {
                db.SkippedDownloads.RemoveRange(expiredSkips);
                await db.SaveChangesAsync(ct);
            }

            var seriesList = await db.Series.ToListAsync(ct);
            IQueryable<RssFeed> feedsQuery = db.RssFeeds.Where(f => f.IsEnabled);

            if (singleFeedId.HasValue)
                feedsQuery = db.RssFeeds.Where(f => f.Id == singleFeedId.Value);

            var feeds = await feedsQuery.ToListAsync(ct);

            if (seriesList.Count == 0 || feeds.Count == 0)
            {
                _logger.LogInformation("Nothing to check — no series or matching feeds configured");
                await _mediator.Publish(
                    new MonitorStatusEvent(MonitorStatus.CheckCompleted, IsManual: isManual), ct);
                return;
            }

            var qualityPreference = await GetConfigValueAsync("QualityPreference", "1080p", ct);

            // Group priority (which release to pick) + auto-download whitelist.
            var preferredGroups = SplitGroups(await GetConfigValueAsync("PreferredReleaseGroups", "", ct));
            var autoDownloadGroups = SplitGroups(await GetConfigValueAsync("AutoDownloadGroups", "", ct));

            var priorityFeeds = feeds.Where(f => f.FeedType == ModelFeedType.Priority).ToList();
            var secondaryFeeds = feeds.Where(f => f.FeedType == ModelFeedType.Secondary).ToList();

            var foundKeys = new HashSet<(int MalId, int EpisodeNumber)>();
            var newEpisodes = new List<NewEpisodeFoundEvent>();
            var pendingEpisodes = new List<NewEpisodeFoundEvent>();

            foreach (var feed in priorityFeeds)
            {
                var quality = feed.PreferredQuality ?? qualityPreference;
                var found = await CheckFeedAsync(feed, seriesList, quality, foundKeys, preferredGroups, ct);
                foreach (var ep in found)
                {
                    var key = (ep.MalId, ep.EpisodeNumber);
                    if (foundKeys.Add(key))
                        newEpisodes.Add(ep with { IsSecondary = false });
                }
            }

            foreach (var feed in secondaryFeeds)
            {
                var quality = feed.PreferredQuality ?? qualityPreference;
                var found = await CheckFeedAsync(feed, seriesList, quality, foundKeys, preferredGroups, ct);
                foreach (var ep in found)
                {
                    var key = (ep.MalId, ep.EpisodeNumber);
                    if (!foundKeys.Contains(key))
                        pendingEpisodes.Add(ep with { IsSecondary = true });
                }
            }

            // Targeted search for fresh series (LastEpisodeNumber <= 0)
            // Normal RSS only has the latest ~75 items. If a series started weeks ago, Ep 1 is gone.
            var freshSeries = seriesList.Where(s => s.LastEpisodeNumber <= 0 && s.AiringStatus != "Finished Airing" && s.AiringStatus != "Completed").ToList();
            foreach (var series in freshSeries)
            {
                // Skip if we already found Episode 1 for this series in the normal feeds
                if (foundKeys.Any(k => k.MalId == series.MalId && k.EpisodeNumber <= 1)) continue;

                var earlyEp = await SearchForEarlyEpisodeAsync(series, qualityPreference, ct);
                if (earlyEp != null)
                {
                    var key = (earlyEp.MalId, earlyEp.EpisodeNumber);
                    if (foundKeys.Add(key))
                    {
                        newEpisodes.Add(earlyEp);
                    }
                }
            }

            // ── Publish with AutoDownload / confirmation branching ─────────────
            // Load SkippedDownloads for all series that appear in newEpisodes
            var newEpMalIds = newEpisodes.Select(e => e.MalId).ToHashSet();
            var seriesByMalId = seriesList
                .Where(s => newEpMalIds.Contains(s.MalId))
                .ToDictionary(s => s.MalId);

            var seriesIdsForSkips = seriesByMalId.Values.Select(s => s.Id).ToHashSet();
            var activeSkips = await db.SkippedDownloads
                .AsNoTracking()
                .Where(sd => seriesIdsForSkips.Contains(sd.SeriesId))
                .ToListAsync(ct);

            var totalFound = newEpisodes.Count + pendingEpisodes.Count;
            var isSilent = totalFound > 1;

            int autoQueuedCount = 0;
            int confirmPendingCount = 0;

            foreach (var ep in newEpisodes)
            {
                if (!seriesByMalId.TryGetValue(ep.MalId, out var series))
                {
                    // Series not found — fall back to legacy auto-download behaviour
                    await _mediator.Publish(ep with { IsSilent = isSilent }, ct);
                    autoQueuedCount++;
                    continue;
                }

                // Check for a permanent or still-active skip entry
                bool isSkipped = activeSkips.Any(sd =>
                    sd.SeriesId == series.Id &&
                    sd.EpisodeNumber == ep.EpisodeNumber);

                if (isSkipped)
                {
                    _logger.LogInformation(
                        "Skipping {Title} Ep {Ep} — found in SkippedDownloads",
                        ep.SeriesTitle, ep.EpisodeNumber);
                    continue;
                }

                // Auto-download only when enabled AND (no group whitelist, or the
                // release is from a whitelisted group). Otherwise fall to confirm.
                var epGroup = ExtractSourceGroup(ep.RssTitle);
                bool groupAllowed = autoDownloadGroups.Count == 0 || GroupInList(epGroup, autoDownloadGroups);

                if (series.AutoDownload && groupAllowed)
                {
                    // Auto-download: publish the original event (NewEpisodeHandler picks it up)
                    await _mediator.Publish(ep with { IsSilent = isSilent }, ct);
                    autoQueuedCount++;
                }
                else
                {
                    // Confirmation required: publish DownloadConfirmationEvent for the UI
                    var releaseGroup = epGroup;
                    var confirmEvt = new DownloadConfirmationEvent(
                        SeriesId:      series.Id,
                        MalId:         ep.MalId,
                        SeriesTitle:   ep.SeriesTitle,
                        PosterPath:    series.PosterPath ?? string.Empty,
                        EpisodeNumber: ep.EpisodeNumber,
                        ReleaseGroup:  releaseGroup,
                        Resolution:    null,
                        SizeDisplay:   string.Empty,
                        Seeders:       0,
                        DownloadLink:  ep.DownloadLink,
                        RssTitle:      ep.RssTitle);

                    await _mediator.Publish(confirmEvt, ct);
                    confirmPendingCount++;
                }
            }

            foreach (var ep in pendingEpisodes)
                await _mediator.Publish(ep with { IsSilent = isSilent }, ct);

            await _mediator.Publish(new MonitorStatusEvent(
                MonitorStatus.CheckCompleted,
                NewEpisodesCount: autoQueuedCount,
                PendingCount: pendingEpisodes.Count + confirmPendingCount,
                IsManual: isManual), ct);

            _logger.LogInformation(
                "Check complete — {New} auto-queued, {Confirm} awaiting confirm, {Pending} pending",
                autoQueuedCount, confirmPendingCount, pendingEpisodes.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RSS check failed");
            await _mediator.Publish(
                new MonitorStatusEvent(MonitorStatus.Error, ErrorMessage: ex.Message), ct);
        }
    }

    // foundKeys passed in for within-feed deduplication too
    private async Task<List<NewEpisodeFoundEvent>> CheckFeedAsync(
        RssFeed feed,
        List<Series> seriesList,
        string qualityPreference,
        HashSet<(int MalId, int EpisodeNumber)> globalFoundKeys,
        List<string> preferredGroups,
        CancellationToken ct)
    {
        var results = new List<NewEpisodeFoundEvent>();

        // All candidate releases keyed by (series, episode) — collected first so
        // we can PICK the best release group instead of taking whichever the feed
        // happened to list first.
        var candidates = new Dictionary<(int MalId, int EpisodeNumber), (NewEpisodeFoundEvent Evt, int Rank)>();

        try
        {
            var parsedFeed = await FeedReader.ReadAsync(feed.Url, ct);

            foreach (var item in parsedFeed.Items)
            {
                var title = item.Title ?? string.Empty;
                var link = item.Link ?? string.Empty;

                if (!MatchesQuality(title, qualityPreference)) continue;

                foreach (var series in seriesList)
                {
                    if (!TitleMatchesSeries(title, series)) continue;

                    bool isCompleted = series.AiringStatus == "Finished Airing" || series.AiringStatus == "Completed";
                    // Same strict rule as everywhere else — a space-padded "S2 - 04"
                    // is a single episode, not a batch.
                    bool isBatch = TitleResolverService.IsBatchRelease(title, 0);

                    if (isCompleted && !isBatch) continue; // Only accept batches for completed series

                    int? episodeNum = ExtractEpisodeNumber(title);
                    // A real batch with no parseable number starts the series at ep 1.
                    if (isBatch && episodeNum == null) episodeNum = 1;
                    if (episodeNum == null) continue;

                    if (episodeNum <= series.LastEpisodeNumber)
                    {
                        _logger.LogTrace("Skipping '{Title}' Ep {Ep} because LastEp is {LastEp}", series.Title, episodeNum, series.LastEpisodeNumber);
                        continue;
                    }

                    var key = (series.MalId, episodeNum.Value);
                    if (globalFoundKeys.Contains(key)) break;

                    int rank = GroupRank(ExtractSourceGroup(title), preferredGroups);
                    var evt = new NewEpisodeFoundEvent(
                        MalId: series.MalId,
                        SeriesTitle: series.Title,
                        EpisodeNumber: episodeNum.Value,
                        DownloadLink: link,
                        RssTitle: title,
                        IsSecondary: false);

                    // Keep the higher-priority group (lower rank wins; first seen breaks ties)
                    if (!candidates.TryGetValue(key, out var existing) || rank < existing.Rank)
                        candidates[key] = (evt, rank);

                    break; // one series per feed item
                }
            }

            foreach (var kv in candidates)
            {
                results.Add(kv.Value.Evt);
                _logger.LogInformation(
                    "New episode: {Title} Ep {Ep} from {Source}",
                    kv.Value.Evt.SeriesTitle, kv.Value.Evt.EpisodeNumber, ExtractSourceGroup(kv.Value.Evt.RssTitle));
            }

            await UpdateFeedHealthAsync(feed.Id, success: true, error: null, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read feed: {Url}", feed.Url);
            await UpdateFeedHealthAsync(feed.Id, success: false, error: ex.Message, ct);
        }

        // One download per series per interval — the earliest unwatched episode
        return results
            .GroupBy(r => r.MalId)
            .Select(g => g.OrderBy(r => r.EpisodeNumber).First())
            .ToList();
    }

    /// <summary>Priority rank of a release group: index in the preferred list (lower = better), or a large value if not listed.</summary>
    private static int GroupRank(string group, List<string> preferredGroups)
    {
        for (int i = 0; i < preferredGroups.Count; i++)
        {
            if (group.Contains(preferredGroups[i], StringComparison.OrdinalIgnoreCase) ||
                preferredGroups[i].Contains(group, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return 10_000;
    }

    private static List<string> SplitGroups(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool GroupInList(string group, List<string> groups)
    {
        if (groups.Count == 0) return false;
        return groups.Any(g =>
            group.Contains(g, StringComparison.OrdinalIgnoreCase) ||
            g.Contains(group, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<NewEpisodeFoundEvent?> SearchForEarlyEpisodeAsync(Series series, string qualityPreference, CancellationToken ct)
    {
        try
        {
            var targetEp = Math.Max(1, series.LastEpisodeNumber + 1); // We look for 1 if it's -1 or 0
            var query = $"{series.Title} {targetEp:D2}";
            var encoded = Uri.EscapeDataString(query);

            // Search Nyaa specifically for English-translated Anime (category 1_2)
            var searchUrl = $"https://nyaa.si/?page=rss&q={encoded}&c=1_2&f=0";

            _logger.LogInformation("Performing targeted search for {Title} Ep {Ep}: {Url}", series.Title, targetEp, searchUrl);
            var parsedFeed = await FeedReader.ReadAsync(searchUrl, ct);

            foreach (var item in parsedFeed.Items)
            {
                var title = item.Title ?? string.Empty;
                var link = item.Link ?? string.Empty;

                if (!MatchesQuality(title, qualityPreference)) continue;
                if (!TitleMatchesSeries(title, series)) continue;

                int? episodeNum = ExtractEpisodeNumber(title);
                if (episodeNum == null || episodeNum <= series.LastEpisodeNumber) continue;

                _logger.LogInformation("Targeted search found early episode: {Title} Ep {Ep} from {Source}", series.Title, episodeNum.Value, ExtractSourceGroup(title));

                return new NewEpisodeFoundEvent(
                    MalId: series.MalId,
                    SeriesTitle: series.Title,
                    EpisodeNumber: episodeNum.Value,
                    DownloadLink: link,
                    RssTitle: title,
                    IsSecondary: false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Targeted search failed for {Title}", series.Title);
        }

        return null;
    }

    // Persist feed health state to DB
    private async Task UpdateFeedHealthAsync(
        int feedId, bool success, string? error, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var feed = await db.RssFeeds.FindAsync([feedId], ct);
            if (feed == null) return;

            feed.LastCheckedAt = DateTime.UtcNow;

            if (success)
            {
                feed.ConsecutiveFailures = 0;
                feed.LastError = null;
                feed.LastSuccessAt = DateTime.UtcNow;
            }
            else
            {
                feed.ConsecutiveFailures++;
                feed.LastError = error;

                // Auto-disable feed after 10 consecutive failures
                if (feed.ConsecutiveFailures >= 10)
                {
                    feed.IsEnabled = false;
                    _logger.LogWarning(
                        "Feed auto-disabled after 10 failures: {Url}", feed.Url);

                    await _mediator.Publish(new MonitorStatusEvent(
                        MonitorStatus.Error,
                        ErrorMessage: $"Feed auto-disabled after repeated failures: {feed.Url}"), ct);
                }
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update feed health for feed {Id}", feedId);
        }
    }

    private bool TitleMatchesSeries(string rssTitle, Series series)
    {
        // Primary: resolve the release to a canonical MAL id via the offline
        // synonym database and compare ids — an integer comparison instead of
        // string fuzzy-matching, which mistook similar titles far too often.
        // When the release resolves to a DIFFERENT anime, that's a definitive NO
        // (this is what stops "Season 2" releases matching the Season 1 entry).
        if (_titleResolver.IsReady && series.MalId > 0)
        {
            var resolved = _titleResolver.ResolveRelease(rssTitle);
            if (resolved is { MalId: > 0 })
                return resolved.MalId == series.MalId;
        }

        // Fallback (resolver unavailable or release unidentifiable): legacy fuzzy
        var allTitles = new List<string> { series.Title };

        if (!string.IsNullOrEmpty(series.OriginalTitle))
            allTitles.Add(series.OriginalTitle);

        if (!string.IsNullOrEmpty(series.AlternativeTitlesJson))
        {
            var alts = System.Text.Json.JsonSerializer
                .Deserialize<List<string>>(series.AlternativeTitlesJson);
            if (alts != null) allTitles.AddRange(alts);
        }

        return allTitles.Any(t => _normalizer.MatchesTitle(rssTitle, t));
    }

    private string NormalizeTitle(string title) => _normalizer.NormalizeTitle(title);

    private int? ExtractEpisodeNumber(string title) =>
        (_titleResolver.IsReady ? _titleResolver.ParseRelease(title).Episode : null)
        ?? _normalizer.ExtractEpisodeNumber(title);

    private bool MatchesQuality(string title, string preference) => _normalizer.MatchesQuality(title, preference);

    private static string ExtractSourceGroup(string title)
    {
        var match = SourceGroupPattern.Match(title);
        return match.Success ? match.Groups[1].Value : "Unknown";
    }

    private async Task<T> GetConfigValueAsync<T>(
        string key, T defaultValue, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var entry = await db.AppConfigs
                .FirstOrDefaultAsync(c => c.Key == key, ct);

            if (entry == null) return defaultValue;
            return (T)Convert.ChangeType(entry.Value, typeof(T));
        }
        catch
        {
            return defaultValue;
        }
    }
}
