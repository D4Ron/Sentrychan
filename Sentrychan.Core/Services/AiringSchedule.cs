using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.Core.Services;

/// <summary>
/// The app's schedule. Uses a source-pack schedule when one is loaded and returns
/// something; otherwise AniList's airing times, then MAL broadcast data, so the Airing
/// Today panel works on a build with no pack installed.
/// </summary>
public sealed class AiringScheduleRouter : IAiringScheduleService, IAiringScheduleRegistry
{
    private readonly JikanAiringScheduleService _fallback;
    private readonly AniListAiringScheduleService? _aniList;
    private readonly ILogger<AiringScheduleRouter> _logger;
    private readonly List<IAiringScheduleService> _providers = [];
    private readonly object _gate = new();

    public AiringScheduleRouter(JikanAiringScheduleService fallback, ILogger<AiringScheduleRouter> logger,
        AniListAiringScheduleService? aniList = null)
    {
        _fallback = fallback;
        _aniList = aniList;
        _logger = logger;
    }

    public void Add(IAiringScheduleService provider)
    {
        if (ReferenceEquals(provider, this)) return;
        lock (_gate)
            if (!_providers.Contains(provider)) _providers.Add(provider);
    }

    public async Task<List<AiringScheduleEntry>> GetTodayAsync(CancellationToken ct = default)
    {
        IAiringScheduleService[] providers;
        lock (_gate) providers = _providers.ToArray();

        foreach (var p in providers)
        {
            try
            {
                var entries = await p.GetTodayAsync(ct);
                if (entries.Count > 0) return entries;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Schedule] source-pack schedule failed; falling back to MAL");
            }
        }
        if (_aniList != null && await _aniList.GetTodayAsync(ct) is { Count: > 0 } fromAniList) return fromAniList;
        return await _fallback.GetTodayAsync(ct);
    }
}

/// <summary>
/// Today's airings from AniList: the actual time of each episode, already in UTC, so the local
/// day is simply asked for — no JST weekday arithmetic. The most-followed shows only, as MAL's
/// schedule was (it listed 25 a day); adult ones never.
/// </summary>
public sealed class AniListAiringScheduleService : IAiringScheduleService
{
    private const int MaxShows = 30;

    private readonly AniList.AniListShows _shows;
    private readonly IDbContextFactory<Data.AppDbContext>? _dbFactory;
    private readonly ILogger<AniListAiringScheduleService> _logger;
    private List<AiringScheduleEntry>? _cache;
    private DateTime _cacheDay = DateTime.MinValue;
    private DateTime _cachedAt = DateTime.MinValue;
    private readonly SemaphoreSlim _fetchGate = new(1, 1);

    public AniListAiringScheduleService(AniList.AniListShows shows, ILogger<AniListAiringScheduleService> logger,
        IDbContextFactory<Data.AppDbContext>? dbFactory = null)
    {
        _shows = shows;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<List<AiringScheduleEntry>> GetTodayAsync(CancellationToken ct = default)
    {
        if (Cached() is { } hit) return hit;
        await _fetchGate.WaitAsync(ct);
        try
        {
            if (Cached() is { } again) return again;
            var today = DateTime.Today;
            var from = new DateTimeOffset(today);
            var airings = await _shows.Client.AiringBetweenAsync(from, from.AddDays(1), ct);
            var library = await LibraryMalIdsAsync(ct);
            var entries = airings
                .Where(a => a.Media is { IsAdult: false })
                .GroupBy(a => a.Media!.Id).Select(g => g.First())
                .Select(a => (At: DateTimeOffset.FromUnixTimeSeconds(a.AiringAt).ToLocalTime(), Airing: a, Mal: _shows.MalIdFor(a.Media!) ?? 0))
                // Everything airs on AniList's schedule — five-minute shorts, and the Chinese
                // schedule as well as the Japanese one; MAL's listed the Japanese season. Kept
                // either way when it's in the library.
                .Where(x => library.Contains(x.Mal) || (x.Airing.Media!.CountryOfOrigin is null or "JP" && x.Airing.Media.Format != "TV_SHORT"))
                .OrderByDescending(x => library.Contains(x.Mal))
                .ThenByDescending(x => x.Airing.Media!.Popularity ?? 0)
                .Take(MaxShows)
                .OrderBy(x => x.At)
                .Select(x => new AiringScheduleEntry(
                    x.Airing.Media!.Title.Romaji ?? x.Airing.Media.Title.English ?? "",
                    x.Airing.Media.CoverImage?.Large ?? "",
                    x.At.ToString("HH:mm"),
                    x.Mal > 0 ? $"https://myanimelist.net/anime/{x.Mal}" : $"https://anilist.co/anime/{x.Airing.Media.Id}",
                    x.Mal))
                .ToList();
            if (entries.Count > 0)
            {
                _cache = entries;
                _cacheDay = today;
                _cachedAt = DateTime.UtcNow;
            }
            _logger.LogInformation("[Schedule] AniList: {Count} shows airing {Day}", entries.Count, today.DayOfWeek);
            return entries;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("[Schedule] AniList schedule unavailable: {Message}", ex.Message);
            return [];
        }
        finally { _fetchGate.Release(); }
    }

    /// <summary>The library's shows always make the list, however few people follow them.</summary>
    private async Task<HashSet<int>> LibraryMalIdsAsync(CancellationToken ct)
    {
        if (_dbFactory == null) return [];
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            return (await db.Series.Where(s => s.MalId > 0).Select(s => s.MalId).ToListAsync(ct)).ToHashSet();
        }
        catch { return []; }
    }

