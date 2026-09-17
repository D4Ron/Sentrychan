using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.Sources;

/// <summary>
/// Roliascan source — a web-novel aggregator (light/web novels as TEXT). An
/// <see cref="IMangaSourceService.IsNovel"/> source. WordPress/Tailwind theme: search via
/// ?s=&amp;post_type=wp-manga, series at /manga/{slug}/, and the FULL chapter list comes
/// from the chapter reader's &lt;select&gt; dropdown (no fragile ajax). Prose lives in the
/// .reader-text container. Carries titles WebNovel hosts (e.g. Shadow Slave) — a pirate
/// mirror, so it's inherently fragile and legally grey. Verified live against Shadow Slave.
/// </summary>
public class RoliascanService : IMangaSourceService
{
    public string SourceName => "Roliascan";
    public bool IsNovel => true;

    private const string Base = "https://roliascan.com";

    private static readonly Regex SeriesLink =
        new(@"href=""https://roliascan\.com/manga/(?<slug>[a-z0-9-]+)/""(?<rest>[\s\S]{0,600})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NearImg =
        new(@"<img[^>]+src=""(?<u>https://[^""]+)""[^>]*(?:alt=""(?<alt>[^""]*)"")?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReadLink =
        new(@"https://roliascan\.com/read/[a-z0-9-]+/[^""'\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ChapterOption =
        new(@"<option[^>]*value=""(?<u>https://roliascan\.com/read/[^""]+)""[^>]*>\s*(?<t>[^<]+?)\s*</option>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NumInText = new(@"(?<n>\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex Paragraph = new(@"<p[^>]*>(?<h>[\s\S]*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OgTitle =
        new(@"<meta property=""og:title"" content=""(?<t>[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OgImage =
        new(@"<meta property=""og:image"" content=""(?<u>[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TitleTag = new(@"<title>(?<t>[^<]+)</title>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly ILogger<RoliascanService> _logger;

    public RoliascanService(ILogger<RoliascanService> logger)
    {
        _logger = logger;
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = true };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Sentrychan/1.0");
    }

    public async Task<List<MangaSearchResult>> SearchAsync(string query, int limit = 20, int page = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || page > 1) return []; // WP search is a single page
        try
        {
            var url = $"{Base}/?s={Uri.EscapeDataString(query)}&post_type=wp-manga";
            var html = await _http.GetStringAsync(url, ct);
            var results = new List<MangaSearchResult>();
            var seen = new HashSet<string>();
            foreach (Match m in SeriesLink.Matches(html))
            {
                var slug = m.Groups["slug"].Value;
                if (!seen.Add(slug)) continue;
                var rest = m.Groups["rest"].Value;
                var img = NearImg.Match(rest);
                var cover = img.Success ? img.Groups["u"].Value : string.Empty;
                var title = img.Success && !string.IsNullOrWhiteSpace(img.Groups["alt"].Value)
                    ? WebUtility.HtmlDecode(img.Groups["alt"].Value).Trim() : SlugToTitle(slug);
                results.Add(new MangaSearchResult(slug, title, null, null, cover, null, null, null, [], false));
                if (results.Count >= limit) break;
            }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Roliascan] Search failed for '{Query}'", query);
            return [];
        }
    }

    public async Task<MangaSearchResult?> GetDetailsAsync(string sourceId, CancellationToken ct = default)
    {
        try
        {
            var html = await _http.GetStringAsync($"{Base}/manga/{sourceId}/", ct);
            var title = OgTitle.Match(html) is { Success: true } og ? WebUtility.HtmlDecode(og.Groups["t"].Value).Trim()
                : TitleTag.Match(html) is { Success: true } tt ? WebUtility.HtmlDecode(tt.Groups["t"].Value).Split('|', '-')[0].Trim()
                : SlugToTitle(sourceId);
            var cover = OgImage.Match(html) is { Success: true } c ? c.Groups["u"].Value : string.Empty;
            return new MangaSearchResult(sourceId, title, null, null, cover, null, null, null, [], false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Roliascan] Details failed for {Id}", sourceId);
            return null;
        }
    }

    public async Task<List<MangaChapterInfo>> GetChaptersAsync(string sourceId, string language = "en", CancellationToken ct = default)
    {
        try
        {
            // The series page exposes one reader link; the reader page's <select> lists ALL chapters.
            var seriesHtml = await _http.GetStringAsync($"{Base}/manga/{sourceId}/", ct);
            var firstRead = ReadLink.Match(seriesHtml);
            if (!firstRead.Success) return [];

            var readerHtml = await _http.GetStringAsync(firstRead.Value, ct);
            var chapters = new List<MangaChapterInfo>();
            var seen = new HashSet<string>();
            foreach (Match o in ChapterOption.Matches(readerHtml))
            {
                var path = StripDomain(o.Groups["u"].Value);
                if (!seen.Add(path)) continue;
                var label = WebUtility.HtmlDecode(o.Groups["t"].Value).Trim();   // "Ch. 12"
                double? sort = NumInText.Match(label) is { Success: true } n
                    && double.TryParse(n.Groups["n"].Value, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
                var num = sort?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? label;
                chapters.Add(new MangaChapterInfo(path, num, sort, null, null, "en", "Roliascan", 0, null));
            }
            return chapters.OrderBy(c => c.ChapterSort ?? double.MaxValue).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Roliascan] Chapters failed for {Id}", sourceId);
            return [];
        }
    }

    public async Task<string?> GetChapterTextAsync(string chapterSourceId, CancellationToken ct = default)
    {
        try
        {
            var html = await _http.GetStringAsync(Base + chapterSourceId, ct);
            var start = html.IndexOf("reader-text", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;

            var rest = html.Substring(start);
            // Cut off before the comments / chapter-select chrome so their <p> tags aren't slurped in.
            var end = rest.Length;
            foreach (var marker in new[] { "id=\"comments\"", "<select", "comment-respond", "class=\"comments" })
            {
                var i = rest.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (i > 0 && i < end) end = i;
            }
            rest = rest[..end];

            var paras = Paragraph.Matches(rest)
                .Select(m => HtmlToText(m.Groups["h"].Value))
                .Where(s => s.Length > 0);
            var text = string.Join("\n\n", paras);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Roliascan] Chapter text failed for {Id}", chapterSourceId);
            return null;
        }
    }

    public Task<List<string>> GetPageUrlsAsync(string chapterSourceId, bool dataSaver = false, CancellationToken ct = default)
        => Task.FromResult(new List<string>());

    public string GetChapterWebUrl(string chapterSourceId) => Base + chapterSourceId;

    // ── Helpers ─────────────────────────────────────────────────────

    private static string StripDomain(string url) =>
        url.StartsWith(Base, StringComparison.OrdinalIgnoreCase) ? url[Base.Length..] : url;

    private static string HtmlToText(string html)
    {
        html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<[^>]+>", string.Empty);
        html = WebUtility.HtmlDecode(html);
        return html.Replace(' ', ' ').Trim();
    }

    private static string SlugToTitle(string slug)
    {
        var words = slug.Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : w));
    }
}

