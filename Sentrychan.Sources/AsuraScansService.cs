using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.Sources;

/// <summary>
/// Asura Scans source — a manhwa/webtoon aggregator. The site is an Astro app: listing,
/// sorting, and search all run through its JSON API (api.asurascans.com/api/series), while
/// the /comics/ pages still server-render the chapter list and the reader still exposes the
/// page images. So search/browse use the API (the old /browse HTML ignored ?order= and
/// ?name= — sorting/filtering are client-side — which made Popular == Latest and search
/// return the unfiltered catalog); chapters/pages stay HTML-scraped. SourceIds are the
/// "/comics/{slug}-{hash}" path segment. Image CDN needs an asurascans.com Referer.
/// </summary>
public class AsuraScansService : IMangaSourceService
{
    public string SourceName => "AsuraScans";
    public string? ImageReferer => "https://asurascans.com/";
    public bool SupportsBrowse => true;

    private const string Base    = "https://asurascans.com";
    private const string ApiBase = "https://api.asurascans.com";

    private static readonly Regex ChapterLink =
        new(@"href=""/comics/(?<full>[^""]+/chapter/(?<num>[0-9]+(?:\.[0-9]+)?))""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Pages live in an HTML-encoded payload (delimited by &quot; not "), so the URL must
    // stop at the first extension and not run greedily across the whole blob.
    private static readonly Regex ChapterImg =
        new(@"(?<u>https://cdn\.asurascans\.com/asura-images/chapters/[^""'\s&<>\\]+?\.(?:jpg|jpeg|png|webp)(?:\?v=\d+)?)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TrailingHash = new(@"-[0-9a-fA-F]{6,}$", RegexOptions.Compiled);
    private static readonly Regex HtmlTag = new("<[^>]+>", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly ILogger<AsuraScansService> _logger;

    public AsuraScansService(ILogger<AsuraScansService> logger)
    {
        _logger = logger;
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = true };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Sentrychan/1.0");
        // The API is fronted by the site; identify the origin like the browser client does.
        _http.DefaultRequestHeaders.Referrer = new Uri(Base + "/");
    }

    // ── Search / browse (JSON API) ──────────────────────────────────

    public async Task<List<MangaSearchResult>> SearchAsync(string query, int limit = 20, int page = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var qs = $"search={Uri.EscapeDataString(query)}&limit={limit}&offset={Math.Max(0, page - 1) * limit}";
        return await FetchSeriesAsync(qs, ct);
    }

    public async Task<List<MangaSearchResult>> BrowseAsync(string category, int limit = 24, int page = 1, CancellationToken ct = default)
    {
        // The site's own sort keys: "update" = Latest Update, "popular" = Popular.
        var sort = category.Equals("Latest", StringComparison.OrdinalIgnoreCase) ? "update" : "popular";
        var qs = $"sort={sort}&order=desc&limit={limit}&offset={Math.Max(0, page - 1) * limit}";
        return await FetchSeriesAsync(qs, ct);
    }

    private async Task<List<MangaSearchResult>> FetchSeriesAsync(string queryString, CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync($"{ApiBase}/api/series?{queryString}", ct);
            using var doc = JsonDocument.Parse(json);
            var results = new List<MangaSearchResult>();
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var s in data.EnumerateArray())
                {
                    var r = ParseApiSeries(s);
                    if (r != null) results.Add(r);
                }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Asura] API series failed: {Query}", queryString);
            return [];
        }
    }

    public async Task<MangaSearchResult?> GetDetailsAsync(string sourceId, CancellationToken ct = default)
    {
        var slug = TrailingHash.Replace(sourceId, "");   // "{slug}-{hash}" → "{slug}"
        try
        {
            var json = await _http.GetStringAsync($"{ApiBase}/api/series/{slug}", ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("series", out var s) ? ParseApiSeries(s) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Asura] Details failed for {Id}", sourceId);
            return null;
        }
    }

    // ── Chapters / pages (still HTML — verified working on the Astro site) ───

    public async Task<List<MangaChapterInfo>> GetChaptersAsync(string sourceId, string language = "en", CancellationToken ct = default)
    {
        try
        {
            var html = await _http.GetStringAsync($"{Base}/comics/{sourceId}", ct);
            var chapters = new List<MangaChapterInfo>();
            var seen = new HashSet<string>();
            foreach (Match m in ChapterLink.Matches(html))
            {
                var full = m.Groups["full"].Value;   // "{seriesId}/chapter/{n}"
                if (!seen.Add(full)) continue;
                var numStr = m.Groups["num"].Value;
                double? sort = double.TryParse(numStr, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
                chapters.Add(new MangaChapterInfo(full, numStr, sort, null, null, "en", "AsuraScans", 0, null));
            }
            return chapters.OrderBy(c => c.ChapterSort ?? double.MaxValue).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Asura] Chapters failed for {Id}", sourceId);
            return [];
        }
    }

    public async Task<List<string>> GetPageUrlsAsync(string chapterSourceId, bool dataSaver = false, CancellationToken ct = default)
    {
        try
        {
            var html = await _http.GetStringAsync($"{Base}/comics/{chapterSourceId}", ct);
            var pages = new List<string>();
            var seen = new HashSet<string>();
            foreach (Match m in ChapterImg.Matches(html))
            {
                var u = m.Groups["u"].Value;
                if (seen.Add(u)) pages.Add(u);
            }
            return pages;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Asura] Pages failed for {Id}", chapterSourceId);
            return [];
        }
    }

    public string GetChapterWebUrl(string chapterSourceId) => $"{Base}/comics/{chapterSourceId}";

    // ── Parsing ─────────────────────────────────────────────────────

    private static MangaSearchResult? ParseApiSeries(JsonElement s)
    {
        if (!s.TryGetProperty("title", out var tEl)) return null;
        var title = tEl.GetString();
        if (string.IsNullOrWhiteSpace(title)) return null;

        // Keep SourceId as the "/comics/{slug}-{hash}" segment so chapter/page URLs resolve.
        string sourceId;
        if (s.TryGetProperty("public_url", out var pu) && pu.GetString() is { Length: > 0 } url)
        {
            var i = url.IndexOf("comics/", StringComparison.Ordinal);
            sourceId = i >= 0 ? url[(i + "comics/".Length)..].Trim('/') : url.Trim('/');
        }
        else
        {
            sourceId = s.TryGetProperty("slug", out var sl) ? sl.GetString() ?? title! : title!;
        }

        var cover = s.TryGetProperty("cover", out var cv) ? cv.GetString() ?? string.Empty : string.Empty;

        string? desc = null;
        if (s.TryGetProperty("description", out var d) && d.GetString() is { Length: > 0 } raw)
            desc = WebUtility.HtmlDecode(HtmlTag.Replace(raw, " ")).Trim();

        var status = s.TryGetProperty("status", out var st) ? st.GetString() : null;

        double? chapters = s.TryGetProperty("chapter_count", out var cc) && cc.ValueKind == JsonValueKind.Number
            ? cc.GetDouble() : null;

        int? year = null;
        if (s.TryGetProperty("created_at", out var ca) && DateTime.TryParse(ca.GetString(), out var dt) && dt.Year > 1)
            year = dt.Year;

        return new MangaSearchResult(sourceId, title!, null, desc, cover, status, year, chapters, [], false);
    }
}

