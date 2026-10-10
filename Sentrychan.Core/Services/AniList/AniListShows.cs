using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.Core.Services.AniList;

/// <summary>
/// Finds the AniList show behind a MyAnimeList id — the id the library is keyed on. The offline
/// database knows most links; AniList's own <c>idMal</c> covers the rest, except where it's wrong
/// (it gives a split show's later parts the first part's id) or missing. Those are found by a title
/// search checked against the show's names, and remembered in <c>anilist-links.json</c>.
/// </summary>
public sealed class AniListShows
{
    private static readonly TimeSpan Remember = TimeSpan.FromMinutes(15);

    private readonly AniListClient _client;
    private readonly ITitleResolverService _resolver;
    private readonly ILogger<AniListShows> _log;
    private readonly string _file;
    private readonly object _gate = new();
    private Dictionary<int, int> _learned = new();
    private readonly ConcurrentDictionary<int, (AniListMedia Media, DateTime At)> _recent = new();

    public AniListShows(AniListClient client, ITitleResolverService resolver, ILogger<AniListShows> log, string? file = null)
    {
        _client = client;
        _resolver = resolver;
        _log = log;
        _file = file ?? AppPaths.Combine("anilist-links.json");
        Load();
    }

    public AniListClient Client => _client;

    /// <summary>The AniList id for a MyAnimeList id, when it's known without asking AniList.</summary>
    public int? AniListIdFor(int malId)
    {
        lock (_gate) if (_learned.TryGetValue(malId, out var id)) return id;
        return _resolver.AniListIdForMal(malId);
    }

    /// <summary>The checked MyAnimeList id of an AniList show; null when it has none that can be trusted.</summary>
    public int? MalIdFor(AniListMedia m)
    {
        lock (_gate)
            foreach (var (mal, al) in _learned)
                if (al == m.Id) return mal;
        return _resolver.MalIdFor(m);
    }

    public int? MalIdFor(AniListRelationNode node) => MalIdFor(new AniListMedia { Id = node.Id, IdMal = node.IdMal });

    public async Task<AniListMedia?> ForMalIdAsync(int malId, string? title, bool background = false, CancellationToken ct = default)
    {
        if (_recent.TryGetValue(malId, out var hit) && DateTime.UtcNow - hit.At < Remember) return hit.Media;
        var found = await ForMalIdsAsync([(malId, title)], background, ct);
        return found.GetValueOrDefault(malId);
    }

    /// <summary>
    /// The AniList show for each of these, by MyAnimeList id; a show it can't place is left out.
    /// One request per 50 shows, plus a search for each one AniList doesn't link.
    /// </summary>
    public async Task<Dictionary<int, AniListMedia>> ForMalIdsAsync(IReadOnlyCollection<(int MalId, string? Title)> shows,
        bool background = false, CancellationToken ct = default)
    {
        var result = new Dictionary<int, AniListMedia>();
        var wanted = shows.Where(s => s.MalId > 0).DistinctBy(s => s.MalId).ToList();

        // 1. Links already known: fetch by AniList id.
        var byLink = wanted.Select(s => (s.MalId, Id: AniListIdFor(s.MalId))).Where(x => x.Id != null).ToList();
        if (byLink.Count > 0)
        {
            var media = (await _client.ByIdsAsync(byLink.Select(x => x.Id!.Value).ToList(), background, ct)).ToDictionary(m => m.Id);
            foreach (var (mal, id) in byLink)
                if (media.TryGetValue(id!.Value, out var m)) result[mal] = m;
        }

        // 2. By AniList's own MyAnimeList id, where it holds up.
        var rest = wanted.Where(s => !result.ContainsKey(s.MalId)).ToList();
        if (rest.Count > 0)
        {
            var media = await _client.ByMalIdsAsync(rest.Select(s => s.MalId).ToList(), background, ct);
            foreach (var group in media.Where(m => m.IdMal is > 0).GroupBy(m => m.IdMal!.Value))
            {
                var trusted = group.Where(m => MalIdFor(m) == group.Key).ToList();
                if (trusted.Count == 1) result[group.Key] = trusted[0];
            }
        }

        // 3. Still missing: a title search, checked against the show's names.
        foreach (var (mal, title) in wanted.Where(s => !result.ContainsKey(s.MalId)))
        {
            var name = title ?? _resolver.GetByMalId(mal)?.CanonicalTitle;
            if (string.IsNullOrWhiteSpace(name)) continue;
            try
            {
                var candidates = await _client.SearchAsync(SeasonDetector.ExtractBaseTitle(name) is { Length: > 2 } b ? b : name, 10, null, background, ct);
                if (PickByName(mal, name, candidates) is { } m)
                {
                    Learn(mal, m.Id);
                    result[mal] = m;
                    _log.LogInformation("[AniList] {Title}: linked to AniList {Id} by name", name, m.Id);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogDebug("[AniList] search for {Title} failed: {Message}", name, ex.Message); }
        }

        foreach (var (mal, m) in result) _recent[mal] = (m, DateTime.UtcNow);
        return result;
    }

    /// <summary>
    /// The search result that is this show: AniList links it to no MyAnimeList id it can be trusted
    /// with (none, or one another show holds), its names share most of their words with the
    /// show's, and no season number disagrees. Clearly better than the runner-up.
    /// </summary>
    internal AniListMedia? PickByName(int malId, string title, IEnumerable<AniListMedia> candidates)
    {
        var offline = _resolver.GetByMalId(malId);
        var names = new[] { title, offline?.CanonicalTitle }.OfType<string>().Distinct().ToList();
        var ranked = candidates
            .Where(c => MalIdFor(c) is not { } trusted || trusted == malId)
            .Where(c => offline?.Year is not { } y || c.Year is not { } cy || Math.Abs(y - cy) <= 1)
            .Select(c => (Media: c, Score: c.AllTitles().SelectMany(t => names.Select(n => Similarity(n, t))).DefaultIfEmpty(0).Max()))
            .OrderByDescending(x => x.Score)
            .Take(2)
            .ToList();
        if (ranked.Count == 0 || ranked[0].Score < 0.75) return null;
        if (ranked.Count > 1 && ranked[1].Score > ranked[0].Score - 0.1) return null;
        return ranked[0].Media;
    }

    /// <summary>Shared words over all words (0–1); 0 when the names state different seasons.</summary>
    internal static double Similarity(string a, string b)
    {
        var na = TitleResolverService.Normalize(a);
        var nb = TitleResolverService.Normalize(b);
        if (SeasonDetector.DetectSeason(na) != SeasonDetector.DetectSeason(nb)) return 0;
        var ta = na.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var tb = nb.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (ta.Count == 0 || tb.Count == 0) return 0;
        var common = ta.Count(tb.Contains);
        return (double)common / (ta.Count + tb.Count - common);
    }

    private void Learn(int malId, int aniListId)
    {
        lock (_gate) _learned[malId] = aniListId;
        try
        {
            Dictionary<int, int> copy;
            lock (_gate) copy = new(_learned);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(copy));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex) { _log.LogWarning("[AniList] couldn't save {File}: {Message}", _file, ex.Message); }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_file))
                _learned = JsonSerializer.Deserialize<Dictionary<int, int>>(File.ReadAllText(_file)) ?? new();
        }
        catch (Exception ex) { _log.LogWarning("[AniList] couldn't read {File}: {Message}", _file, ex.Message); }
    }
}
