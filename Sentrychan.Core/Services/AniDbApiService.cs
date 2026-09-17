using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models.Api;
using Sentrychan.Core.Services.AniDb;

namespace Sentrychan.Core.Services;

public class AniDbApiService : IAniDbApiService
{
    private readonly AniDbUdpClient _udpClient;
    private readonly IMemoryCache _memoryCache;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<AniDbApiService> _logger;

    private static readonly TimeSpan MemoryCacheTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan PersistentCacheTtl = TimeSpan.FromDays(30);

    public AniDbApiService(
        AniDbUdpClient udpClient,
        IMemoryCache memoryCache,
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<AniDbApiService> logger)
    {
        _udpClient = udpClient;
        _memoryCache = memoryCache;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<List<AnimeResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var cacheKey = $"anidb:search:{query.ToLowerInvariant().Trim()}";

        if (_memoryCache.TryGetValue(cacheKey, out List<AnimeResult>? cached))
            return cached ?? [];

        var persistent = await GetFromPersistentCacheAsync<List<AnimeResult>>(cacheKey, ct);
        if (persistent != null)
        {
            _memoryCache.Set(cacheKey, persistent, MemoryCacheTtl);
            return persistent;
        }

        try
        {
            // ANIMEBYNAME command -- search by name
            // Command: ANIMEBYNAME aname={Uri.EscapeDataString(query)}&fields=aid|titles|type|startdate|epcount|rating
            var command = $"ANIMEBYNAME aname={Uri.EscapeDataString(query)}&fields=aid|titles|type|startdate|epcount|rating";
            var response = await _udpClient.SendCommandAsync(command, ct);

            if (response.StartsWith("230 "))
            {
                var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1);
                var results = new List<AnimeResult>();

                foreach (var line in lines)
                {
                    var result = ParsePipeDelimited(line);
                    if (result != null) results.Add(result);
                }

                await SetPersistentCacheAsync(cacheKey, results, ct);
                _memoryCache.Set(cacheKey, results, MemoryCacheTtl);
                return results;
            }

            return [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AniDB search failed for query: {Query}", query);
            return [];
        }
    }

    public async Task<AnimeResult?> GetByIdAsync(int anidbId, CancellationToken ct = default)
    {
        var cacheKey = $"anidb:anime:{anidbId}";

        if (_memoryCache.TryGetValue(cacheKey, out AnimeResult? cached))
            return cached;

        var persistent = await GetFromPersistentCacheAsync<AnimeResult>(cacheKey, ct);
        if (persistent != null)
        {
            _memoryCache.Set(cacheKey, persistent, MemoryCacheTtl);
            return persistent;
        }

        try
        {
            // ANIME command -- fetch anime metadata by AniDB ID
            // Command: ANIME aid={anidbId}&fields=aid|eps|epcount|titles|type|startdate|ended|rating|votes|tmprating|tmpvotes|avgreviewrating|reviews|year|picname|nsfo
            var command = $"ANIME aid={anidbId}&fields=aid|eps|epcount|titles|type|startdate|ended|rating|votes|tmprating|tmpvotes|avgreviewrating|reviews|year|picname|nsfo";
            var response = await _udpClient.SendCommandAsync(command, ct);

            if (response.StartsWith("230 "))
            {
                var dataLine = response.Split('\n').Skip(1).FirstOrDefault();
                if (!string.IsNullOrEmpty(dataLine))
                {
                    var result = ParsePipeDelimited(dataLine);
                    if (result != null)
                    {
                        await SetPersistentCacheAsync(cacheKey, result, ct);
                        _memoryCache.Set(cacheKey, result, MemoryCacheTtl);
                        return result;
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AniDB GetById failed for AID: {Aid}", anidbId);
            return null;
        }
    }

    private AnimeResult? ParsePipeDelimited(string line)
    {
        // This is a simplified parser. Real AniDB responses can be complex.
        // fields requested in Search: aid|titles|type|startdate|epcount|rating
        // fields requested in GetById: aid|eps|epcount|titles|type|startdate|ended|rating|votes|tmprating|tmpvotes|avgreviewrating|reviews|year|picname|nsfo
        
        var fields = line.Split('|');
        if (fields.Length < 1) return null;

        var result = new AnimeResult();
        
        // Very basic mapping based on instructios
        // Map response to AnimeResult model: title from titles field (pipe-separated, pick the "main" type),
        // episodes from epcount, score from rating/10.0, year from startdate

        if (fields.Length > 0 && int.TryParse(fields[0], out var aid)) result.MalId = aid; // Using MalId as a generic ID field

        // Searching: aid(0)|titles(1)|type(2)|startdate(3)|epcount(4)|rating(5)
        // GetById: aid(0)|eps(1)|epcount(2)|titles(3)|type(4)|startdate(5)|ended(6)|rating(7)...

        if (fields.Length == 6) // Likely search result
        {
            result.Title = PickMainTitle(fields[1]);
            if (int.TryParse(fields[4], out var eps)) result.Episodes = eps;
            if (double.TryParse(fields[5], out var rating)) result.Score = rating / 100.0; // instructions say /10.0 but usually rating is 0-1000? instructions say rating/10.0
            if (DateTime.TryParse(fields[3], out var start)) result.Year = start.Year;
        }
        else if (fields.Length > 7) // Likely anime details
        {
             result.Title = PickMainTitle(fields[3]);
             if (int.TryParse(fields[2], out var eps)) result.Episodes = eps;
             if (double.TryParse(fields[7], out var rating)) result.Score = rating / 100.0;
             if (DateTime.TryParse(fields[5], out var start)) result.Year = start.Year;
        }

        return result;
    }

    private string PickMainTitle(string titlesField)
    {
        // Titles are often 'MainTitle' or a list of titles.
        // Instructions: pick the "main" type. Usually it's just the first one if it's a simple response.
        return titlesField.Split(',').FirstOrDefault() ?? "Unknown";
    }

    private async Task<T?> GetFromPersistentCacheAsync<T>(string key, CancellationToken ct) where T : class
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var entry = await db.ApiCaches.FirstOrDefaultAsync(c => c.CacheKey == key, ct);

            if (entry == null) return null;
            if (entry.ExpiresAt < DateTime.UtcNow)
            {
                db.ApiCaches.Remove(entry);
                await db.SaveChangesAsync(ct);
                return null;
            }

            return JsonSerializer.Deserialize<T>(entry.Payload);
        }
        catch { return null; }
    }

    private async Task SetPersistentCacheAsync<T>(string key, T data, CancellationToken ct) where T : class
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var payload = JsonSerializer.Serialize(data);
            var existing = await db.ApiCaches.FirstOrDefaultAsync(c => c.CacheKey == key, ct);

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
        catch { }
    }
}
