using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.Sources;

/// <summary>
/// Royal Road source — a legitimate free web-serial platform (light/web novels). Chapters
/// are TEXT, not images, so this is an <see cref="IMangaSourceService.IsNovel"/> source:
/// the reader renders <see cref="GetChapterTextAsync"/>. Search/browse scrape the fiction
/// listing HTML; the chapter list comes from the fiction page's embedded window.chapters
/// JSON; chapter prose is the .chapter-content div. All endpoints verified live.
/// </summary>
public class RoyalRoadService : IMangaSourceService
{
    public string SourceName => "RoyalRoad";
    public bool IsNovel => true;
    public bool SupportsBrowse => true;

    private const string Base = "https://www.royalroad.com";

    // A fiction's title anchor (carries id, slug, and display title together).
    private static readonly Regex TitleAnchor =
        new(@"<a href=""/fiction/(?<id>\d+)/(?<slug>[a-z0-9-]+)""[^>]*class=""[^""]*font-red-sunglo[^""]*""[^>]*>(?<t>[^<]+)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Cover URLs embed the fiction id: .../covers-full/{id}-{slug}.jpg — map covers by id.
    private static readonly Regex CoverImg =
        new(@"https://www\.royalroadcdn\.com/public/covers[^""]*?/(?<id>\d+)-[^""]+\.(?:jpg|jpeg|png|webp)(?:\?[^""]*)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ChaptersJson =
        new(@"window\.chapters\s*=\s*(?<json>\[[\s\S]*?\]);", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ChapterContent =
        new(@"<div class=""chapter-inner chapter-content"">(?<html>[\s\S]*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OgTitle =
        new(@"<meta property=""og:title"" content=""(?<t>[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TitleTag =
        new(@"<title>(?<t>[^<]+)</title>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OgImage =
        new(@"<meta property=""og:image"" content=""(?<u>[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OgDesc =
        new(@"<meta property=""og:description"" content=""(?<d>[^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly ILogger<RoyalRoadService> _logger;

    public RoyalRoadService(ILogger<RoyalRoadService> logger)
    {
        _logger = logger;
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = true };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Sentrychan/1.0");
    }

    public async Task<List<MangaSearchResult>> SearchAsync(string query, int limit = 20, int page = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var url = $"{Base}/fictions/search?title={Uri.EscapeDataString(query)}&page={Math.Max(1, page)}";
        return await ListAsync(url, limit, ct);
    }

    public async Task<List<MangaSearchResult>> BrowseAsync(string category, int limit = 24, int page = 1, CancellationToken ct = default)
    {
        var path = category.Equals("Latest", StringComparison.OrdinalIgnoreCase) ? "latest-updates" : "best-rated";
        return await ListAsync($"{Base}/fictions/{path}?page={Math.Max(1, page)}", limit, ct);
    }

    private async Task<List<MangaSearchResult>> ListAsync(string url, int limit, CancellationToken ct)
    {
        try
        {
            var html = await _http.GetStringAsync(url, ct);

            // Covers are keyed by the fiction id embedded in their URL.
            var coverById = new Dictionary<string, string>();
            foreach (Match c in CoverImg.Matches(html))
                coverById.TryAdd(c.Groups["id"].Value, c.Value);

            var results = new List<MangaSearchResult>();
            var seen = new HashSet<string>();
            foreach (Match a in TitleAnchor.Matches(html))
            {
                var id = a.Groups["id"].Value;
                if (!seen.Add(id)) continue;
                var title = WebUtility.HtmlDecode(a.Groups["t"].Value).Trim();
                coverById.TryGetValue(id, out var cover);
                results.Add(new MangaSearchResult(id, title, null, null, cover ?? string.Empty, null, null, null, [], false));
                if (results.Count >= limit) break;
            }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RoyalRoad] List failed: {Url}", url);
            return [];
        }
    }

    public async Task<MangaSearchResult?> GetDetailsAsync(string sourceId, CancellationToken ct = default)
    {
        try
        {
            var html = await _http.GetStringAsync($"{Base}/fiction/{sourceId}", ct);
            var title = OgTitle.Match(html) is { Success: true } t
                ? WebUtility.HtmlDecode(t.Groups["t"].Value).Replace(" | Royal Road", "").Trim()
                : TitleTag.Match(html) is { Success: true } tt
                    ? WebUtility.HtmlDecode(tt.Groups["t"].Value).Replace(" | Royal Road", "").Trim() : $"#{sourceId}";
            var cover = OgImage.Match(html) is { Success: true } c ? c.Groups["u"].Value : string.Empty;
            var desc  = OgDesc.Match(html) is { Success: true } d ? WebUtility.HtmlDecode(d.Groups["d"].Value).Trim() : null;
            return new MangaSearchResult(sourceId, title, null, desc, cover, null, null, null, [], false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RoyalRoad] Details failed for {Id}", sourceId);
            return null;
        }
    }

    public async Task<List<MangaChapterInfo>> GetChaptersAsync(string sourceId, string language = "en", CancellationToken ct = default)
    {
        try
        {
            var html = await _http.GetStringAsync($"{Base}/fiction/{sourceId}", ct);
            var m = ChaptersJson.Match(html);
            if (!m.Success) return [];

            using var doc = JsonDocument.Parse(m.Groups["json"].Value);
            var chapters = new List<MangaChapterInfo>();
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                var url = c.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(url)) continue;
                var title = c.TryGetProperty("title", out var t) ? WebUtility.HtmlDecode(t.GetString() ?? "") : "";
                var order = c.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : chapters.Count;
                var num = (order + 1).ToString();
                DateTime? published = c.TryGetProperty("date", out var d) && DateTime.TryParse(d.GetString(), out var dt) ? dt : null;
                // Chapter SourceId = the chapter path; text is fetched from there.
                chapters.Add(new MangaChapterInfo(url, num, order + 1, null, title, "en", "RoyalRoad", 0, published));
            }
            return chapters.OrderBy(c => c.ChapterSort ?? double.MaxValue).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RoyalRoad] Chapters failed for {Id}", sourceId);
            return [];
        }
    }

    public async Task<string?> GetChapterTextAsync(string chapterSourceId, CancellationToken ct = default)
    {
        try
        {
            var html = await _http.GetStringAsync(Base + chapterSourceId, ct);
            var m = ChapterContent.Match(html);
            if (!m.Success) return null;
            var text = HtmlToText(m.Groups["html"].Value);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RoyalRoad] Chapter text failed for {Id}", chapterSourceId);
            return null;
        }
    }

    // Novel source: no image pages.
    public Task<List<string>> GetPageUrlsAsync(string chapterSourceId, bool dataSaver = false, CancellationToken ct = default)
        => Task.FromResult(new List<string>());

    public string GetChapterWebUrl(string chapterSourceId) => Base + chapterSourceId;

    // ── Helpers ─────────────────────────────────────────────────────

    internal static string HtmlToText(string html)
    {
        html = Regex.Replace(html, @"</p\s*>", "\n\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<[^>]+>", string.Empty);
        html = WebUtility.HtmlDecode(html);
        html = Regex.Replace(html, @"[ \t]+\n", "\n");
        html = Regex.Replace(html, @"\n{3,}", "\n\n");
        return html.Trim();
    }

    private static string SlugToTitle(string slug)
    {
        var words = slug.Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : w));
    }
}

