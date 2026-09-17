using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;
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
///     cross-linked MAL ids and poster URLs. Downloaded once, refreshed weekly,
///     indexed in memory: exact normalized-synonym match first, fuzzy fallback.
/// Resolution is fully offline and instant after the first download.
/// </summary>
public class TitleResolverService : ITitleResolverService
{
    private static readonly string[] DatabaseUrls =
    [
        "https://raw.githubusercontent.com/manami-project/anime-offline-database/master/anime-offline-database-minified.json",
        "https://github.com/manami-project/anime-offline-database/releases/latest/download/anime-offline-database-minified.json"
    ];

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(7);

    private readonly ILogger<TitleResolverService> _logger;
    private readonly HttpClient _http;
    private readonly string _dbFilePath;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    // normalized title/synonym → entry index
    private Dictionary<string, int> _exactIndex = new();
    // parallel arrays for fuzzy scan
    private List<OfflineAnimeEntry> _entries = [];
    private List<(string Key, int EntryIdx, HashSet<string> Tokens)> _fuzzyKeys = [];

    // per-session resolution memo (positive AND negative results)
    private readonly ConcurrentDictionary<string, ResolvedAnime?> _resolveCache = new(StringComparer.OrdinalIgnoreCase);

    public bool IsReady { get; private set; }

    public TitleResolverService(ILogger<TitleResolverService> logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Sentrychan/2.0");

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sentrychan");
        Directory.CreateDirectory(appData);
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
                "[TitleResolver] Ready — {Entries} anime, {Keys} title keys indexed",
                _entries.Count, _exactIndex.Count);
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

        foreach (var url in DatabaseUrls)
        {
            try
            {
                _logger.LogInformation("[TitleResolver] Downloading offline database: {Url}", url);
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode) continue;

                var tmpPath = _dbFilePath + ".tmp";
                await using (var fs = File.Create(tmpPath))
                {
                    await response.Content.CopyToAsync(fs, ct);
                }

                var size = new FileInfo(tmpPath).Length;
                if (size < 1_000_000) { File.Delete(tmpPath); continue; }

                File.Move(tmpPath, _dbFilePath, overwrite: true);
                _logger.LogInformation("[TitleResolver] Database downloaded ({Mb:F1} MB)", size / 1_048_576.0);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[TitleResolver] Download failed from {Url}", url);
            }
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

        var exact = new Dictionary<string, int>(root.Data.Count * 3);
        var keyRank = new Dictionary<string, int>(root.Data.Count * 3);
        var fuzzy = new List<(string, int, HashSet<string>)>(root.Data.Count * 3);

        for (int i = 0; i < root.Data.Count; i++)
        {
            var entry = root.Data[i];
            entry.MalId = ExtractMalId(entry.Sources);

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
                    var tokens = key.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                    if (tokens.Count > 0) fuzzy.Add((key, i, tokens));
                }
                else if (rank > keyRank[key])
                {
                    exact[key] = i;
                    keyRank[key] = rank;
                }
            }
        }

        _entries = root.Data;
        _exactIndex = exact;
        _fuzzyKeys = fuzzy;
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

    private static int ExtractMalId(List<string>? sources)
    {
        if (sources == null) return 0;
        foreach (var s in sources)
        {
            var idx = s.IndexOf("myanimelist.net/anime/", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var tail = s[(idx + "myanimelist.net/anime/".Length)..].TrimEnd('/');
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
                int common = qTokens.Count(t => tokens.Contains(t));
                if (common == 0) continue;
                score = 100.0 * common / Math.Max(tokens.Count, qTokens.Count);
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
            if (tokens.Count > queryTokens.Count + 3 || queryTokens.Count > tokens.Count + 3) continue;

            int common = 0;
            foreach (var t in queryTokens)
                if (tokens.Contains(t)) common++;
            if (common == 0) continue;

            // Dice coefficient over token sets
            double dice = 2.0 * common / (tokens.Count + queryTokens.Count);
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
        Status: e.Status);

    // ── Normalization ─────────────────────────────────────────────

    private static readonly Regex NonAlphaNumPattern = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    /// <summary>
    /// Aggressive normalization so "Re:ZERO -Starting Life-", "re zero starting life"
    /// and "Re Zero: Starting Life" all produce the same key.
    /// </summary>
    private static string Normalize(string s)
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

        [JsonIgnore]
        public int MalId { get; set; }
    }

    private class OfflineAnimeSeason
    {
        [JsonPropertyName("season")]
        public string? Season { get; set; }

        [JsonPropertyName("year")]
        public int? Year { get; set; }
    }
}
