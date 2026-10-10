using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Services.AniList;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Sentrychan.Core.Services;

/// <summary>
/// Recognition engine. Two pillars:
///  1. AnitomySharp — a real anime-release tokenizer (title/episode/season/group)
///     instead of homegrown regexes.
///  2. manami-project anime-offline-database — ~40k anime with EVERY synonym,
///     cross-linked MAL ids and poster URLs. Downloaded once, indexed in memory:
///     exact normalized-synonym match first, fuzzy fallback.
///  3. AniList, for everything after the database: its project was archived in 2026 (last
///     update 4 July), so shows that started later were missing or stuck as "upcoming" with no
///     length. <see cref="AniListCatalog"/> fetches what's aired or announced since, daily, and
///     <see cref="ApplySupplement"/> merges it in.
/// Resolution is fully offline and instant after the first download.
/// </summary>
public class TitleResolverService : ITitleResolverService
{
    // The repository's own copy has been gone since the files moved to releases (2025).
    private const string DatabaseUrl =
        "https://github.com/manami-project/anime-offline-database/releases/latest/download/anime-offline-database-minified.json";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(7);
    private static readonly TimeSpan CatalogInterval = TimeSpan.FromHours(12);

    private readonly ILogger<TitleResolverService> _logger;
    private readonly HttpClient _http;
    private string _dbFilePath;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly AniListCatalog? _catalog;
    private readonly object _supplementLock = new();

    // AniList ↔ MyAnimeList, from the database's sources and then from merged AniList data.
    private Dictionary<int, int> _aniListByMal = new();
    private Dictionary<int, int> _malByAniList = new();
    private Dictionary<int, int> _entryByAniList = new();

    /// <summary>The offline database's last update: AniList fills in from a while before it.</summary>
    public DateTime? SnapshotDate { get; private set; }

    /// <summary>AniList ids of the entries the database still had as airing, upcoming or unknown when it froze.</summary>
    public IReadOnlyCollection<int> UnsettledAniListIds { get; private set; } = [];

    // normalized title/synonym → entry index
    private Dictionary<string, int> _exactIndex = new();
    // parallel arrays for fuzzy scan
    private List<OfflineAnimeEntry> _entries = [];
    private Dictionary<int, int> _byMalId = new();
    // Tokens are small distinct arrays of shared strings: a HashSet per key cost ~40 MB for
    // sets of three or four words.
    private List<(string Key, int EntryIdx, string[] Tokens)> _fuzzyKeys = [];
    // MAL id → the entries of its show in airing order (only shows MAL splits into several).
    private Dictionary<int, int[]> _chainByMalId = new();

    // per-session resolution memo (positive AND negative results)
    private readonly ConcurrentDictionary<string, ResolvedAnime?> _resolveCache = new(StringComparer.OrdinalIgnoreCase);

    public bool IsReady { get; private set; }

    public TitleResolverService(ILogger<TitleResolverService> logger, AniListCatalog? catalog = null)
    {
        _logger = logger;
        _catalog = catalog;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Sentrychan/2.0");

        var appData = AppPaths.EnsureDataDir();
        _dbFilePath = Path.Combine(appData, "anime-offline-database-min.json");
    }

    // ── Initialization ────────────────────────────────────────────

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (IsReady) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (IsReady) return;

            await EnsureDatabaseFileAsync(ct);
            if (!File.Exists(_dbFilePath))
            {
                _logger.LogWarning("[TitleResolver] Offline database unavailable — resolver disabled (consumers fall back to legacy matching)");
                return;
            }

            // Parse + index off-thread; ~40k entries with all synonyms
            await Task.Run(() => LoadAndIndex(), ct);

            IsReady = true;
            _logger.LogInformation(
                "[TitleResolver] Ready — {Entries} anime, {Keys} title keys indexed (database of {Date:yyyy-MM-dd})",
                _entries.Count, _exactIndex.Count, SnapshotDate);

            if (_catalog != null)
            {
                if (_catalog.Items.Count > 0) ApplySupplement(_catalog.Items);
                _ = Task.Run(() => KeepCatalogFreshAsync(CancellationToken.None));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TitleResolver] Initialization failed");
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task EnsureDatabaseFileAsync(CancellationToken ct)
    {
        var info = new FileInfo(_dbFilePath);
        bool fresh = info.Exists && DateTime.UtcNow - info.LastWriteTimeUtc < RefreshInterval
                     && info.Length > 1_000_000; // sanity: a truncated file is stale

        if (fresh) return;

        try
        {
            using var response = await _http.GetAsync(DatabaseUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode)
            {
                // The project is archived: the file hasn't changed in months. Same size → the copy
                // here is it; don't fetch 60 MB again to find out.
                if (info.Exists && response.Content.Headers.ContentLength == info.Length)
                {
                    File.SetLastWriteTimeUtc(_dbFilePath, DateTime.UtcNow);
                    return;
                }

                _logger.LogInformation("[TitleResolver] Downloading offline database: {Url}", DatabaseUrl);
                var tmpPath = _dbFilePath + ".tmp";
                await using (var fs = File.Create(tmpPath))
                {
                    await response.Content.CopyToAsync(fs, ct);
                }

                var size = new FileInfo(tmpPath).Length;
                if (size >= 1_000_000)
                {
                    File.Move(tmpPath, _dbFilePath, overwrite: true);
                    _logger.LogInformation("[TitleResolver] Database downloaded ({Mb:F1} MB)", size / 1_048_576.0);
                    return;
                }
                File.Delete(tmpPath);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TitleResolver] Download failed from {Url}", DatabaseUrl);
        }

        if (info.Exists)
            _logger.LogInformation("[TitleResolver] Using stale cached database from {Date}", info.LastWriteTimeUtc);
    }

