using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models.Api;
using Sentrychan.Core.Services.Api;

namespace Sentrychan.Core.Services;

/// <summary>
/// Show details, search, seasons and recommendations: from AniList first (current, and quick), with
/// Jikan (MyAnimeList) behind it for when AniList can't answer. Characters and a MyAnimeList user's
/// list only exist on Jikan.
/// </summary>
public class AnimeApiService : IAnimeApiService
{
    private readonly IJikanApi _jikan;
    private readonly AniList.AniListShows? _aniList;
    private readonly IMemoryCache _memoryCache;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<AnimeApiService> _logger;
    // Replace the HttpClient field and constructor parameter with:
    private readonly IHttpClientFactory _httpClientFactory;

    private static readonly TimeSpan MemoryCacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PersistentCacheTtl = TimeSpan.FromDays(7);

    private readonly string _imageCacheDir = AppPaths.Combine("ImageCache");

    

    public AnimeApiService(
        IJikanApi jikan,
        IMemoryCache memoryCache,
        IDbContextFactory<AppDbContext> dbFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<AnimeApiService> logger,
        AniList.AniListShows? aniList = null)
    {
        _jikan = jikan;
        _aniList = aniList;
        _memoryCache = memoryCache;
        _dbFactory = dbFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        Directory.CreateDirectory(_imageCacheDir);
    }