    private List<AiringScheduleEntry>? Cached() =>
        _cache != null && _cacheDay == DateTime.Today && DateTime.UtcNow - _cachedAt < TimeSpan.FromMinutes(30) ? _cache : null;
}

/// <summary>
/// Today's airings from MAL broadcast data (Jikan /schedules). Broadcast times are in JST,
/// so a user's local day can straddle two JST weekdays: fetch both, convert each airing to
/// local time, and keep only those that land on the local date.
/// </summary>
public sealed class JikanAiringScheduleService : IAiringScheduleService
{
    private readonly IAnimeApiService _api;
    private readonly ILogger<JikanAiringScheduleService> _logger;

    // The schedule barely changes within a session.
    private List<AiringScheduleEntry>? _cache;
    private DateTime _cacheDay = DateTime.MinValue;
    private DateTime _cachedAt = DateTime.MinValue;

    // The first call lands in the startup burst, where Jikan often answers 429 and the shared
    // client's circuit breaker opens for 30s. An empty answer is therefore treated as "try
    // again" rather than "nothing airs today": it is never cached, and is retried past the
    // breaker window. One fetch at a time, so the library reloading repeatedly doesn't
    // multiply requests against an already rate-limited API.
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(35), TimeSpan.FromSeconds(70)];
    private readonly SemaphoreSlim _fetchGate = new(1, 1);

    public JikanAiringScheduleService(IAnimeApiService api, ILogger<JikanAiringScheduleService> logger)
    {
        _api = api;
        _logger = logger;
    }

    public async Task<List<AiringScheduleEntry>> GetTodayAsync(CancellationToken ct = default)
    {
        if (TryCached(out var hit)) return hit;

        await _fetchGate.WaitAsync(ct);
        try
        {
            // Another caller may have filled the cache while this one waited.
            if (TryCached(out hit)) return hit;

            var entries = await FetchTodayAsync(ct);
            for (var i = 0; entries.Count == 0 && i < RetryDelays.Length; i++)
            {
                _logger.LogInformation("[Schedule] MAL returned nothing; retrying in {Delay}s", RetryDelays[i].TotalSeconds);
                await Task.Delay(RetryDelays[i], ct);
                entries = await FetchTodayAsync(ct);
            }

            if (entries.Count > 0)
            {
                _cache = entries;
                _cacheDay = DateTime.Today;
                _cachedAt = DateTime.UtcNow;
            }
            _logger.LogInformation("[Schedule] MAL: {Count} shows airing {Day}", entries.Count, DateTime.Today.DayOfWeek);
            return entries;
        }
        finally { _fetchGate.Release(); }
    }

    private bool TryCached(out List<AiringScheduleEntry> entries)
    {
        entries = _cache ?? [];
        return _cache != null && _cacheDay == DateTime.Today
            && DateTime.UtcNow - _cachedAt < TimeSpan.FromMinutes(30);
    }

    private async Task<List<AiringScheduleEntry>> FetchTodayAsync(CancellationToken ct)
    {
        try
        {
            var jst = ResolveJst();
            var localDay = DateTime.Today;
            var jstFirst = TimeZoneInfo.ConvertTime(localDay, TimeZoneInfo.Local, jst).Date;
            var jstLast  = TimeZoneInfo.ConvertTime(localDay.AddDays(1).AddTicks(-1), TimeZoneInfo.Local, jst).Date;

            var found = new List<(DateTime At, AiringScheduleEntry Entry)>();
            var seen = new HashSet<int>();

            for (var jstDay = jstFirst; jstDay <= jstLast; jstDay = jstDay.AddDays(1))
            {
                var shows = await _api.GetScheduleAsync(jstDay.DayOfWeek.ToString().ToLowerInvariant(), ct);
                foreach (var show in shows)
                {
                    if (show.Broadcast?.Time is not { } time || !TimeSpan.TryParse(time, out var timeOfDay))
                        continue;

                    var airJst = DateTime.SpecifyKind(jstDay + timeOfDay, DateTimeKind.Unspecified);
                    var airLocal = TimeZoneInfo.ConvertTime(airJst, jst, TimeZoneInfo.Local);
                    if (airLocal.Date != localDay || !seen.Add(show.MalId)) continue;

                    found.Add((airLocal, new AiringScheduleEntry(
                        show.Title,
                        show.LargeImageUrl,
                        airLocal.ToString("HH:mm"),
                        show.MalId > 0 ? $"https://myanimelist.net/anime/{show.MalId}" : string.Empty,
                        show.MalId)));
                }
            }

            return found.OrderBy(f => f.At).Select(f => f.Entry).ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Schedule] MAL schedule fetch failed");
            return [];
        }
    }

    /// <summary>JST, by IANA id where ICU is available, else by Windows id, else a fixed +9.</summary>
    private static TimeZoneInfo ResolveJst()
    {
        foreach (var id in new[] { "Asia/Tokyo", "Tokyo Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* try the next id */ }
        }
        return TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");
    }
}