    private void LoadAndIndex()
    {
        using var stream = File.OpenRead(_dbFilePath);
        var root = JsonSerializer.Deserialize<OfflineDatabaseRoot>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        if (root?.Data == null || root.Data.Count == 0)
            throw new InvalidOperationException("Offline database parsed empty");

        SnapshotDate = DateTime.TryParse(root.LastUpdate, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var updated)
            ? updated : File.GetLastWriteTimeUtc(_dbFilePath);
        var aniListByMal = new Dictionary<int, int>();
        var malByAniList = new Dictionary<int, int>();
        var entryByAniList = new Dictionary<int, int>();
        var unsettled = new List<int>();

        var exact = new Dictionary<string, int>(root.Data.Count * 3);
        var keyRank = new Dictionary<string, int>(root.Data.Count * 3);
        var fuzzy = new List<(string, int, string[])>(root.Data.Count * 3);
        var words = new Dictionary<string, string>(StringComparer.Ordinal);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        string Shared(Dictionary<string, string> pool, string s) =>
            pool.TryGetValue(s, out var existing) ? existing : pool[s] = s;

        for (int i = 0; i < root.Data.Count; i++)
        {
            var entry = root.Data[i];
            entry.MalId = ExtractMalId(entry.Sources);
            if (ExtractId(entry.Sources, "anilist.co/anime/") is > 0 and var aniList)
            {
                entryByAniList.TryAdd(aniList, i);
                if (!string.Equals(entry.Status, "FINISHED", StringComparison.OrdinalIgnoreCase)) unsettled.Add(aniList);
                if (entry.MalId > 0)
                {
                    aniListByMal.TryAdd(entry.MalId, aniList);
                    malByAniList.TryAdd(aniList, entry.MalId);
                }
            }

            var canonicalKey = Normalize(entry.Title ?? string.Empty);

            foreach (var name in EnumerateNames(entry))
            {
                var key = Normalize(name);
                if (key.Length < 2) continue;

                // Collision rule: many entries share keys (specials/recaps often
                // carry the main show's name as a synonym). Prefer, in order:
                // the entry whose CANONICAL title owns the key, then TV series,
                // then longer shows — so "one piece" maps to the series, not a special.
                var rank = KeyRank(entry, key == canonicalKey);
                if (!exact.TryGetValue(key, out _))
                {
                    exact[key] = i;
                    keyRank[key] = rank;
                    var tokens = key.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Distinct().Select(t => Shared(words, t)).ToArray();
                    if (tokens.Length > 0) fuzzy.Add((key, i, tokens));
                }
                else if (rank > keyRank[key])
                {
                    exact[key] = i;
                    keyRank[key] = rank;
                }
            }

            if (entry.RelatedAnime is { Count: > 0 } related)
            {
                var ids = related.Select(u => ExtractMalId([u])).Where(id => id > 0).Distinct().ToArray();
                entry.RelatedMal = ids.Length > 0 ? ids : null;
            }

            // Only indexing reads these; together they were most of the strings the app held.
            entry.Sources = null;
            entry.Synonyms = null;
            entry.RelatedAnime = null;
            if (entry.Type != null) entry.Type = Shared(labels, entry.Type);
            if (entry.Status != null) entry.Status = Shared(labels, entry.Status);
            if (entry.AnimeSeason?.Season != null) entry.AnimeSeason.Season = Shared(labels, entry.AnimeSeason.Season);
        }

        var byMal = new Dictionary<int, int>(root.Data.Count);
        for (int i = 0; i < root.Data.Count; i++)
            if (root.Data[i].MalId > 0) byMal.TryAdd(root.Data[i].MalId, i);

        _entries = root.Data;
        _byMalId = byMal;
        _chainByMalId = BuildChains(root.Data);
        _exactIndex = exact;
        _fuzzyKeys = fuzzy;
        _aniListByMal = aniListByMal;
        _malByAniList = malByAniList;
        _entryByAniList = entryByAniList;
        UnsettledAniListIds = unsettled;
    }

