using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.Sources;

/// <summary>
/// Weeb Central source — HTML/HTMX scraping (no public API). Endpoints verified live:
/// quick search POSTs to /search/simple, browse via /hot-series and /latest-updates,
/// chapters via /series/{id}/full-chapter-list, pages via /chapters/{id}/images.
/// SourceIds are the site's ULIDs; covers are deterministic from the series id.
/// Image CDNs (planeptune.us for pages) require a weebcentral.com Referer.
/// </summary>
public class WeebCentralService : IMangaSourceService
{
    public string SourceName => "WeebCentral";
    public string? ImageReferer => "https://weebcentral.com/";
    public bool SupportsBrowse => true;

    private const string Base      = "https://weebcentral.com";
    private const string CoverBase = "https://temp.compsci88.com/cover/fallback";

    private static readonly Regex SeriesLink =
        new(@"href=""(?:https://weebcentral\.com)?/series/(?<id>[A-Z0-9]+)/(?<slug>[^""?]+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // A chapter anchor: the /chapters/{id} link followed by its "Chapter N" label.
    private static readonly Regex ChapterEntry =
        new(@"href=""(?:https://weebcentral\.com)?/chapters/(?<cid>[A-Z0-9]+)""[\s\S]{0,400}?Chapter\s+(?<num>[0-9]+(?:\.[0-9]+)?)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PageImg =
        new(@"src=""(?<u>https://[^""]+\.(?:jpg|jpeg|png|webp))""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OgTitle =
        new(@"<meta[^>]+property=""og:title""[^>]+content=""(?<t>[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OgDesc =
        new(@"<meta[^>]+property=""og:description""[^>]+content=""(?<d>[^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly ILogger<WeebCentralService> _logger;

    public WeebCentralService(ILogger<WeebCentralService> logger)
    {
        _logger = logger;
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Sentrychan/1.0");
    }

    public async Task<List<MangaSearchResult>> SearchAsync(string query, int limit = 20, int page = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || page > 1) return []; // quick-search returns one page
        try
        {
            using var body = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("text", query) });
            using var resp = await _http.PostAsync($"{Base}/search/simple?location=main", body, ct);
            var html = await resp.Content.ReadAsStringAsync(ct);
            return ParseSeriesList(html, limit);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WeebCentral] Search failed for '{Query}'", query);
            return [];
        }
    }

    public async Task<List<MangaSearchResult>> BrowseAsync(string category, int limit = 24, int page = 1, CancellationToken ct = default)
    {
        // Popular is a fixed top list (no paging); Latest paginates by page number.
        var url = category.Equals("Latest", StringComparison.OrdinalIgnoreCase)
            ? $"{Base}/latest-updates/{Math.Max(1, page)}"
            : page > 1 ? null : $"{Base}/hot-series?sort=weekly_views";
        if (url == null) return [];

        try
        {
            var html = await _http.GetStringAsync(url, ct);
            return ParseSeriesList(html, limit);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WeebCentral] Browse {Category} failed", category);
            return [];
        }
    }

    public async Task<MangaSearchResult?> GetDetailsAsync(string sourceId, CancellationToken ct = default)
    {
        try
        {
            var html = await _http.GetStringAsync($"{Base}/series/{sourceId}", ct);
            var title = OgTitle.Match(html) is { Success: true } t ? WebUtility.HtmlDecode(t.Groups["t"].Value.Trim()) : sourceId;
            var desc  = OgDesc.Match(html) is { Success: true } d ? WebUtility.HtmlDecode(d.Groups["d"].Value.Trim()) : null;
            return new MangaSearchResult(sourceId, title, null, desc, $"{CoverBase}/{sourceId}.jpg", null, null, null, [], false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WeebCentral] Details failed for {Id}", sourceId);
            return null;
        }
    }

    public async Task<List<MangaChapterInfo>> GetChaptersAsync(string sourceId, string language = "en", CancellationToken ct = default)
    {
        try
        {
            var html = await _http.GetStringAsync($"{Base}/series/{sourceId}/full-chapter-list", ct);
            var chapters = new List<MangaChapterInfo>();
            var seen = new HashSet<string>();

            foreach (Match m in ChapterEntry.Matches(html))
            {
                var cid = m.Groups["cid"].Value;
                if (!seen.Add(cid)) continue;
                var numStr = m.Groups["num"].Value;
                double? sort = double.TryParse(numStr, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
                chapters.Add(new MangaChapterInfo(cid, numStr, sort, null, null, "en", "WeebCentral", 0, null));
            }

            return chapters.OrderBy(c => c.ChapterSort ?? double.MaxValue).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WeebCentral] Chapters failed for {Id}", sourceId);
            return [];
        }
    }

    public async Task<List<string>> GetPageUrlsAsync(string chapterSourceId, bool dataSaver = false, CancellationToken ct = default)
    {
        var url = $"{Base}/chapters/{chapterSourceId}/images?is_prev=False&current_page=1&reading_style=long_strip";
        try
        {
            var html = await _http.GetStringAsync(url, ct);
            var pages = new List<string>();
            foreach (Match m in PageImg.Matches(html))
                pages.Add(m.Groups["u"].Value);
            return pages;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WeebCentral] Pages failed for {Id}", chapterSourceId);
            return [];
        }
    }

    public string GetChapterWebUrl(string chapterSourceId) => $"{Base}/chapters/{chapterSourceId}";

    // ── Helpers ─────────────────────────────────────────────────────

    private static List<MangaSearchResult> ParseSeriesList(string html, int limit)
    {
        var results = new List<MangaSearchResult>();
        var seen = new HashSet<string>();
        foreach (Match m in SeriesLink.Matches(html))
        {
            var id = m.Groups["id"].Value;
            if (!seen.Add(id)) continue;
            var title = SlugToTitle(m.Groups["slug"].Value);
            results.Add(new MangaSearchResult(id, title, null, null, $"{CoverBase}/{id}.jpg",
                null, null, null, [], false));
            if (results.Count >= limit) break;
        }
        return results;
    }

    private static string SlugToTitle(string slug)
    {
        var words = slug.Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : w));
    }
}

