using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.Core.Services;

/// <summary>
/// The app's schedule. Uses a source-pack schedule when one is loaded and returns
/// something; otherwise falls back to MAL broadcast data, so the Airing Today panel
/// works on a build with no pack installed.
/// </summary>
public sealed class AiringScheduleRouter : IAiringScheduleService, IAiringScheduleRegistry
{
    private readonly JikanAiringScheduleService _fallback;
    private readonly ILogger<AiringScheduleRouter> _logger;
    private readonly List<IAiringScheduleService> _providers = [];
    private readonly object _gate = new();

    public AiringScheduleRouter(JikanAiringScheduleService fallback, ILogger<AiringScheduleRouter> logger)
    {
        _fallback = fallback;
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
        return await _fallback.GetTodayAsync(ct);
    }
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