    private static int KeyRank(OfflineAnimeEntry entry, bool isCanonicalTitle)
    {
        int rank = 0;
        if (isCanonicalTitle) rank += 8;
        if (string.Equals(entry.Type, "TV", StringComparison.OrdinalIgnoreCase)) rank += 4;
        else if (string.Equals(entry.Type, "ONA", StringComparison.OrdinalIgnoreCase)) rank += 2;
        if (entry.Episodes >= 10) rank += 1;
        if (entry.MalId > 0) rank += 1;
        return rank;
    }

    private static IEnumerable<string> EnumerateNames(OfflineAnimeEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Title)) yield return entry.Title;
        if (entry.Synonyms == null) yield break;
        foreach (var s in entry.Synonyms)
            if (!string.IsNullOrWhiteSpace(s)) yield return s;
    }

    private static int ExtractMalId(List<string>? sources) => ExtractId(sources, "myanimelist.net/anime/");

    private static int ExtractId(List<string>? sources, string prefix)
    {
        if (sources == null) return 0;
        foreach (var s in sources)
        {
            var idx = s.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var tail = s[(idx + prefix.Length)..].TrimEnd('/');
            if (int.TryParse(tail, out var id)) return id;
        }
        return 0;
    }

    // ── Parsing (AnitomySharp) ────────────────────────────────────

    public ParsedRelease ParseRelease(string releaseName)
    {
        if (string.IsNullOrWhiteSpace(releaseName))
            return new ParsedRelease(string.Empty, null, 1, null, null, false);

        try
        {
            var elements = AnitomySharp.AnitomySharp.Parse(releaseName);

            string title = string.Empty;
            int? episode = null;
            int season = 1;
            string? group = null;
            string? resolution = null;
            int episodeValueCount = 0;

            foreach (var el in elements)
            {
                switch (el.Category)
                {
                    case AnitomySharp.Element.ElementCategory.ElementAnimeTitle:
                        title = el.Value;
                        break;
                    case AnitomySharp.Element.ElementCategory.ElementEpisodeNumber:
                        episodeValueCount++;
                        if (episode == null && int.TryParse(el.Value, out var ep))
                            episode = ep;
                        break;
                    case AnitomySharp.Element.ElementCategory.ElementAnimeSeason:
                        if (int.TryParse(el.Value, out var se) && se >= 1 && se <= 30)
                            season = se;
                        break;
                    case AnitomySharp.Element.ElementCategory.ElementReleaseGroup:
                        group = el.Value;
                        break;
                    case AnitomySharp.Element.ElementCategory.ElementVideoResolution:
                        resolution = el.Value;
                        break;
                }
            }

            bool isBatch = IsBatchRelease(releaseName, episodeValueCount);
            // A batch has no single episode number.
            if (isBatch) episode = null;

            // Season expressed inside the title ("2nd Season") that Anitomy kept
            if (season == 1)
            {
                var detected = SeasonDetector.DetectSeason(title);
                if (detected > 1)
                {
                    season = detected;
                    title = SeasonDetector.ExtractBaseTitle(title);
                }
            }

            return new ParsedRelease(title.Trim(), episode, season, group, resolution, isBatch);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[TitleResolver] Anitomy parse failed for '{Name}'", releaseName);
            return new ParsedRelease(releaseName, null, 1, null, null, false);
        }
    }

    // A true episode RANGE: two numbers joined DIRECTLY by a dash/tilde, e.g.
    // "01-24" or "01~24". Crucially, NOT preceded by a letter — this excludes
    // season markers like "S3 - 03" (the "3" follows "S") and "S3-03". Single
    // episodes use " - 03 " (spaces) and never match this.
    private static readonly Regex EpisodeRangePattern = new(
        @"(?<![A-Za-z0-9])(\d{1,4})[-~](\d{1,4})(?![A-Za-z0-9])", RegexOptions.Compiled);
    private static readonly Regex BatchWordPattern = new(
        @"\b(batch|complete\s*series|complete|all\s*episodes)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// True if a batch release plausibly contains the given episode. Word batches
    /// ("Complete Series", "Batch") are assumed to; a numeric range "01-24" is checked
    /// for containment. Prevents an episode search from matching an out-of-range batch.
    /// </summary>
    public static bool BatchContainsEpisode(string releaseName, int episode)
    {
        if (string.IsNullOrWhiteSpace(releaseName)) return false;
        if (BatchWordPattern.IsMatch(releaseName)) return true;

        foreach (Match m in EpisodeRangePattern.Matches(releaseName))
        {
            if (int.TryParse(m.Groups[1].Value, out var a) &&
                int.TryParse(m.Groups[2].Value, out var b) &&
                a < 1900 && b <= 4000 && b - a >= 2 &&
                episode >= a && episode <= b)
                return true;
        }
        return false;
    }

    public static bool IsBatchRelease(string releaseName, int anitomyEpisodeCount)
    {
        if (string.IsNullOrWhiteSpace(releaseName)) return false;
        if (anitomyEpisodeCount > 1) return true;
        if (BatchWordPattern.IsMatch(releaseName)) return true;

        // Episode range like "01-25" / "(01~24)". Valid only when the second number
        // is meaningfully larger (spans ≥3 eps) and the first isn't a year — so
        // "S3-03", "1-2", and dates like "2024-01" are excluded.
        foreach (Match m in EpisodeRangePattern.Matches(releaseName))
        {
            if (int.TryParse(m.Groups[1].Value, out var a) &&
                int.TryParse(m.Groups[2].Value, out var b) &&
                a < 1900 && b <= 4000 && b - a >= 2)
                return true;
        }
        return false;
    }

    // ── Resolution ────────────────────────────────────────────────

    public ResolvedAnime? ResolveRelease(string releaseName)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(releaseName)) return null;

        return _resolveCache.GetOrAdd(releaseName, _ =>
        {
            var parsed = ParseRelease(releaseName);
            return ResolveTitleCore(parsed.Title, parsed.Season);
        });
    }

    public ResolvedAnime? GetByMalId(int malId) =>
        IsReady && _byMalId.TryGetValue(malId, out var idx) ? ToResolved(_entries[idx]) : null;

    /// <summary>Indexes a database file directly (tests).</summary>
    internal void LoadFile(string path)
    {
        _dbFilePath = path;
        LoadAndIndex();
        IsReady = true;
    }

    // ── AniList top-up ────────────────────────────────────────────

    public int? AniListIdForMal(int malId) => _aniListByMal.TryGetValue(malId, out var id) ? id : null;

    public int? MalIdFor(AniList.AniListMedia media)
    {
        if (_malByAniList.TryGetValue(media.Id, out var known)) return known;
        return media.IdMal is > 0 and var claimed && (!_aniListByMal.TryGetValue(claimed, out var other) || other == media.Id)
            ? claimed : null;
    }

    /// <summary>
    /// Fetches what AniList knows since the database's snapshot, now if the copy on disk is stale and
    /// then every <see cref="CatalogInterval"/>, merging each new copy in.
    /// </summary>
    private async Task KeepCatalogFreshAsync(CancellationToken ct)
    {
        if (_catalog == null) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await _catalog.RefreshAsync(CatalogSince, UnsettledAniListIds, ct)) ApplySupplement(_catalog.Items);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning("[TitleResolver] AniList top-up failed: {Message}", ex.Message);
            }
            await Task.Delay(CatalogInterval, ct);
        }
    }

    /// <summary>
    /// From a season before the snapshot: the database caught the shows that had just started then
    /// with a placeholder length and "ongoing", so they need AniList's word too.
    /// </summary>
    public DateTime CatalogSince => (SnapshotDate ?? DateTime.UtcNow).Date.AddDays(-90);

    /// <summary>
    /// Merges AniList's shows into the index: each one updates the entry it already is (status,
    /// length, season, extra names, sequel/prequel links), or is added when the database never had
    /// it. Which entry it is: the database's own AniList link first, then AniList's MyAnimeList id
    /// when nothing else claims it, then — for the shows AniList links wrongly or not at all — a
    /// MyAnimeList-only entry of the same kind and year whose names match closely. A show with no
    /// MyAnimeList id at all can't be a library series, so it only refreshes an entry it already has.
    /// Safe to repeat; readers see the old index until the new one is swapped in.
    /// </summary>
    public int ApplySupplement(IReadOnlyList<AniList.AniListMedia> media)
    {
        if (!IsReady || media.Count == 0) return 0;
        lock (_supplementLock)
        {
            var entries = new List<OfflineAnimeEntry>(_entries);
            var exact = new Dictionary<string, int>(_exactIndex);
            var fuzzy = new List<(string Key, int EntryIdx, string[] Tokens)>(_fuzzyKeys);
            var byMal = new Dictionary<int, int>(_byMalId);
            var aniListByMal = new Dictionary<int, int>(_aniListByMal);
            var malByAniList = new Dictionary<int, int>(_malByAniList);
            var entryByAniList = new Dictionary<int, int>(_entryByAniList);

            // An id AniList gives to several shows in this batch is right for at most one of them.
            var shared = media.Where(m => m.IdMal is > 0).GroupBy(m => m.IdMal!.Value)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            var orphans = new Lazy<List<(int EntryIdx, string[] Tokens)>>(() => MalOnlyNames(entries, fuzzy, aniListByMal));

            int updated = 0, added = 0, linked = 0;
            foreach (var m in media)
            {
                int? mal = malByAniList.TryGetValue(m.Id, out var known) ? known : null;
                if (mal == null && m.IdMal is > 0 and var claimed && !shared.Contains(claimed)
                    && (!aniListByMal.TryGetValue(claimed, out var other) || other == m.Id))
                    mal = claimed;
                if (mal == null && MatchByNames(m, entries, orphans.Value) is { } byName && !aniListByMal.ContainsKey(byName))
                {
                    mal = byName;
                    linked++;
                }

                int idx;
                if (mal is { } id && byMal.TryGetValue(id, out var existing)) idx = existing;
                else if (entryByAniList.TryGetValue(m.Id, out var aniListOnly)) idx = aniListOnly;
                else if (mal is { } newId)
                {
                    idx = entries.Count;
                    entries.Add(new OfflineAnimeEntry { Title = m.Title.Romaji ?? m.Title.English ?? m.Title.Native, MalId = newId });
                    added++;
                }
                else continue;

                var e = entries[idx];
                if (mal is { } link)
                {
                    if (e.MalId <= 0) e.MalId = link;
                    byMal.TryAdd(link, idx);
                    aniListByMal.TryAdd(link, m.Id);
                    malByAniList.TryAdd(m.Id, link);
                }
                entryByAniList.TryAdd(m.Id, idx);
                Refresh(e, m);
                updated++;

                foreach (var name in m.AllTitles())
                {
                    var key = Normalize(name);
                    if (key.Length < 2 || exact.ContainsKey(key)) continue;
                    exact[key] = idx;
                    fuzzy.Add((key, idx, key.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray()));
                }

                var seasons = (m.Relations?.Edges ?? [])
                    .Where(r => r.RelationType is "SEQUEL" or "PREQUEL" && r.Node is { Type: "ANIME" })
                    .Select(r => malByAniList.TryGetValue(r.Node!.Id, out var n) ? n
                        : r.Node.IdMal is > 0 and var nodeMal && !shared.Contains(nodeMal) ? nodeMal : 0)
                    .Where(n => n > 0 && n != e.MalId)
                    .ToArray();
                if (seasons.Length > 0) e.SeasonMal = (e.SeasonMal ?? []).Union(seasons).ToArray();
            }

            _entries = entries;
            _byMalId = byMal;
            _exactIndex = exact;
            _fuzzyKeys = fuzzy;
            _aniListByMal = aniListByMal;
            _malByAniList = malByAniList;
            _entryByAniList = entryByAniList;
            _chainByMalId = BuildChains(entries);
            _resolveCache.Clear();
            _logger.LogInformation("[TitleResolver] AniList top-up: {Updated} shows refreshed, {Added} added, {Linked} linked by name",
                updated, added, linked);
            return updated;
        }
    }

    private static void Refresh(OfflineAnimeEntry e, AniList.AniListMedia m)
    {
        // A length the database already had for a finished show is MAL's, and may follow MAL's
        // split where AniList's doesn't; the matcher counts on it. Only placeholders are replaced.
        if (m.Episodes is > 0 && (e.Status != "FINISHED" || e.Episodes <= 0)) e.Episodes = m.Episodes.Value;
        e.Status = AniList.AniListMapper.CatalogStatus(m.Status);
        if (string.IsNullOrEmpty(e.Type) || e.Type == "UNKNOWN") e.Type = AniList.AniListMapper.CatalogType(m.Format);
        if (m.Year is { } year)
            e.AnimeSeason = new OfflineAnimeSeason { Year = year, Season = m.Season ?? e.AnimeSeason?.Season };
        e.Picture ??= m.CoverImage?.Large;
        e.Title ??= m.Title.Romaji ?? m.Title.English;
    }

    /// <summary>The names of the entries that have a MyAnimeList id but no AniList link — recent ones only.</summary>
    private static List<(int EntryIdx, string[] Tokens)> MalOnlyNames(List<OfflineAnimeEntry> entries,
        List<(string Key, int EntryIdx, string[] Tokens)> fuzzy, Dictionary<int, int> aniListByMal)
    {
        var since = DateTime.UtcNow.Year - 3;
        return fuzzy
            .Where(k => entries[k.EntryIdx] is { MalId: > 0 } e && !aniListByMal.ContainsKey(e.MalId)
                        && (e.AnimeSeason?.Year ?? since) >= since)
            .Select(k => (k.EntryIdx, k.Tokens))
            .ToList();
    }

    /// <summary>
    /// The MyAnimeList-only entry this AniList show is, by its names: the same kind (TV/ONA…) and a
    /// year apart at most, no season number that disagrees, and names sharing most of their words
    /// — clearly better than any other candidate.
    /// </summary>
    private static int? MatchByNames(AniList.AniListMedia m, List<OfflineAnimeEntry> entries, List<(int EntryIdx, string[] Tokens)> candidates)
    {
        var kind = AniList.AniListMapper.CatalogType(m.Format);
        var names = m.AllTitles().Select(Normalize).Where(n => n.Length >= 2)
            .Select(n => (Tokens: n.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(), Season: SeasonDetector.DetectSeason(n)))
            .ToList();
        if (names.Count == 0) return null;

        var best = new Dictionary<int, double>();
        foreach (var (entryIdx, tokens) in candidates)
        {
            var e = entries[entryIdx];
            if (!string.Equals(e.Type, kind, StringComparison.OrdinalIgnoreCase)) continue;
            if (m.Year is { } y && e.AnimeSeason?.Year is { } ey && Math.Abs(y - ey) > 1) continue;
            var keySeason = SeasonDetector.DetectSeason(string.Join(' ', tokens));
            foreach (var (nameTokens, season) in names)
            {
                if (season != keySeason) continue;
                var common = tokens.Count(nameTokens.Contains);
                var score = (double)common / (tokens.Length + nameTokens.Count - common);
                if (!best.TryGetValue(e.MalId, out var prev) || score > prev) best[e.MalId] = score;
            }
        }

        var ranked = best.OrderByDescending(kv => kv.Value).Take(2).ToList();
        if (ranked.Count == 0 || ranked[0].Value < 0.75) return null;
        if (ranked.Count > 1 && ranked[1].Value > ranked[0].Value - 0.1) return null;
        return ranked[0].Key;
    }

    /// <summary>
    /// A show's seasons as MAL links them (sequel/prequel, see SeasonFamilyService), when known.
    /// They join seasons whose titles share nothing ("Jujutsu Kaisen" → "… Shimetsu Kaiyuu - Zenpen",
    /// "Enen no Shouboutai" → "… Ni no Shou"), which the title chain can't. Unknown → the title chain.
    /// </summary>
    public Func<int, IReadOnlyList<int>?>? Families { get; set; }

    public IReadOnlyList<ResolvedAnime> GetSeasonChain(int malId)
    {
        if (!IsReady) return [];
        if (Families?.Invoke(malId) is { Count: > 1 } family)
        {
            var members = family.Select(GetByMalId).OfType<ResolvedAnime>()
                .Where(a => a.Type is { } t && (t.Equals("TV", StringComparison.OrdinalIgnoreCase) || t.Equals("ONA", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(a => a.Year ?? int.MaxValue).ThenBy(a => SeasonRank(a.AnimeSeason)).ThenBy(a => a.MalId)
                .ToList();
            if (members.Count > 1 && members.Any(m => m.MalId == malId)) return members;
        }

        // Without them: the offline database's own links, combined with the title chain.
        var ids = new HashSet<int>(OfflineFamily(malId));
        if (_chainByMalId.TryGetValue(malId, out var chain))
            foreach (var i in chain) ids.Add(_entries[i].MalId);
        if (ids.Count > 1)
            return ids.Select(GetByMalId).OfType<ResolvedAnime>()
                .OrderBy(a => a.Year ?? int.MaxValue).ThenBy(a => SeasonRank(a.AnimeSeason)).ThenBy(a => a.MalId)
                .ToList();
        return GetByMalId(malId) is { } self ? [self] : [];
    }

    /// <summary>
    /// The entries the offline database links to this one, followed link to link — only between
    /// entries of the same kind (TV with TV, ONA with ONA). Its links carry no relation type, so a
    /// show's chibi shorts (ONA) or films would join a TV series otherwise.
    /// </summary>
    private IEnumerable<int> OfflineFamily(int malId)
    {
        if (!_byMalId.TryGetValue(malId, out var start)) return [];
        var type = _entries[start].Type;
        if (!string.Equals(type, "TV", StringComparison.OrdinalIgnoreCase) && !string.Equals(type, "ONA", StringComparison.OrdinalIgnoreCase))
            return [];

        var seen = new HashSet<int> { malId };
        var confirmed = new HashSet<int>();
        var queue = new Queue<int>([malId]);
        while (queue.Count > 0 && seen.Count <= SeasonFamilyService.MaxMembers)
        {
            if (!_byMalId.TryGetValue(queue.Dequeue(), out var i)) continue;
            var e = _entries[i];
            foreach (var id in (e.RelatedMal ?? []).Concat(e.SeasonMal ?? []))
                if (_byMalId.TryGetValue(id, out var j) && string.Equals(_entries[j].Type, type, StringComparison.OrdinalIgnoreCase) && seen.Add(id))
                    queue.Enqueue(id);
            foreach (var id in e.SeasonMal ?? []) confirmed.Add(id);
        }
        // A franchise this big links far more than one show's seasons: leave it to the titles.
        if (seen.Count > SeasonFamilyService.MaxMembers) return [];

        // Links are walked through anything of the same kind, but only seasons are kept: not a
        // one- or two-episode special or collab (unless AniList calls it a sequel or prequel — a
        // one-episode first part, "Steel Ball Run - 1st STAGE", is counted by the groups), and named
        // after the show — its title starts, word for word, with the show's base name (the shortest
        // family title this one starts with: "Enen no Shouboutai" for "… San no Shou"), not a
        // spin-off with a title of its own.
        var keys = seen.ToDictionary(id => id, id => ChainKey(_entries[_byMalId[id]].Title ?? ""));
        var own = keys[malId];
        var root = keys.Values.Where(k => IsWordPrefix(k, own)).OrderBy(k => k.Length).FirstOrDefault() ?? own;
        return seen.Where(id =>
            id == malId ||
            ((confirmed.Contains(id) || _entries[_byMalId[id]].Episodes is not (> 0 and <= 2)) && IsWordPrefix(root, keys[id])));
    }

    private static bool IsWordPrefix(string prefix, string of) =>
        prefix.Length > 0 && of.StartsWith(prefix, StringComparison.Ordinal) && (of.Length == prefix.Length || of[prefix.Length] == ' ');

    // ── Season chains ─────────────────────────────────────────────

    /// <summary>
    /// Groups the TV/ONA entries that are one show split up by MAL — "Bleach: Sennen Kessen-hen",
    /// "… - Ketsubetsu-tan", "… - Soukoku-tan"; "Re:Zero … ", "… 2nd Season", "… 2nd Season Part 2" —
    /// by their base title, and orders each group by when it aired. Specials, recaps and movies
    /// are left out: release groups don't count them in a show's episode numbers.
    /// </summary>
    private static Dictionary<int, int[]> BuildChains(List<OfflineAnimeEntry> entries)
    {
        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.MalId <= 0 || string.IsNullOrWhiteSpace(e.Title)) continue;
            if (!string.Equals(e.Type, "TV", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(e.Type, "ONA", StringComparison.OrdinalIgnoreCase)) continue;
            var key = ChainKey(e.Title);
            if (key.Length < 2) continue;
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(i);
        }

        var byMal = new Dictionary<int, int[]>();
        foreach (var list in groups.Values)
        {
            if (list.Count < 2) continue;
            var ordered = list
                .OrderBy(i => entries[i].AnimeSeason?.Year ?? int.MaxValue)
                .ThenBy(i => SeasonRank(entries[i].AnimeSeason?.Season))
                .ThenBy(i => entries[i].MalId)
                .ToArray();
            foreach (var i in ordered) byMal[entries[i].MalId] = ordered;
        }
        return byMal;
    }

    /// <summary>A show's title without its season, part or cour: what its seasons have in common.</summary>
    internal static string ChainKey(string title)
    {
        var t = SeasonDetector.ExtractBaseTitle(title);
        // A cour's own subtitle comes after " - " ("Bleach: Sennen Kessen-hen - Kashin-tan").
        var dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) t = t[..dash];
        return Normalize(t);
    }

    private static int SeasonRank(string? season) => season?.ToUpperInvariant() switch
    {
        "WINTER" => 0, "SPRING" => 1, "SUMMER" => 2, "FALL" => 3, _ => 4,
    };

    public ResolvedAnime? ResolveTitle(string title, int season = 1)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(title)) return null;
        return _resolveCache.GetOrAdd($"{title}{season}", _ => ResolveTitleCore(title, season));
    }

    public List<ResolvedAnime> Search(string query, int limit = 20)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(query)) return [];

        var q = Normalize(query);
        if (q.Length < 2) return [];
        var qTokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        // Score each indexed title/synonym; keep the best score per entry, then rank.
        var bestByEntry = new Dictionary<int, double>();
        foreach (var (key, entryIdx, tokens) in _fuzzyKeys)
        {
            double score;
            if (key == q) score = 1000;
            else if (key.StartsWith(q, StringComparison.Ordinal)) score = 500 + q.Length;
            else if (key.Contains(q, StringComparison.Ordinal)) score = 200 + q.Length;
            else
            {
                int common = 0;
                foreach (var t in tokens)
                    if (qTokens.Contains(t)) common++;
                if (common == 0) continue;
                score = 100.0 * common / Math.Max(tokens.Length, qTokens.Count);
            }

            if (!bestByEntry.TryGetValue(entryIdx, out var prev) || score > prev)
                bestByEntry[entryIdx] = score;
        }

        return bestByEntry
            .OrderByDescending(kv => kv.Value)
            .ThenByDescending(kv => _entries[kv.Key].Episodes)  // prefer series over specials
            .Take(limit)
            .Select(kv => ToResolved(_entries[kv.Key]))
            .ToList();
    }

    private ResolvedAnime? ResolveTitleCore(string title, int season)
    {
        // Dual-titled releases keep an alternate name in parentheses:
        // "Re:Zero kara Hajimeru... (Re:ZERO -Starting Life...-)". Try the
        // whole string, the part outside the parens, and the part inside.
        foreach (var baseTitle in SplitDualTitles(title))
        {
            // 1. Exact synonym hit, trying season-qualified variants first
            foreach (var candidate in TitleCandidates(baseTitle, season))
            {
                var key = Normalize(candidate);
                if (key.Length >= 2 && _exactIndex.TryGetValue(key, out var idx))
                    return ToResolved(_entries[idx]);
            }
        }

        // 2. Fuzzy fallback — strict threshold; wrong match is worse than no match
        var queryKey = Normalize(season > 1 ? $"{title} season {season}" : title);
        var queryTokens = queryKey.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (queryTokens.Count == 0) return null;

        double bestScore = 0;
        int bestIdx = -1;

        foreach (var (key, entryIdx, tokens) in _fuzzyKeys)
        {
            // Cheap rejects before set math
            if (tokens.Length > queryTokens.Count + 3 || queryTokens.Count > tokens.Length + 3) continue;

            int common = 0;
            foreach (var t in tokens)
                if (queryTokens.Contains(t)) common++;
            if (common == 0) continue;

            // Dice coefficient over token sets
            double dice = 2.0 * common / (tokens.Length + queryTokens.Count);
            if (dice > bestScore)
            {
                bestScore = dice;
                bestIdx = entryIdx;
            }
        }

        return bestScore >= 0.85 && bestIdx >= 0 ? ToResolved(_entries[bestIdx]) : null;
    }

    private static IEnumerable<string> SplitDualTitles(string title)
    {
        yield return title;

        var open = title.IndexOf('(');
        var close = title.LastIndexOf(')');
        if (open <= 0 || close <= open) yield break;

        var outside = (title[..open] + title[(close + 1)..]).Trim();
        var inside  = title[(open + 1)..close].Trim();

        // Only treat the parenthetical as an alt TITLE when it's substantial —
        // short parens are usually tags, years, or quality markers.
        if (outside.Length >= 3) yield return outside;
        if (inside.Length >= 8 && !inside.All(char.IsDigit)) yield return inside;
    }

    private static IEnumerable<string> TitleCandidates(string title, int season)
    {
        if (season > 1)
        {
            // Common season-suffix conventions across the synonym lists
            yield return $"{title} Season {season}";
            yield return $"{title} {ToOrdinal(season)} Season";
            yield return $"{title} {season}";
            yield return $"{title} S{season}";
            yield return $"{title} {ToRoman(season)}";
            yield return $"{title} Part {season}";
        }
        yield return title;
    }

    private static string ToOrdinal(int n) => n switch
    {
        1 => "1st", 2 => "2nd", 3 => "3rd", _ => $"{n}th"
    };

    private static string ToRoman(int n) => n switch
    {
        2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI",
        7 => "VII", 8 => "VIII", 9 => "IX", 10 => "X", _ => n.ToString()
    };

    private static ResolvedAnime ToResolved(OfflineAnimeEntry e) => new(
        MalId: e.MalId,
        CanonicalTitle: e.Title ?? string.Empty,
        Episodes: e.Episodes > 0 ? e.Episodes : null,
        PictureUrl: e.Picture,
        ThumbnailUrl: e.Thumbnail,
        Year: e.AnimeSeason?.Year,
        AnimeSeason: e.AnimeSeason?.Season,
        Status: e.Status,
        Type: e.Type);

    // ── Normalization ─────────────────────────────────────────────

    private static readonly Regex NonAlphaNumPattern = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    /// <summary>
    /// Aggressive normalization so "Re:ZERO -Starting Life-", "re zero starting life"
    /// and "Re Zero: Starting Life" all produce the same key.
    /// </summary>
    internal static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;

        // Strip diacritics (ō → o, é → e)
        var formD = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var c in formD)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        var cleaned = NonAlphaNumPattern.Replace(sb.ToString(), " ");
        return Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
    }

    // ── Offline DB DTOs (lenient — unmapped fields ignored) ───────

    private class OfflineDatabaseRoot
    {
        [JsonPropertyName("lastUpdate")]
        public string? LastUpdate { get; set; }

        [JsonPropertyName("data")]
        public List<OfflineAnimeEntry> Data { get; set; } = [];
    }

    private class OfflineAnimeEntry
    {
        [JsonPropertyName("sources")]
        public List<string>? Sources { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("synonyms")]
        public List<string>? Synonyms { get; set; }

        [JsonPropertyName("episodes")]
        public int Episodes { get; set; }

        [JsonPropertyName("picture")]
        public string? Picture { get; set; }

        [JsonPropertyName("thumbnail")]
        public string? Thumbnail { get; set; }

        [JsonPropertyName("animeSeason")]
        public OfflineAnimeSeason? AnimeSeason { get; set; }

        [JsonPropertyName("relatedAnime")]
        public List<string>? RelatedAnime { get; set; }

        [JsonIgnore]
        public int MalId { get; set; }

        /// <summary>MAL ids of the related entries (any relation: sequel, side story, film…).</summary>
        [JsonIgnore]
        public int[]? RelatedMal { get; set; }

        /// <summary>MAL ids AniList calls this entry's sequel or prequel — a relation the database's links don't name.</summary>
        [JsonIgnore]
        public int[]? SeasonMal { get; set; }
    }

    private class OfflineAnimeSeason
    {
        [JsonPropertyName("season")]
        public string? Season { get; set; }

        [JsonPropertyName("year")]
        public int? Year { get; set; }
    }
}
