using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.Sources;

/// <summary>
/// E-Hentai source (adult — secret mode only). A gallery is one work → one "chapter"
/// holding all pages. Metadata comes from the JSON gdata API (api.e-hentai.org); search
/// and browse scrape the site's HTML; page images are resolved through the site's
/// two-step chain (gallery page → /s/ image pages → the full image on a Hath node).
/// SourceIds are "{gid}/{token}". The site's "offensive content" interstitial is
/// bypassed with the nw=1 cookie. Endpoints + the whole page chain verified live.
/// </summary>
public class EHentaiService : IMangaSourceService
{
    public string SourceName => "E-Hentai";
    public bool IsAdultSource => true;
    public string? ImageReferer => "https://e-hentai.org/";
    public bool SupportsBrowse => true;

    private const string Base    = "https://e-hentai.org";
    private const string ApiBase = "https://api.e-hentai.org/api.php";

    // A gallery permalink: /g/{gid}/{token}/
    private static readonly Regex GalleryLink =
        new(@"e-hentai\.org/g/(?<gid>[0-9]+)/(?<token>[0-9a-f]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // An image-page link inside a gallery: /s/{key}/{gid}-{page}
    private static readonly Regex ImagePageLink =
        new(@"e-hentai\.org/s/(?<key>[0-9a-f]+)/(?<gid>[0-9]+)-(?<page>[0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // The full image on an /s/ page.
    private static readonly Regex FullImg =
        new(@"id=""img""\s+src=""(?<u>[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly ILogger<EHentaiService> _logger;

    // e-hentai paginates by CURSOR (?next=<last-gid>), not page number — &page= is ignored,
    // which made "load more" re-fetch page 1. Since load-more is sequential, cache the
    // cursor that reaches the page after the last one served, per query/browse key.
    private readonly ConcurrentDictionary<string, (int Page, string Cursor)> _nextCursor = new();

    public EHentaiService(ILogger<EHentaiService> logger)
    {
        _logger = logger;
        var cookies = new CookieContainer();
        // Bypass the content-warning interstitial that otherwise replaces listings.
        cookies.Add(new Cookie("nw", "1", "/", "e-hentai.org"));
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = cookies,
            UseCookies = true
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Sentrychan/1.0");
    }

    // ── Search / browse ─────────────────────────────────────────────

    public async Task<List<MangaSearchResult>> SearchAsync(string query, int limit = 20, int page = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var key = "s:" + query;
        var cursor = CursorFor(key, page);
        var url = $"{Base}/?f_search={Uri.EscapeDataString(query)}" + (cursor != null ? $"&next={cursor}" : "");
        var results = await ListFromHtmlAsync(url, limit, ct);
        RememberCursor(key, page, results);
        return results;
    }

    public async Task<List<MangaSearchResult>> BrowseAsync(string category, int limit = 24, int page = 1, CancellationToken ct = default)
    {
        // Popular is a fixed "right now" list (no paging).
        if (!category.Equals("Latest", StringComparison.OrdinalIgnoreCase))
            return page > 1 ? [] : await ListFromHtmlAsync($"{Base}/popular", limit, ct);

        // Latest = the paged front page, walked by ?next= cursor.
        var key = "browse:latest";
        var cursor = CursorFor(key, page);
        var url = $"{Base}/" + (cursor != null ? $"?next={cursor}" : "");
        var results = await ListFromHtmlAsync(url, limit, ct);
        RememberCursor(key, page, results);
        return results;
    }

    /// <summary>The ?next= cursor to reach <paramref name="page"/>, or null for page 1 (or a gap).</summary>
    private string? CursorFor(string key, int page)
        => page > 1 && _nextCursor.TryGetValue(key, out var e) && e.Page == page ? e.Cursor : null;

    /// <summary>Record the cursor (last gid served) needed to fetch the page AFTER this one.</summary>
    private void RememberCursor(string key, int page, List<MangaSearchResult> results)
    {
        if (results.Count == 0) return;
        var lastGid = results[^1].SourceId.Split('/')[0];   // SourceId = "{gid}/{token}"
        _nextCursor[key] = (page + 1, lastGid);
    }

    /// <summary>
    /// Pull the ordered gallery ids from a listing page, then hydrate them in one gdata
    /// API call — far more robust than scraping titles/thumbs out of the listing markup.
    /// </summary>
    private async Task<List<MangaSearchResult>> ListFromHtmlAsync(string url, int limit, CancellationToken ct)
    {
        try
        {
            var html = await _http.GetStringAsync(url, ct);
            var ids = new List<(string Gid, string Token)>();
            var seen = new HashSet<string>();
            foreach (Match m in GalleryLink.Matches(html))
            {
                var gid = m.Groups["gid"].Value;
                if (!seen.Add(gid)) continue;
                ids.Add((gid, m.Groups["token"].Value));
                if (ids.Count >= limit) break;
            }
            return ids.Count == 0 ? [] : await HydrateAsync(ids, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[E-Hentai] Listing failed: {Url}", url);
            return [];
        }
    }

    public async Task<MangaSearchResult?> GetDetailsAsync(string sourceId, CancellationToken ct = default)
    {
        var (gid, token) = SplitId(sourceId);
        if (gid == null) return null;
        var list = await HydrateAsync([(gid, token!)], ct);
        return list.FirstOrDefault();
    }

    /// <summary>gdata: gallery metadata for up to 25 galleries per call.</summary>
    private async Task<List<MangaSearchResult>> HydrateAsync(List<(string Gid, string Token)> ids, CancellationToken ct)
    {
        try
        {
            var gidlist = string.Join(",", ids.Select(i => $"[{i.Gid},\"{i.Token}\"]"));
            var payload = $"{{\"method\":\"gdata\",\"gidlist\":[{gidlist}],\"namespace\":1}}";
            using var body = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(ApiBase, body, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var results = new List<MangaSearchResult>();
            if (doc.RootElement.TryGetProperty("gmetadata", out var arr))
                foreach (var g in arr.EnumerateArray())
                {
                    var r = ParseMeta(g);
                    if (r != null) results.Add(r);
                }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[E-Hentai] gdata hydrate failed");
            return [];
        }
    }

    // ── Chapters / pages ────────────────────────────────────────────

    public async Task<List<MangaChapterInfo>> GetChaptersAsync(string sourceId, string language = "en", CancellationToken ct = default)
    {
        // A gallery is one work → one "chapter" (its pages fetched on read).
        var details = await GetDetailsAsync(sourceId, ct);
        var pages = details?.LastChapter.HasValue == true ? (int)details.LastChapter.Value : 0;
        return
        [
            new MangaChapterInfo(sourceId, "1", 1, null, "Gallery", "en", "E-Hentai", pages, null)
        ];
    }

    public async Task<List<string>> GetPageUrlsAsync(string chapterSourceId, bool dataSaver = false, CancellationToken ct = default)
    {
        var (gid, token) = SplitId(chapterSourceId);
        if (gid == null) return [];

        try
        {
            // 1) Walk the gallery's listing pages, collecting the /s/ image-page links in
            //    order. Stop when a page adds nothing new (handles any per-page count).
            var imagePages = new List<string>();
            var seen = new HashSet<string>();
            for (int p = 0; p < 25; p++) // safety cap ~ up to a few thousand images
            {
                var html = await _http.GetStringAsync($"{Base}/g/{gid}/{token}/?p={p}", ct);
                int added = 0;
                foreach (Match m in ImagePageLink.Matches(html))
                {
                    var link = $"{Base}/s/{m.Groups["key"].Value}/{m.Groups["gid"].Value}-{m.Groups["page"].Value}";
                    if (seen.Add(link)) { imagePages.Add(link); added++; }
                }
                if (added == 0) break;
            }
            if (imagePages.Count == 0) return [];

            // 2) Resolve each /s/ page to its full image URL, order preserved, with a
            //    small concurrency cap to stay well under e-hentai's rate limits.
            var urls = new string?[imagePages.Count];
            using var gate = new SemaphoreSlim(4);
            var tasks = imagePages.Select(async (link, idx) =>
            {
                await gate.WaitAsync(ct);
                try { urls[idx] = await ResolveImageAsync(link, ct); }
                catch (Exception ex) { _logger.LogInformation(ex, "[E-Hentai] page resolve failed: {Link}", link); }
                finally { gate.Release(); }
            });
            await Task.WhenAll(tasks);

            return urls.Where(u => !string.IsNullOrEmpty(u)).Select(u => u!).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[E-Hentai] Pages failed for {Id}", chapterSourceId);
            return [];
        }
    }

    private async Task<string?> ResolveImageAsync(string imagePageUrl, CancellationToken ct)
    {
        var html = await _http.GetStringAsync(imagePageUrl, ct);
        var m = FullImg.Match(html);
        return m.Success ? WebUtility.HtmlDecode(m.Groups["u"].Value) : null;
    }

    public string GetChapterWebUrl(string chapterSourceId)
    {
        var (gid, token) = SplitId(chapterSourceId);
        return gid == null ? Base : $"{Base}/g/{gid}/{token}/";
    }

    // ── Parsing ─────────────────────────────────────────────────────

    private static MangaSearchResult? ParseMeta(JsonElement g)
    {
        if (!g.TryGetProperty("gid", out var gidEl)) return null;
        var gid = gidEl.ValueKind == JsonValueKind.Number ? gidEl.GetInt64().ToString() : gidEl.GetString();
        var token = g.TryGetProperty("token", out var tk) ? tk.GetString() : null;
        if (string.IsNullOrEmpty(gid) || string.IsNullOrEmpty(token)) return null;

        var title = g.TryGetProperty("title", out var t) ? WebUtility.HtmlDecode(t.GetString() ?? "") : "";
        var titleJpn = g.TryGetProperty("title_jpn", out var tj) ? WebUtility.HtmlDecode(tj.GetString() ?? "") : null;
        if (string.IsNullOrWhiteSpace(title)) title = !string.IsNullOrWhiteSpace(titleJpn) ? titleJpn! : $"#{gid}";

        var cover = g.TryGetProperty("thumb", out var th) ? th.GetString() ?? string.Empty : string.Empty;
        var category = g.TryGetProperty("category", out var c) ? c.GetString() : null;

        int? filecount = null;
        if (g.TryGetProperty("filecount", out var fc))
            filecount = fc.ValueKind == JsonValueKind.Number ? fc.GetInt32()
                      : int.TryParse(fc.GetString(), out var fcn) ? fcn : null;

        int? year = null;
        if (g.TryGetProperty("posted", out var po) &&
            long.TryParse(po.ValueKind == JsonValueKind.Number ? po.GetInt64().ToString() : po.GetString(), out var epoch))
            year = DateTimeOffset.FromUnixTimeSeconds(epoch).Year;

        // LastChapter carries the page count (a gallery = one chapter of N pages).
        return new MangaSearchResult($"{gid}/{token}", title, titleJpn, category,
            cover, null, year, filecount, [], true);
    }

    private static (string? Gid, string? Token) SplitId(string sourceId)
    {
        var parts = sourceId.Split('/', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : (null, null);
    }
}

