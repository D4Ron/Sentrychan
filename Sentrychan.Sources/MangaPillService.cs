using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.Sources;

/// <summary>
/// MangaPill source — HTML scraping (the site has no public API). Server-rendered, so
/// plain HTTP works. SourceIds are the site's own path fragments: a manga's SourceId is
/// "{id}/{slug}" (from /manga/{id}/{slug}); a chapter's is the path after /chapters/.
/// Patterns verified live against the current markup.
/// </summary>
public class MangaPillService : IMangaSourceService
{
    public string SourceName => "MangaPill";
    public string? ImageReferer => "https://mangapill.com/";

    private const string Base = "https://mangapill.com";

    private readonly HttpClient _http;
    private readonly ILogger<MangaPillService> _logger;

    private static readonly Regex ResultBlock =
        new(@"href=""/manga/(?<id>\d+)/(?<slug>[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex CoverImg =
        new(@"<img[^>]+src=""(?<u>https://[^""]+\.(?:jpg|jpeg|png|webp)(?:\?[^""]*)?)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TitleH1 =
        new(@"<h1[^>]*>(?<t>[^<]+)</h1>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ChapterLink =
        new(@"href=""/chapters/(?<cid>[^""]+)""[^>]*>\s*(?<label>[^<]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ChapterNum =
        new(@"chapter-(?<n>[\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PageImg =
        new(@"data-src=""(?<u>https://[^""]+\.(?:jpg|jpeg|png|webp)(?:\?[^""]*)?)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Description =
        new(@"<p[^>]*class=""[^""]*text-secondary[^""]*""[^>]*>(?<d>[^<]+)</p>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public MangaPillService(ILogger<MangaPillService> logger)
    {
        _logger = logger;
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        // A browser-like UA is required or the site 403s.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Sentrychan/1.0");
    }

    public async Task<List<MangaSearchResult>> SearchAsync(string query, int limit = 20, int page = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var url = $"{Base}/search?q={Uri.EscapeDataString(query)}&type=&status=&page={Math.Max(1, page)}";
        try
        {
            var html = await _http.GetStringAsync(url, ct);
            var results = new List<MangaSearchResult>();
            var seen = new HashSet<string>();

            // The results grid pairs each /manga/ link with a nearby cover + title.
            foreach (Match m in ResultBlock.Matches(html))
            {
                var sourceId = $"{m.Groups["id"].Value}/{m.Groups["slug"].Value}";
                if (!seen.Add(sourceId)) continue;

                // Slug → readable title as a fallback; the card's own title is better.
                var title = SlugToTitle(m.Groups["slug"].Value);
                var cover = FindNearbyCover(html, m.Index);

                results.Add(new MangaSearchResult(sourceId, title, null, null, cover, null, null, null, [], false));
                if (results.Count >= limit) break;
            }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MangaPill] Search failed for '{Query}'", query);
            return [];
        }
    }

    public async Task<MangaSearchResult?> GetDetailsAsync(string sourceId, CancellationToken ct = default)
    {
        try
        {
            var html = await _http.GetStringAsync($"{Base}/manga/{sourceId}", ct);
            var title = TitleH1.Match(html) is { Success: true } t ? WebUtility.HtmlDecode(t.Groups["t"].Value.Trim())
                        : SlugToTitle(sourceId);
            var cover = CoverImg.Match(html) is { Success: true } c ? c.Groups["u"].Value : string.Empty;
            var desc  = Description.Match(html) is { Success: true } d ? WebUtility.HtmlDecode(d.Groups["d"].Value.Trim()) : null;
            return new MangaSearchResult(sourceId, title, null, desc, cover, null, null, null, [], false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MangaPill] Details failed for {Id}", sourceId);
            return null;
        }
    }

    public async Task<List<MangaChapterInfo>> GetChaptersAsync(string sourceId, string language = "en", CancellationToken ct = default)
    {
        try
        {
            var html = await _http.GetStringAsync($"{Base}/manga/{sourceId}", ct);
            var chapters = new List<MangaChapterInfo>();
            var seen = new HashSet<string>();

            foreach (Match m in ChapterLink.Matches(html))
            {
                var cid = m.Groups["cid"].Value;
                if (!seen.Add(cid)) continue;

                var numMatch = ChapterNum.Match(cid);
                var numStr = numMatch.Success ? numMatch.Groups["n"].Value : "";
                double? sort = double.TryParse(numStr, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

                chapters.Add(new MangaChapterInfo(cid, numStr, sort, null, null, "en", "MangaPill", 0, null));
            }

            // Site lists newest-first; the app wants ascending.
            return chapters.OrderBy(c => c.ChapterSort ?? double.MaxValue).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MangaPill] Chapters failed for {Id}", sourceId);
            return [];
        }
    }

    public async Task<List<string>> GetPageUrlsAsync(string chapterSourceId, bool dataSaver = false, CancellationToken ct = default)
    {
        try
        {
            var html = await _http.GetStringAsync($"{Base}/chapters/{chapterSourceId}", ct);
            var pages = new List<string>();
            foreach (Match m in PageImg.Matches(html))
                pages.Add(m.Groups["u"].Value);
            return pages;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MangaPill] Pages failed for {Id}", chapterSourceId);
            return [];
        }
    }

    public string GetChapterWebUrl(string chapterSourceId) => $"{Base}/chapters/{chapterSourceId}";

    // ── Helpers ─────────────────────────────────────────────────────

    private static string SlugToTitle(string slugOrPath)
    {
        var slug = slugOrPath.Contains('/') ? slugOrPath[(slugOrPath.IndexOf('/') + 1)..] : slugOrPath;
        var words = slug.Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => char.ToUpper(w[0]) + w[1..]));
    }

    private static string FindNearbyCover(string html, int fromIndex)
    {
        // Covers sit close to their link in the markup; scan a window around it.
        var start = Math.Max(0, fromIndex - 400);
        var window = html.Substring(start, Math.Min(800, html.Length - start));
        var m = CoverImg.Match(window);
        return m.Success ? m.Groups["u"].Value : string.Empty;
    }
}

