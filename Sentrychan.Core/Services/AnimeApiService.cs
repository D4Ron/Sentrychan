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

public class AnimeApiService : IAnimeApiService
{
    private readonly IJikanApi _jikan;
    private readonly IMemoryCache _memoryCache;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<AnimeApiService> _logger;
    // Replace the HttpClient field and constructor parameter with:
    private readonly IHttpClientFactory _httpClientFactory;

    private static readonly TimeSpan MemoryCacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PersistentCacheTtl = TimeSpan.FromDays(7);

    private readonly string _imageCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Sentrychan", "ImageCache");

    

    public AnimeApiService(
        IJikanApi jikan,
        IMemoryCache memoryCache,
        IDbContextFactory<AppDbContext> dbFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<AnimeApiService> logger)
    {
        _jikan = jikan;
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
        string key, T data, CancellationToken ct) where T : class
    {
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
                existing.ExpiresAt = DateTime.UtcNow.Add(PersistentCacheTtl);
            }
            else
            {
                db.ApiCaches.Add(new Models.ApiCache
                {
                    CacheKey = key,
                    Payload = payload,
                    CachedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.Add(PersistentCacheTtl)
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