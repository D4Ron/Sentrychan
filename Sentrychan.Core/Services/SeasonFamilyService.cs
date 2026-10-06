using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models.Api;

namespace Sentrychan.Core.Services;

/// <summary>
/// A show's seasons as MAL links them: every entry reachable through "Sequel" and "Prequel"
/// relations. Release groups often count straight through all of them under the first title
/// ("Jujutsu Kaisen - 50" is the third season's episode 3), and the seasons' titles may share
/// nothing, so grouping by title (the fallback) misses them. Fetched once per show, paced for the
/// API's rate limit, and kept on disk; the resolver reads it through <see cref="TitleResolverService.Families"/>.
/// </summary>
public sealed class SeasonFamilyService
{
    /// <summary>A family is given up on past this many linked entries (long franchises link dozens of films).</summary>
    public const int MaxMembers = 40;

    private static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(1100);
    private static readonly TimeSpan StaleWhileAiring = TimeSpan.FromDays(3);
    private static readonly TimeSpan StaleWhenFinished = TimeSpan.FromDays(60);

    private readonly Func<int, CancellationToken, Task<List<AnimeRelation>?>> _relations;
    private readonly ITitleResolverService _resolver;
    private readonly ILogger _log;
    private readonly string _file;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _fetching = new(1, 1);
    private Dictionary<int, FamilyRecord> _byMember = new();

    public sealed record FamilyRecord(List<int> Members, DateTime FetchedUtc, bool Complete);

    public SeasonFamilyService(
        Func<int, CancellationToken, Task<List<AnimeRelation>?>> relations,
        ITitleResolverService resolver,
        ILogger<SeasonFamilyService> log,
        string? file = null)
    {
        _relations = relations;
        _resolver = resolver;
        _log = log;
        _file = file ?? AppPaths.Combine("season-families.json");
        Load();
        if (resolver is TitleResolverService concrete) concrete.Families = FamilyOf;
    }

    /// <summary>The family's MAL ids (any type, airing order not implied), or null when not known.</summary>
    public IReadOnlyList<int>? FamilyOf(int malId)
    {
        lock (_gate) return _byMember.TryGetValue(malId, out var f) && f.Complete ? f.Members : null;
    }

    /// <summary>Fetches the families of these shows that aren't known or have gone stale. One request at a time.</summary>
    public async Task RefreshAsync(IEnumerable<int> malIds, CancellationToken ct = default)
    {
        foreach (var id in malIds.Where(i => i > 0).Distinct())
        {
            if (!NeedsFetch(id)) continue;
            try { await EnsureAsync(id, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogWarning("[Seasons] couldn't link the seasons of {MalId}: {Message}", id, ex.Message); }
        }
    }

    public bool NeedsFetch(int malId)
    {
        lock (_gate)
        {
            if (!_byMember.TryGetValue(malId, out var f)) return true;
            var airing = f.Members.Any(m => _resolver.GetByMalId(m) is { Status: not "FINISHED" });
            return DateTime.UtcNow - f.FetchedUtc > (airing ? StaleWhileAiring : StaleWhenFinished);
        }
    }

    /// <summary>Walks the sequel/prequel links from one entry and stores the family it finds.</summary>
    public async Task<FamilyRecord?> EnsureAsync(int malId, CancellationToken ct = default)
    {
        await _fetching.WaitAsync(ct);
        try
        {
            if (!NeedsFetch(malId)) lock (_gate) return _byMember[malId];

            var seen = new HashSet<int> { malId };
            var queue = new Queue<int>([malId]);
            var complete = true;
            while (queue.Count > 0)
            {
                if (seen.Count > MaxMembers) { complete = false; break; }
                var id = queue.Dequeue();
                var links = await FetchAsync(id, ct);
                if (links == null) return null; // the API failed: try another time, keep what was known
                foreach (var entry in links
                             .Where(r => r.Relation is "Sequel" or "Prequel")
                             .SelectMany(r => r.Entry)
                             .Where(e => e.Type == "anime" && e.MalId > 0))
                    if (seen.Add(entry.MalId)) queue.Enqueue(entry.MalId);
            }

            var record = new FamilyRecord(seen.OrderBy(i => i).ToList(), DateTime.UtcNow, complete);
            lock (_gate) foreach (var m in record.Members) _byMember[m] = record;
            Save();
            _log.LogInformation("[Seasons] {Title}: {Count} linked entries{Note}",
                _resolver.GetByMalId(malId)?.CanonicalTitle ?? malId.ToString(), record.Members.Count,
                complete ? "" : " (too many — titles decide instead)");
            return record;
        }
        finally { _fetching.Release(); }
    }

    private async Task<List<AnimeRelation>?> FetchAsync(int malId, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await Task.Delay(Pace * attempt, ct);
            try { return await _relations(malId, ct) ?? []; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (attempt < 3)
            {
                _log.LogDebug("[Seasons] relations of {MalId} failed (attempt {Attempt}): {Message}", malId, attempt, ex.Message);
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Seasons] relations of {MalId} unavailable: {Message}", malId, ex.Message);
            }
        }
        return null;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var records = JsonSerializer.Deserialize<List<FamilyRecord>>(File.ReadAllText(_file)) ?? [];
            lock (_gate) foreach (var r in records) foreach (var m in r.Members) _byMember[m] = r;
        }
        catch (Exception ex) { _log.LogWarning("[Seasons] couldn't read {File}: {Message}", _file, ex.Message); }
    }

    private void Save()
    {
        try
        {
            List<FamilyRecord> records;
            lock (_gate) records = _byMember.Values.Distinct().ToList();
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(records));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex) { _log.LogWarning("[Seasons] couldn't save {File}: {Message}", _file, ex.Message); }
    }
}