    public async Task<List<AnimeResult>> SearchAnimeAsync(
    string query, string? status = null, int limit = 12,
    CancellationToken ct = default)
    {
        var cacheKey = $"search_{query.ToLowerInvariant().Trim()}_{status}_{limit}";

        if (_memoryCache.TryGetValue(cacheKey, out List<AnimeResult>? cached) && cached != null)
            return cached;

        var persistent = await GetFromPersistentCacheAsync<List<AnimeResult>>(cacheKey, ct);
        if (persistent != null)
        {
            _memoryCache.Set(cacheKey, persistent, MemoryCacheTtl);
            return persistent;
        }

        if (await AniListSearchAsync(query, status, limit, ct) is { Count: > 0 } fromAniList)
        {
            await SetPersistentCacheAsync(cacheKey, fromAniList, ct, TimeSpan.FromDays(1));
            _memoryCache.Set(cacheKey, fromAniList, MemoryCacheTtl);
            return fromAniList;
        }

        try
        {
            var results = new List<AnimeResult>();
            var seenIds = new HashSet<int>();

            // ── Request 1: all-status search ──────────────────────────
            // Fetch more than needed so scoring has material to work with
            var allResponse = await _jikan.SearchAnimeAllStatusAsync(query, 25, ct);
            if (allResponse.Data != null)
                foreach (var r in allResponse.Data)
                    if (seenIds.Add(r.MalId))
                        results.Add(r);

            // ── Jikan rate limit: 3 req/s, wait before second call ────
            await Task.Delay(400, ct);

            // ── Request 2: airing-only search (different ranking) ─────
            if (status == null)
            {
                var airingResponse = await _jikan.SearchAnimeAsync(query, 10, "airing", ct);
                if (airingResponse.Data != null)
                    foreach (var r in airingResponse.Data)
                        if (seenIds.Add(r.MalId))
                            results.Add(r);
            }

            // ── Score and rank ────────────────────────────────────────
            results = results
                .Select(r => (Result: r, Score: ScoreMatch(r, query)))
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Result.Members)
                .Select(x => x.Result)
                .Take(limit)
                .ToList();

            await SetPersistentCacheAsync(cacheKey, results, ct);
            _memoryCache.Set(cacheKey, results, MemoryCacheTtl);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jikan search failed for query: {Query}", query);
            return [];
        }
    }

    /// <summary>AniList's search, ranked like Jikan's results were; empty when it can't answer.</summary>
    private async Task<List<AnimeResult>> AniListSearchAsync(string query, string? status, int limit, CancellationToken ct)
    {
        if (_aniList == null) return [];
        try
        {
            var aniListStatus = status?.ToLowerInvariant() switch
            {
                "airing" => "RELEASING",
                "complete" => "FINISHED",
                "upcoming" => "NOT_YET_RELEASED",
                _ => null,
            };
            var media = await _aniList.Client.SearchAsync(query, 25, aniListStatus, ct: ct);
            return ToResults(media)
                .Select(r => (Result: r, Score: ScoreMatch(r, query)))
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Result.Members)
                .Select(x => x.Result)
                .Take(limit)
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("AniList search for {Query} unavailable, asking MyAnimeList: {Message}", query, ex.Message);
            return [];
        }
    }

    /// <summary>AniList shows as results; one that can't be tied to a MyAnimeList id can't be added, so it's left out.</summary>
    private List<AnimeResult> ToResults(IEnumerable<AniList.AniListMedia> media) =>
        media.Select(m => (Media: m, Mal: _aniList!.MalIdFor(m)))
            .Where(x => x.Mal is > 0)
            .DistinctBy(x => x.Mal)
            .Select(x => AniList.AniListMapper.ToAnimeResult(x.Media, x.Mal!.Value, _aniList!.MalIdFor))
            .ToList();

    // Score how well a result matches the query — boosts English title matches
    private static double ScoreMatch(AnimeResult result, string query)
    {
        var q = query.ToLowerInvariant().Trim();
        double score = 0;

        if (result.Title.ToLowerInvariant().Contains(q)) score += 10;
        if (result.TitleEnglish?.ToLowerInvariant().Contains(q) == true) score += 10;
        if (result.TitleJapanese?.ToLowerInvariant().Contains(q) == true) score += 5;
        if (result.Titles?.Any(t => t.Title.ToLowerInvariant().Contains(q)) == true) score += 5;
        if (result.Status == "Currently Airing") score += 3;

        // Exact match bonus
        if (result.Title.ToLowerInvariant() == q) score += 20;
        if (result.TitleEnglish?.ToLowerInvariant() == q) score += 20;

        return score;
    }

    public async Task<AnimeResult?> GetAnimeByIdAsync(
        int malId, CancellationToken ct = default)
    {
        var aniListKey = $"al_anime_{malId}";
        if (_memoryCache.TryGetValue(aniListKey, out AnimeResult? fresh) && fresh != null)
            return fresh;
        if (await GetFromPersistentCacheAsync<AnimeResult>(aniListKey, ct) is { } stored)
        {
            _memoryCache.Set(aniListKey, stored, MemoryCacheTtl);
            return stored;
        }
        if (_aniList != null)
        {
            try
            {
                if (await _aniList.ForMalIdAsync(malId, null, ct: ct) is { } media)
                {
                    var result = AniList.AniListMapper.ToAnimeResult(media, malId, _aniList.MalIdFor);
                    // A show that's airing changes week to week; a finished one doesn't.
                    var ttl = result.Status == AiringStatusNormalizer.Finished ? PersistentCacheTtl : TimeSpan.FromHours(12);
                    await SetPersistentCacheAsync(aniListKey, result, ct, ttl);
                    _memoryCache.Set(aniListKey, result, MemoryCacheTtl);
                    return result;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning("AniList details for {MalId} unavailable, asking MyAnimeList: {Message}", malId, ex.Message);
            }
        }

        var cacheKey = $"anime_{malId}";

        if (_memoryCache.TryGetValue(cacheKey, out AnimeResult? cached) && cached != null)
            return cached;

        var persistent = await GetFromPersistentCacheAsync<AnimeResult>(cacheKey, ct);
        if (persistent != null)
        {
            _memoryCache.Set(cacheKey, persistent, MemoryCacheTtl);
            return persistent;
        }

        try
        {
            var response = await _jikan.GetAnimeByIdAsync(malId, ct);
            if (response.Data == null) return null;

            await SetPersistentCacheAsync(cacheKey, response.Data, ct);
            _memoryCache.Set(cacheKey, response.Data, MemoryCacheTtl);

            return response.Data;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get anime {MalId}", malId);
            return null;
        }
    }

    public async Task<List<AnimeCharacter>> GetAnimeCharactersAsync(
        int malId, CancellationToken ct = default)
    {
        var cacheKey = $"chars_{malId}";

        if (_memoryCache.TryGetValue(cacheKey, out List<AnimeCharacter>? cached) && cached != null)
            return cached;

        var persistent = await GetFromPersistentCacheAsync<List<AnimeCharacter>>(cacheKey, ct);
        if (persistent != null)
        {
            _memoryCache.Set(cacheKey, persistent, MemoryCacheTtl);
            return persistent;
        }

        try
        {
            var response = await _jikan.GetAnimeCharactersAsync(malId, ct);
            var result = response.Data ?? [];

            await SetPersistentCacheAsync(cacheKey, result, ct);
            _memoryCache.Set(cacheKey, result, MemoryCacheTtl);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get characters for {MalId}", malId);
            return [];
        }
    }

    public async Task<List<AnimeResult>> GetSeasonalAnimeAsync(
        int year, string season, CancellationToken ct = default)
    {
        var cacheKey = $"season_{year}_{season}";

        if (_memoryCache.TryGetValue(cacheKey, out List<AnimeResult>? cached) && cached != null)
            return cached;

        var persistent = await GetFromPersistentCacheAsync<List<AnimeResult>>(cacheKey, ct);
        if (persistent != null)
        {
            _memoryCache.Set(cacheKey, persistent, MemoryCacheTtl);
            return persistent;
        }

        if (_aniList != null)
        {
            try
            {
                var media = await _aniList.Client.SeasonAsync(year, season, ct: ct);
                var fromAniList = ToResults(media);
                if (fromAniList.Count > 0)
                {
                    await SetPersistentCacheAsync(cacheKey, fromAniList, ct, TimeSpan.FromDays(1));
                    _memoryCache.Set(cacheKey, fromAniList, MemoryCacheTtl);
                    return fromAniList;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogWarning("AniList season {Season} {Year} unavailable: {Message}", season, year, ex.Message); }
        }

        try
        {
            var response = await _jikan.GetSeasonalAnimeAsync(year, season.ToLower(), ct);
            var result = response.Data?
                .OrderByDescending(a => a.Members)
                .ToList() ?? [];

            await SetPersistentCacheAsync(cacheKey, result, ct);
            _memoryCache.Set(cacheKey, result, MemoryCacheTtl);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get seasonal anime {Year} {Season}", year, season);
            return [];
        }
    }

    public async Task<List<AnimeResult>> GetScheduleAsync(
        string day, CancellationToken ct = default)
    {
        var cacheKey = $"schedule_{day.ToLowerInvariant()}";

        if (_memoryCache.TryGetValue(cacheKey, out List<AnimeResult>? cached) && cached != null)
            return cached;

        var persistent = await GetFromPersistentCacheAsync<List<AnimeResult>>(cacheKey, ct);
        if (persistent != null)
        {
            _memoryCache.Set(cacheKey, persistent, MemoryCacheTtl);
            return persistent;
        }

        try
        {
            var response = await _jikan.GetSchedulesAsync(day.ToLowerInvariant(), ct);
            var result = response.Data?
                .OrderByDescending(a => a.Members)
                .ToList() ?? [];

            await SetPersistentCacheAsync(cacheKey, result, ct);
            _memoryCache.Set(cacheKey, result, MemoryCacheTtl);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get schedule for {Day}", day);
            return [];
        }
    }

    public async Task<List<AnimeResult>> GetAnimeRecommendationsAsync(
        int malId, CancellationToken ct = default)
    {
        var cacheKey = $"recs_{malId}";

        if (_memoryCache.TryGetValue(cacheKey, out List<AnimeResult>? cached) && cached != null)
            return cached;

        var persistent = await GetFromPersistentCacheAsync<List<AnimeResult>>(cacheKey, ct);
        if (persistent != null)
        {
            _memoryCache.Set(cacheKey, persistent, MemoryCacheTtl);
            return persistent;
        }

        if (_aniList != null)
        {
            try
            {
                var id = _aniList.AniListIdFor(malId) ?? (await _aniList.ForMalIdAsync(malId, null, ct: ct))?.Id;
                if (id is { } aniListId)
                {
                    var fromAniList = ToResults(await _aniList.Client.RecommendationsAsync(aniListId, ct));
                    await SetPersistentCacheAsync(cacheKey, fromAniList, ct);
                    _memoryCache.Set(cacheKey, fromAniList, MemoryCacheTtl);
                    return fromAniList;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogWarning("AniList recommendations for {MalId} unavailable: {Message}", malId, ex.Message); }
        }

        try
        {
            var response = await _jikan.GetAnimeRecommendationsAsync(malId, ct);
            var result = response.Data?
                .OrderByDescending(r => r.Votes)
                .Select(r => r.Entry)
                .ToList() ?? [];

            await SetPersistentCacheAsync(cacheKey, result, ct);
            _memoryCache.Set(cacheKey, result, MemoryCacheTtl);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get recommendations for {MalId}", malId);
            return [];
        }
    }

    public async Task<List<AnimeResult>> GetUserWatchingAsync(
        string username, CancellationToken ct = default)
    {
        try
        {
            var response = await _jikan.GetUserAnimeListAsync(username, "watching", ct);
            return response.Data?
                .Select(e => e.Anime)
                .ToList() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get watching list for user: {User}", username);
            return [];
        }
    }

    public async Task<string?> GetCachedImagePathAsync(
    string imageUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(imageUrl)) return null;

        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(imageUrl)));
        var ext = Path.GetExtension(imageUrl);
        if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".jpg";

        var filePath = Path.Combine(_imageCacheDir, $"{hash}{ext}");
        if (File.Exists(filePath)) return filePath;

        try
        {
            var client = _httpClientFactory.CreateClient("ImageClient");
            var bytes = await client.GetByteArrayAsync(imageUrl, ct);
            await File.WriteAllBytesAsync(filePath, bytes, ct);
            return filePath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache image: {Url}", imageUrl);
            return null;
        }
    }

    // ── Persistent Cache Helpers ───────────────────────────────────

    private async Task<T?> GetFromPersistentCacheAsync<T>(
        string key, CancellationToken ct) where T : class
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var entry = await db.ApiCaches
                .FirstOrDefaultAsync(c => c.CacheKey == key, ct);

            if (entry == null) return null;
            if (entry.ExpiresAt < DateTime.UtcNow)
            {
                db.ApiCaches.Remove(entry);
                await db.SaveChangesAsync(ct);
                return null;
            }

            return JsonSerializer.Deserialize<T>(entry.Payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Persistent cache read failed for key: {Key}", key);
            return null;
        }
    }

    private async Task SetPersistentCacheAsync<T>(
        string key, T data, CancellationToken ct, TimeSpan? ttl = null) where T : class
    {
        var lifetime = ttl ?? PersistentCacheTtl;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var payload = JsonSerializer.Serialize(data);
            var existing = await db.ApiCaches
                .FirstOrDefaultAsync(c => c.CacheKey == key, ct);

            if (existing != null)
            {
                existing.Payload = payload;
                existing.CachedAt = DateTime.UtcNow;
                existing.ExpiresAt = DateTime.UtcNow.Add(lifetime);
            }
            else
            {
                db.ApiCaches.Add(new Models.ApiCache
                {
                    CacheKey = key,
                    Payload = payload,
                    CachedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.Add(lifetime)
                });
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Persistent cache write failed for key: {Key}", key);
        }
    }
}