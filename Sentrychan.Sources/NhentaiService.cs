using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.Sources;

/// <summary>
/// nhentai source (adult — secret mode only). Uses the v2 JSON API (the v1 API is
/// deprecated). A gallery is a single work, so it maps to a manga with ONE chapter
/// holding all pages. Image/thumbnail CDNs require a Referer. Shapes verified live.
/// </summary>
public class NhentaiService : IMangaSourceService
{
    public string SourceName => "nhentai";
    public bool IsAdultSource => true;
    public string? ImageReferer => "https://nhentai.net/";

    private const string ApiBase   = "https://nhentai.net/api/v2";
    private const string ImageHost = "https://i.nhentai.net";
    private const string ThumbHost = "https://t.nhentai.net";

    private readonly HttpClient _http;
    private readonly ILogger<NhentaiService> _logger;

    public NhentaiService(ILogger<NhentaiService> logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Sentrychan/1.0");
    }

    public async Task<List<MangaSearchResult>> SearchAsync(string query, int limit = 20, int page = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var url = $"{ApiBase}/search?query={Uri.EscapeDataString(query)}&page={Math.Max(1, page)}";
        try
        {
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var results = new List<MangaSearchResult>();
            if (doc.RootElement.TryGetProperty("result", out var arr))
                foreach (var g in arr.EnumerateArray())
                {
                    var r = ParseGallery(g);
                    if (r != null) results.Add(r);
                    if (results.Count >= limit) break;
                }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[nhentai] Search failed for '{Query}'", query);
            return [];
        }
    }

    public bool SupportsBrowse => true;

    public async Task<List<MangaSearchResult>> BrowseAsync(string category, int limit = 24, int page = 1, CancellationToken ct = default)
    {
        // The v2 search endpoint doubles as browse: a wildcard query + a sort.
        var sort = category.Equals("Latest", StringComparison.OrdinalIgnoreCase) ? "date" : "popular";
        var url = $"{ApiBase}/search?query=*&sort={sort}&page={Math.Max(1, page)}";
        try
        {
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var results = new List<MangaSearchResult>();
            if (doc.RootElement.TryGetProperty("result", out var arr))
                foreach (var g in arr.EnumerateArray())
                {
                    var r = ParseGallery(g);
                    if (r != null) results.Add(r);
                    if (results.Count >= limit) break;
                }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[nhentai] Browse {Category} failed", category);
            return [];
        }
    }

    public async Task<MangaSearchResult?> GetDetailsAsync(string sourceId, CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync($"{ApiBase}/galleries/{sourceId}", ct);
            using var doc = JsonDocument.Parse(json);
            return ParseGallery(doc.RootElement);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[nhentai] Details failed for {Id}", sourceId);
            return null;
        }
    }

    public Task<List<MangaChapterInfo>> GetChaptersAsync(string sourceId, string language = "en", CancellationToken ct = default)
    {
        // A gallery is one work → one "chapter" (its pages fetched on read).
        return Task.FromResult(new List<MangaChapterInfo>
        {
            new(sourceId, "1", 1, null, "Gallery", "en", "nhentai", 0, null)
        });
    }

    public async Task<List<string>> GetPageUrlsAsync(string chapterSourceId, bool dataSaver = false, CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync($"{ApiBase}/galleries/{chapterSourceId}", ct);
            using var doc = JsonDocument.Parse(json);
            var pages = new List<string>();
            if (doc.RootElement.TryGetProperty("pages", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var p in arr.EnumerateArray())
                    if (p.TryGetProperty("path", out var path) && path.GetString() is { Length: > 0 } rel)
                        pages.Add($"{ImageHost}/{rel}");
            return pages;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[nhentai] Pages failed for {Id}", chapterSourceId);
            return [];
        }
    }

    public string GetChapterWebUrl(string chapterSourceId) => $"https://nhentai.net/g/{chapterSourceId}/";

    // ── Parsing ─────────────────────────────────────────────────────

    private static MangaSearchResult? ParseGallery(JsonElement g)
    {
        if (!g.TryGetProperty("id", out var idEl)) return null;
        var id = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64().ToString() : idEl.GetString();
        if (string.IsNullOrEmpty(id)) return null;

        var en = g.TryGetProperty("english_title", out var e) ? e.GetString() : null;
        var jp = g.TryGetProperty("japanese_title", out var j) ? j.GetString() : null;
        var title = !string.IsNullOrWhiteSpace(en) ? en! : (!string.IsNullOrWhiteSpace(jp) ? jp! : $"#{id}");

        // Thumbnail is a relative path ("galleries/{mid}/thumb.webp"); cover on detail is under "cover".
        string cover = string.Empty;
        if (g.TryGetProperty("thumbnail", out var th) && th.ValueKind == JsonValueKind.String)
            cover = $"{ThumbHost}/{th.GetString()}";
        else if (g.TryGetProperty("cover", out var cv) && cv.TryGetProperty("path", out var cp)
                 && cp.GetString() is { Length: > 0 } cpath)
            cover = $"{ThumbHost}/{cpath}";

        return new MangaSearchResult(id, title, jp, null, cover, null, null, null, [], true);
    }
}

