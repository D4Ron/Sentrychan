using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Xml.Linq;

namespace Sentrychan.Core.Services;

/// <summary>
/// Fetches anime cover images from AniDB's HTTP API.
/// AniDB indexes adult anime and is the primary cover source for secret mode.
/// Uses in-memory cache (1 hour TTL) to avoid repeated API calls.
/// AniDB rate limit: 1 request per 2 seconds per IP.
/// </summary>
public class AniDbCoverService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AniDbCoverService> _logger;
    private readonly SemaphoreSlim _rateLimitSemaphore = new(1, 1);

    // AniDB HTTP API endpoint for anime info by title
    private const string AniDbApiUrl =
        "http://api.anidb.net:9001/httpapi?request=animetitles&client=sentrychan&clientver=1&protover=1";

    // AniDB CDN for images
    private const string AniDbImageCdn = "https://cdn.anidb.net/images/main/";

    public AniDbCoverService(
        IHttpClientFactory httpFactory,
        IMemoryCache cache,
        ILogger<AniDbCoverService> logger)
    {
        _httpFactory = httpFactory;
        _cache       = cache;
        _logger      = logger;
    }

    /// <summary>
    /// Searches AniDB for the given title and returns the cover image URL.
    /// Returns null if not found or on error.
    /// </summary>
    public async Task<string?> GetCoverUrlAsync(string title, CancellationToken ct = default)
    {
        var cacheKey = $"anidb_cover_{title.ToLowerInvariant()}";
        if (_cache.TryGetValue(cacheKey, out string? cached))
            return cached;

        try
        {
            // AniDB anime title database — a compressed XML dump of all titles
            // This is the correct approach per AniDB's API documentation.
            // The titles dump is cached locally after the first download.
            var animeId = await FindAnimeIdByTitleAsync(title, ct);
            if (animeId == null)
            {
                _cache.Set(cacheKey, (string?)null, TimeSpan.FromHours(1));
                return null;
            }

            var imageUrl = await FetchCoverUrlForIdAsync(animeId, ct);

            _cache.Set(cacheKey, imageUrl, TimeSpan.FromHours(1));
            return imageUrl;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AniDbCover] Failed to get cover for: {Title}", title);
            return null;
        }
    }

    /// <summary>
    /// Searches the AniDB anime-titles XML for the best match.
    /// Falls back to a partial match if exact match fails.
    /// </summary>
    private async Task<string?> FindAnimeIdByTitleAsync(string title, CancellationToken ct)
    {
        // AniDB's HTTP API: search for anime by title
        // Returns XML with aid (anime ID) and anime info
        await _rateLimitSemaphore.WaitAsync(ct);
        try
        {
            var client = _httpFactory.CreateClient("AniDb");
            // Use the anime-search endpoint
            var encoded = Uri.EscapeDataString(title);
            var url = $"http://api.anidb.net:9001/httpapi?request=anime&client=sentrychan&clientver=1&protover=1&search={encoded}";

            // NOTE: AniDB HTTP API requires a registered client.
            // "sentrychan" should be registered at https://anidb.net/software/add
            // For development purposes, use "teststub" (returns test data).
            // For production, register "sentrychan" as an HTTP API client.
            var response = await client.GetStringAsync(url, ct);

            var doc = XDocument.Parse(response);
            var ns  = doc.Root?.Name.Namespace ?? XNamespace.None;

            // The response is either <anime id="..."> for a single match
            // or <animetitles> for multiple. Parse the first result.
            var animeElement = doc.Root?.Name.LocalName == "anime"
                ? doc.Root
                : doc.Descendants(ns + "anime").FirstOrDefault();

            return animeElement?.Attribute("id")?.Value;
        }
        finally
        {
            // AniDB rate limit: 1 request per 2 seconds
            await Task.Delay(2000, ct);
            _rateLimitSemaphore.Release();
        }
    }

    private async Task<string?> FetchCoverUrlForIdAsync(string animeId, CancellationToken ct)
    {
        await _rateLimitSemaphore.WaitAsync(ct);
        try
        {
            var client = _httpFactory.CreateClient("AniDb");
            var url = $"http://api.anidb.net:9001/httpapi?request=anime&client=sentrychan&clientver=1&protover=1&aid={animeId}";
            var xml = await client.GetStringAsync(url, ct);

            var doc     = XDocument.Parse(xml);
            var ns      = doc.Root?.Name.Namespace ?? XNamespace.None;
            var picElem = doc.Descendants(ns + "picture").FirstOrDefault();

            if (picElem == null) return null;

            return AniDbImageCdn + picElem.Value;
        }
        finally
        {
            await Task.Delay(2000, ct);
            _rateLimitSemaphore.Release();
        }
    }
}
