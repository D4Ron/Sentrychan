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

    // Monitoring is "on" only while the loop is alive AND not paused — by the user, or because
    // the other flavour of the app is running.
    public bool IsMonitoring => _running && !_paused && !InstanceGuard.PausedForOtherInstance;

    // Regex and normalization moved to IEpisodeNormalizer
    private readonly IEpisodeNormalizer _normalizer;
    private static readonly Regex SourceGroupPattern = new(@"^\[(.*?)\]", RegexOptions.Compiled);

    // Release-index access for the early-episode search. Empty unless a source pack is loaded.
    private readonly IReleaseProviders _releases;

    // Links each show's seasons through MAL's relations (see ReleaseMatcher); filled in as checks run.
    private readonly SeasonFamilyService? _families;
    private Task _familyRefresh = Task.CompletedTask;

    public RssMonitorService(
        IDbContextFactory<AppDbContext> dbFactory,
        IMediator mediator,
        DownloadQueueManager downloadQueue,
        IEpisodeNormalizer normalizer,
        ITitleResolverService titleResolver,
        IReleaseProviders releases,
        ILogger<RssMonitorService> logger,
        SeasonFamilyService? families = null)
    {
        _dbFactory = dbFactory;
        _mediator = mediator;
        _downloadQueue = downloadQueue;
        _normalizer = normalizer;
        _titleResolver = titleResolver;
        _releases = releases;
        _logger = logger;
        _families = families;
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

    public void Pause()  { _paused = true;  _logger.LogInformation("RSS Monitor PAUSED by user");  _ = PersistPausedAsync(true); }
    public void Resume() { _paused = false; _logger.LogInformation("RSS Monitor RESUMED by user"); _ = PersistPausedAsync(false); }

    /// <summary>
    /// Writes the paused flag so the choice survives a restart.
    ///
    /// This used to swallow every exception silently, which made a lost write
    /// indistinguishable from a working one: the pause held for the session, then
    /// the app came back up monitoring again with nothing in the log to explain it.
    /// SQLite write contention here is real — several services share this DB — so
    /// retry briefly and, if it still fails, say so loudly.
    /// </summary>
    private async Task PersistPausedAsync(bool paused)
    {
        var value = paused ? "true" : "false";

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var row = await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == PausedKey);
                if (row == null) db.AppConfigs.Add(new AppConfig { Key = PausedKey, Value = value });
                else row.Value = value;
                await db.SaveChangesAsync();

                _logger.LogInformation("RSS Monitor persisted {Key}={Value}", PausedKey, value);
                return;
            }
            catch (Exception ex) when (attempt < 3)
            {
                _logger.LogWarning(ex,
                    "RSS Monitor could not persist {Key} (attempt {Attempt}/3), retrying",
                    PausedKey, attempt);
                await Task.Delay(200 * attempt);
            }
            catch (Exception ex)
            {
                // Final failure — the session-local pause still holds, but the
                // setting will NOT survive a restart. Never hide this again.
                _logger.LogError(ex,
                    "RSS Monitor FAILED to persist {Key}={Value}. The pause applies to this " +
                    "session only and monitoring will resume after a restart.", PausedKey, value);
            }
        }
    }

    public async Task CheckSingleFeedAsync(int feedId, CancellationToken ct = default)
        => await RunCheckAsync(isManual: true, ct, singleFeedId: feedId);

    // ── Core Check Logic ───────────────────────────────────────────
    private async Task RunCheckAsync(bool isManual, CancellationToken ct, int? singleFeedId = null)
    {
        // Covers manual checks too (the tray's Check Now, a single feed): a check enqueues downloads.
        // Not persisted like a user pause, so the user's own choice is intact for next time.
        if (InstanceGuard.PausedForOtherInstance)
        {
            _logger.LogInformation("RSS check skipped — {Reason}", InstanceGuard.PausedMessage);
            return;
        }

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

            // Only series the user is actually monitoring. Turning monitoring off on a
            // series set MonitoringState.Paused and persisted it, but this query used to
            // load every series regardless — so paused shows kept matching feed items and
            // kept downloading, which is exactly what "stop monitoring does nothing" meant.
            var seriesList = await db.Series
                .Where(s => s.MonitoringState == MonitoringState.Active)
                .ToListAsync(ct);

            // Shows whose seasons aren't linked yet (just added, or gone stale) get linked in the
            // background — a few paced requests, never holding up the check.
            if (_families != null && _familyRefresh.IsCompleted)
            {
                var ids = await db.Series.Select(s => s.MalId).Where(id => id > 0).ToListAsync(ct);
                if (ids.Any(_families.NeedsFetch))
                    _familyRefresh = Task.Run(() => _families.RefreshAsync(ids, CancellationToken.None));
            }

            var pausedCount = await db.Series.CountAsync(s => s.MonitoringState != MonitoringState.Active, ct);
            if (pausedCount > 0)
                _logger.LogInformation("RSS check covering {Active} series, skipping {Paused} paused",
                    seriesList.Count, pausedCount);

            IQueryable<RssFeed> feedsQuery = db.RssFeeds.Where(f => f.IsEnabled);

            if (singleFeedId.HasValue)
                feedsQuery = db.RssFeeds.Where(f => f.Id == singleFeedId.Value);

            var feeds = await feedsQuery.ToListAsync(ct);

            // A feed that keeps failing waits longer between tries; asking by hand checks it anyway.
            if (!isManual && !singleFeedId.HasValue)
            {
                var now = DateTime.UtcNow;
                foreach (var resting in feeds.Where(f => !FeedBackoff.IsDue(f, now)))
                    _logger.LogInformation("Feed {Url} failed {Count} time(s) in a row — next try after {Wait}",
                        resting.Url, resting.ConsecutiveFailures, FeedBackoff.Wait(resting.ConsecutiveFailures));
                feeds = feeds.Where(f => FeedBackoff.IsDue(f, now)).ToList();
            }

            if (seriesList.Count == 0 || feeds.Count == 0)
            {
                _logger.LogInformation("Nothing to check — no series or matching feeds configured");
                await _mediator.Publish(
                    new MonitorStatusEvent(MonitorStatus.CheckCompleted, IsManual: isManual), ct);
                return;
            }

            var qualityPreference = await GetConfigValueAsync("QualityPreference", "1080p", ct);

            // Which groups the automatic downloads keep to (see ReleaseGroupPolicy).
            var groupRule = new GroupRule(
                ReleaseGroupPolicy.SplitGroups(await GetConfigValueAsync(ReleaseGroupPolicy.GroupsKey, "", ct)),
                ReleaseGroupPolicy.ParseMode(await GetConfigValueAsync(ReleaseGroupPolicy.ModeKey, "", ct)),
                ReleaseGroupPolicy.ReadSightings(await GetConfigValueAsync(ReleaseGroupPolicy.SightingsKey, "", ct)),
                _logger);

            var priorityFeeds = feeds.Where(f => f.FeedType == ModelFeedType.Priority).ToList();
            var secondaryFeeds = feeds.Where(f => f.FeedType == ModelFeedType.Secondary).ToList();

            var foundKeys = new HashSet<(int MalId, int EpisodeNumber)>();
            var newEpisodes = new List<NewEpisodeFoundEvent>();
            var pendingEpisodes = new List<NewEpisodeFoundEvent>();

            foreach (var feed in priorityFeeds)
            {
                // Stop pressed mid-check: bail now instead of finishing the sweep and
                // enqueuing episodes after the UI already says Idle. A manual check is
                // an explicit user action, so it is allowed to run to completion.
                if (_paused && !isManual)
                {
                    _logger.LogInformation("RSS check aborted — monitoring was paused mid-check");
                    break;
                }

                var quality = feed.PreferredQuality ?? qualityPreference;
                var found = await CheckFeedAsync(feed, seriesList, quality, foundKeys, groupRule, ct);
                foreach (var ep in found)
                {
                    var key = (ep.MalId, ep.EpisodeNumber);
                    if (foundKeys.Add(key))
                        newEpisodes.Add(ep with { IsSecondary = false });
                }
            }

            foreach (var feed in secondaryFeeds)
            {
                if (_paused && !isManual)
                {
                    _logger.LogInformation("RSS check aborted — monitoring was paused mid-check");
                    break;
                }

                var quality = feed.PreferredQuality ?? qualityPreference;
                var found = await CheckFeedAsync(feed, seriesList, quality, foundKeys, groupRule, ct);
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

                var earlyEp = await SearchForEarlyEpisodeAsync(series, qualityPreference, groupRule, ct);
                if (earlyEp != null)
                {
                    var key = (earlyEp.MalId, earlyEp.EpisodeNumber);
                    if (foundKeys.Add(key))
                    {
                        newEpisodes.Add(earlyEp);
                    }
                }
            }

            // "Prefer my groups": episodes no preferred group released within the wait. Their
            // release was kept when first seen — the feed has long moved past it by now.
            foreach (var (series, episode, sighting) in groupRule.WaitsOver(seriesList))
            {
                if (!foundKeys.Add((series.MalId, episode))) continue;
                _logger.LogInformation("No preferred group released {Title} Ep {Ep} within {Hours} h — taking {Group}",
                    series.Title, episode, ReleaseGroupPolicy.PreferWait.TotalHours, ExtractSourceGroup(sighting.Title));
                newEpisodes.Add(new NewEpisodeFoundEvent(series.MalId, series.Title, episode, sighting.Link, sighting.Title, IsSecondary: false));
            }
            foreach (var ep in newEpisodes.Concat(pendingEpisodes)) groupRule.Done(ep.MalId, ep.EpisodeNumber);
            groupRule.Forget(seriesList);
            if (groupRule.Changed)
                await SetConfigValueAsync(ReleaseGroupPolicy.SightingsKey, groupRule.SaveSightings(), ct);

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

                // Auto-download only when enabled AND the release is one the series' group rule
                // takes without asking: a preferred group, no list at all, or the fallback after the
                // "Prefer" wait (the wait was the user's consent). "Any group" still asks about others.
                var epGroup = ExtractSourceGroup(ep.RssTitle);
                var (seriesGroups, seriesMode) = groupRule.For(series);
                bool groupAllowed = seriesGroups.Count == 0
                                    || ReleaseGroupPolicy.IsPreferred(epGroup, seriesGroups)
                                    || seriesMode == GroupMode.Prefer;

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
        GroupRule groupRule,
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
                    var (matched, episodeNum) = MatchRelease(title, series);
                    if (!matched) continue;

                    bool isCompleted = series.AiringStatus == "Finished Airing" || series.AiringStatus == "Completed";
                    // Same strict rule as everywhere else — a space-padded "S2 - 04"
                    // is a single episode, not a batch.
                    bool isBatch = TitleResolverService.IsBatchRelease(title, 0);

                    if (isCompleted && !isBatch) continue; // Only accept batches for completed series

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

                    // The series' group rule: take it, wait for a preferred group, or leave it.
                    var decision = groupRule.Decide(series, episodeNum.Value, title, link, out var rank);
                    if (decision != GroupDecision.Take) break;
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

    /// <summary>
    /// One check's view of the release-group rule: the app's list and mode, each series' own,
    /// and the episodes waiting for a preferred group (persisted between checks).
    /// </summary>
    internal sealed class GroupRule(List<string> groups, GroupMode mode, Dictionary<string, GroupSighting> sightings, ILogger log)
    {
        private readonly DateTime _now = DateTime.UtcNow;
        public bool Changed { get; private set; }

        public (List<string> Groups, GroupMode Mode) For(Series series) => ReleaseGroupPolicy.For(series, groups, mode);

        public GroupDecision Decide(Series series, int episode, string title, string link, out int rank)
        {
            var (seriesGroups, seriesMode) = For(series);
            var group = ExtractSourceGroup(title);
            rank = ReleaseGroupPolicy.Rank(group, seriesGroups);
            var key = ReleaseGroupPolicy.Key(series.MalId, episode);
            sightings.TryGetValue(key, out var seen);
            var decision = ReleaseGroupPolicy.Decide(seriesMode, group, seriesGroups, seen?.FirstSeen, _now);
            if (decision == GroupDecision.Wait && seen == null)
            {
                sightings[key] = new GroupSighting(_now, title, link);
                Changed = true;
                log.LogInformation("{Title} Ep {Ep} is out from {Group}, not a preferred group — waiting until {Until:u} for one",
                    series.Title, episode, group, ReleaseGroupPolicy.WaitEnds(_now));
            }
            return decision;
        }

        /// <summary>Waiting episodes whose wait is over and that the series still needs.</summary>
        public IEnumerable<(Series Series, int Episode, GroupSighting Sighting)> WaitsOver(List<Series> seriesList)
        {
            foreach (var (key, sighting) in sightings.ToList())
            {
                if (_now - sighting.FirstSeen < ReleaseGroupPolicy.PreferWait) continue;
                var parts = key.Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var malId) || !int.TryParse(parts[1], out var ep)) continue;
                var series = seriesList.FirstOrDefault(s => s.MalId == malId);
                if (series == null || ep <= series.LastEpisodeNumber || For(series).Mode != GroupMode.Prefer) continue;
                yield return (series, ep, sighting);
            }
        }

        /// <summary>An episode is being downloaded: stop waiting for it.</summary>
        public void Done(int malId, int episode) => Changed |= sightings.Remove(ReleaseGroupPolicy.Key(malId, episode));

        /// <summary>Drops waits for episodes the series already has (or series no longer monitored).</summary>
        public void Forget(List<Series> seriesList)
        {
            foreach (var key in sightings.Keys.ToList())
            {
                var parts = key.Split(':');
                var series = parts.Length == 2 && int.TryParse(parts[0], out var malId) ? seriesList.FirstOrDefault(s => s.MalId == malId) : null;
                if (series == null || (int.TryParse(parts[1], out var ep) && ep <= series.LastEpisodeNumber))
                    Changed |= sightings.Remove(key);
            }
        }

        public string SaveSightings() => ReleaseGroupPolicy.WriteSightings(sightings, _now);
    }

    private async Task<NewEpisodeFoundEvent?> SearchForEarlyEpisodeAsync(Series series, string qualityPreference, GroupRule groupRule, CancellationToken ct)
    {
        // Needs a release provider, which only a loaded source pack supplies.
        if (!_releases.HasSearch) return null;

        try
        {
            var targetEp = Math.Max(1, series.LastEpisodeNumber + 1); // We look for 1 if it's -1 or 0
            var queries = new List<string> { $"{series.Title} {targetEp:D2}" };
            // A later season is often released under an earlier title, numbered straight through
            // ("Jujutsu Kaisen - 50", "Bleach - Sennen Kessen Hen - 48").
            foreach (var (title, absolute) in ReleaseMatcher.AbsoluteForms(_titleResolver, series, targetEp))
                queries.Insert(0, $"{title} {absolute:D2}");

            _logger.LogInformation("Performing targeted search for {Title} Ep {Ep}", series.Title, targetEp);
            var results = new List<ReleaseResult>();
            foreach (var query in queries)
                results.AddRange(await _releases.SearchAsync(query, quality: null, secretMode: false, ct));

            // Preferred groups first, then the rule decides (a non-preferred one may have to wait).
            var seriesGroups = groupRule.For(series).Groups;
            foreach (var result in results.OrderBy(r => ReleaseGroupPolicy.Rank(ExtractSourceGroup(r.Title), seriesGroups)))
            {
                var title = result.Title;
                var link = result.DownloadLink;

                if (!MatchesQuality(title, qualityPreference)) continue;
                var (matched, episodeNum) = MatchRelease(title, series);
                if (!matched) continue;
                if (episodeNum == null || episodeNum <= series.LastEpisodeNumber) continue;
                if (groupRule.Decide(series, episodeNum.Value, title, link, out _) != GroupDecision.Take) continue;

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
                if (feed.ConsecutiveFailures >= FeedBackoff.NoticeAfter)
                    _logger.LogInformation("Feed answering again after {Count} failed checks: {Url}", feed.ConsecutiveFailures, feed.Url);
                feed.ConsecutiveFailures = 0;
                feed.LastError = null;
                feed.LastSuccessAt = DateTime.UtcNow;
            }
            else
            {
                feed.ConsecutiveFailures++;
                feed.LastError = error;

                // Never switched off (see FeedBackoff): said once, then tried less often until it's back.
                if (feed.ConsecutiveFailures == FeedBackoff.NoticeAfter)
                {
                    _logger.LogWarning("Feed failing for a while, now checked less often: {Url} — {Error}", feed.Url, error);
                    await _mediator.Publish(new MonitorStatusEvent(
                        MonitorStatus.Error,
                        ErrorMessage: $"{new Uri(feed.Url).Host} hasn't answered properly for a while. Sentrychan keeps trying, less often, and picks it up again once it's back."), ct);
                }
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update feed health for feed {Id}", feedId);
        }
    }

    /// <summary>
    /// Whether a release is an episode of the series, and which one in the series' own numbering.
    /// Primary: the offline database's MAL ids, cour-aware (<see cref="ReleaseMatcher"/>) — a
    /// release resolving to a different show is a definite no, which is what stops "Season 2"
    /// releases matching the Season 1 entry. Fallback when the release can't be identified: titles.
    /// </summary>
    private (bool Matched, int? Episode) MatchRelease(string rssTitle, Series series)
    {
        var (verdict, episode) = ReleaseMatcher.Match(_titleResolver, rssTitle, series);
        return verdict switch
        {
            ReleaseVerdict.Yes => (true, episode),
            ReleaseVerdict.No => (false, null),
            _ => (TitleMatchesSeries(rssTitle, series), ExtractEpisodeNumber(rssTitle)),
        };
    }

    private bool TitleMatchesSeries(string rssTitle, Series series)
    {
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

    private async Task SetConfigValueAsync(string key, string value, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var row = await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == key, ct);
            if (row == null) db.AppConfigs.Add(new AppConfig { Key = key, Value = value });
            else row.Value = value;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Couldn't save {Key}", key);
        }
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
