using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Sentrychan.Core.Services.AniList;

/// <summary>
/// AniList's shows since the offline database's snapshot: everything that started from a season
/// before it, everything still airing, and what finished since. About a thousand shows, ~25
/// requests, fetched in the background at most twice a day and kept on disk so the title index
/// has them from the first second of the next start.
/// </summary>
public sealed class AniListCatalog
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(20);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly AniListClient _client;
    private readonly ILogger<AniListCatalog> _log;
    private readonly string _file;
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private Snapshot? _snapshot;

    public sealed record Snapshot(DateTime FetchedUtc, DateTime Since, List<AniListMedia> Items);

    public AniListCatalog(AniListClient client, ILogger<AniListCatalog> log, string? file = null)
    {
        _client = client;
        _log = log;
        _file = file ?? AppPaths.Combine("anilist-catalog.json");
        Load();
    }

    public IReadOnlyList<AniListMedia> Items => _snapshot?.Items ?? [];
    public DateTime? FetchedUtc => _snapshot?.FetchedUtc;

    public bool IsStale(DateTime since) =>
        _snapshot == null || _snapshot.Since != since.Date || DateTime.UtcNow - _snapshot.FetchedUtc > MaxAge;

    /// <summary>Fetches a new copy when the one held is stale. True when it did.</summary>
    /// <param name="unsettled">
    /// AniList ids the database had as airing or upcoming when it froze, whatever their dates (it
    /// lagged: a one-episode special from March was still "ongoing" in July). One that has since
    /// finished is carried over from the last copy instead of being asked for again.
    /// </param>
    public async Task<bool> RefreshAsync(DateTime since, IReadOnlyCollection<int>? unsettled = null, CancellationToken ct = default)
    {
        if (!IsStale(since)) return false;
        await _refreshing.WaitAsync(ct);
        try
        {
            if (!IsStale(since)) return false;
            var from = int.Parse(since.ToString("yyyyMMdd"));
            var byId = new Dictionary<int, AniListMedia>();
            void Add(IEnumerable<AniListMedia> items) { foreach (var m in items) byId[m.Id] = m; }

            Add(await _client.PagesAsync("type: ANIME, startDate_greater: $from, sort: ID", ", $from: FuzzyDateInt",
                new() { ["from"] = from }, AniListClient.CatalogFields, 60, background: true, ct));
            Add(await _client.PagesAsync("type: ANIME, status: RELEASING, sort: ID", "",
                new(), AniListClient.CatalogFields, 30, background: true, ct));
            Add(await _client.PagesAsync("type: ANIME, status: FINISHED, endDate_greater: $from, startDate_lesser: $from, sort: ID",
                ", $from: FuzzyDateInt", new() { ["from"] = from }, AniListClient.CatalogFields, 20, background: true, ct));

            if (unsettled is { Count: > 0 })
            {
                var previous = (_snapshot?.Items ?? []).ToDictionary(m => m.Id);
                var ask = new List<int>();
                foreach (var id in unsettled.Where(id => !byId.ContainsKey(id)))
                {
                    if (previous.TryGetValue(id, out var settled) && settled.Status is "FINISHED" or "CANCELLED") byId[id] = settled;
                    else ask.Add(id);
                }
                Add(await _client.ByIdsAsync(ask, background: true, ct, AniListClient.CatalogFields));
            }

            _snapshot = new Snapshot(DateTime.UtcNow, since.Date, byId.Values.OrderBy(m => m.Id).ToList());
            Save();
            _log.LogInformation("[AniList] {Count} shows since {Since:yyyy-MM-dd} fetched", _snapshot.Items.Count, since);
            return true;
        }
        finally { _refreshing.Release(); }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_file))
                _snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_file), Json);
        }
        catch (Exception ex) { _log.LogWarning("[AniList] couldn't read {File}: {Message}", _file, ex.Message); }
    }

    private void Save()
    {
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_snapshot));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex) { _log.LogWarning("[AniList] couldn't save {File}: {Message}", _file, ex.Message); }
    }
}
