using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.Sources;

/// <summary>
/// MangaDex source connector (https://api.mangadex.org — keyless public API).
/// Field shapes verified live: /manga search, /manga/{id} details, /manga/{id}/feed
/// chapters. Covers come from uploads.mangadex.org/covers/{id}/{fileName}.
/// </summary>
public class MangaDexService : IMangaSourceService
{
    public string SourceName => "MangaDex";

    private const string ApiBase   = "https://api.mangadex.org";
    private const string CoverBase = "https://uploads.mangadex.org/covers";

    private readonly HttpClient _http;
    private readonly ILogger<MangaDexService> _logger;

    public MangaDexService(ILogger<MangaDexService> logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // MangaDex asks clients to identify themselves.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Sentrychan/1.0 (+manga)");
    }

    public async Task<List<MangaSearchResult>> SearchAsync(string query, int limit = 20, int page = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var offset = Math.Max(0, page - 1) * limit;
        var url = $"{ApiBase}/manga?title={Uri.EscapeDataString(query)}" +
                  $"&limit={limit}&offset={offset}&includes[]=cover_art" +
                  "&contentRating[]=safe&contentRating[]=suggestive&contentRating[]=erotica" +
                  "&order[relevance]=desc";
        try
        {
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var results = new List<MangaSearchResult>();
            if (doc.RootElement.TryGetProperty("data", out var data))
                foreach (var m in data.EnumerateArray())
                {
                    var parsed = ParseManga(m);
                    if (parsed != null) results.Add(parsed);
                }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MangaDex] Search failed for '{Query}'", query);
            return [];
        }
    }

    public bool SupportsBrowse => true;

    public async Task<List<MangaSearchResult>> BrowseAsync(string category, int limit = 24, int page = 1, CancellationToken ct = default)
    {
        // Popular = most-followed; Latest = most recently updated chapter.
        var order = category.Equals("Latest", StringComparison.OrdinalIgnoreCase)
            ? "order[latestUploadedChapter]=desc"
            : "order[followedCount]=desc";
        var offset = Math.Max(0, page - 1) * limit;
        var url = $"{ApiBase}/manga?{order}&limit={limit}&offset={offset}&includes[]=cover_art" +
                  "&contentRating[]=safe&contentRating[]=suggestive&contentRating[]=erotica";
        try
        {
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var results = new List<MangaSearchResult>();
            if (doc.RootElement.TryGetProperty("data", out var data))
                foreach (var m in data.EnumerateArray())
                {
                    var parsed = ParseManga(m);
                    if (parsed != null) results.Add(parsed);
                }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MangaDex] Browse {Category} failed", category);
            return [];
        }
    }

    public async Task<MangaSearchResult?> GetDetailsAsync(string sourceId, CancellationToken ct = default)
    {
        var url = $"{ApiBase}/manga/{sourceId}?includes[]=cover_art";
        try
        {
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("data", out var m) ? ParseManga(m) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MangaDex] Details failed for {Id}", sourceId);
            return null;
        }
    }

    public async Task<List<MangaChapterInfo>> GetChaptersAsync(string sourceId, string language = "en", CancellationToken ct = default)
    {
        var all = new List<MangaChapterInfo>();
        int offset = 0;
        const int page = 100; // MangaDex feed max

        try
        {
            while (true)
            {
                var url = $"{ApiBase}/manga/{sourceId}/feed" +
                          $"?translatedLanguage[]={language}" +
                          "&order[chapter]=asc&includes[]=scanlation_group" +
                          "&contentRating[]=safe&contentRating[]=suggestive&contentRating[]=erotica&contentRating[]=pornographic" +
                          $"&limit={page}&offset={offset}";
                var json = await _http.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("data", out var data)) break;
                int count = 0;
                foreach (var c in data.EnumerateArray())
                {
                    count++;
                    var info = ParseChapter(c);
                    if (info != null) all.Add(info);
                }

                var total = doc.RootElement.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
                offset += page;
                if (count == 0 || offset >= total) break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MangaDex] Chapter feed failed for {Id}", sourceId);
        }

        return all;
    }

    public async Task<List<string>> GetPageUrlsAsync(string chapterSourceId, bool dataSaver = false, CancellationToken ct = default)
    {
        // MangaDex's @Home delivery: /at-home/server/{id} hands back a temporary node
        // baseUrl + the chapter hash + the ordered filenames. Full page URL is
        // {baseUrl}/data/{hash}/{filename} (or /data-saver/ for the compressed set).
        var url = $"{ApiBase}/at-home/server/{chapterSourceId}";
        try
        {
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var baseUrl = root.TryGetProperty("baseUrl", out var b) ? b.GetString() : null;
            if (string.IsNullOrEmpty(baseUrl) || !root.TryGetProperty("chapter", out var ch))
                return [];

            var hash = ch.TryGetProperty("hash", out var h) ? h.GetString() : null;
            if (string.IsNullOrEmpty(hash)) return [];

            var arrayName = dataSaver ? "dataSaver" : "data";
            var segment   = dataSaver ? "data-saver" : "data";
            if (!ch.TryGetProperty(arrayName, out var files) || files.ValueKind != JsonValueKind.Array)
                return [];

            var pages = new List<string>();
            foreach (var f in files.EnumerateArray())
            {
                var name = f.GetString();
                if (!string.IsNullOrEmpty(name))
                    pages.Add($"{baseUrl}/{segment}/{hash}/{name}");
            }
            return pages;
        }
        catch (Exception ex)
        {
            // Licensed/external chapters 404 here — that's expected, not an error.
            _logger.LogInformation(ex, "[MangaDex] No in-app pages for chapter {Id} (likely external)", chapterSourceId);
            return [];
        }
    }

    public string GetChapterWebUrl(string chapterSourceId) => $"https://mangadex.org/chapter/{chapterSourceId}";

    // ── Parsing ─────────────────────────────────────────────────────

    private static MangaSearchResult? ParseManga(JsonElement m)
    {
        if (!m.TryGetProperty("id", out var idEl)) return null;
        var id = idEl.GetString() ?? "";
        if (!m.TryGetProperty("attributes", out var a)) return null;

        var titles   = ReadLocalizedList(a, "title");
        var altList  = ReadAltTitles(a);
        var display  = PickEnglish(titles) ?? titles.Values.FirstOrDefault()
                       ?? altList.FirstOrDefault() ?? "(untitled)";
        var original = titles.TryGetValue("ja", out var jp) ? jp
                       : titles.TryGetValue("ja-ro", out var jpr) ? jpr : null;

        string? desc = null;
        if (a.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.Object)
            desc = PickEnglish(ReadLocalized(d)) ?? ReadLocalized(d).Values.FirstOrDefault();

        var status = a.TryGetProperty("status", out var s) ? s.GetString() : null;
        int? year  = a.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : null;
        double? last = ParseNum(a.TryGetProperty("lastChapter", out var lc) ? lc.GetString() : null);

        var rating = a.TryGetProperty("contentRating", out var cr) ? cr.GetString() : "safe";
        bool adult = rating is "erotica" or "pornographic";

        var cover = ExtractCoverUrl(m, id);

        return new MangaSearchResult(id, display, original, desc, cover, status, year, last, altList, adult);
    }

    private static MangaChapterInfo? ParseChapter(JsonElement c)
    {
        if (!c.TryGetProperty("id", out var idEl)) return null;
        var id = idEl.GetString() ?? "";
        if (!c.TryGetProperty("attributes", out var a)) return null;

        var num  = a.TryGetProperty("chapter", out var ch) ? ch.GetString() : null;
        var vol  = a.TryGetProperty("volume", out var v) ? v.GetString() : null;
        var titl = a.TryGetProperty("title", out var tt) ? tt.GetString() : null;
        var lang = a.TryGetProperty("translatedLanguage", out var tl) ? tl.GetString() ?? "en" : "en";
        var pages = a.TryGetProperty("pages", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;

        DateTime? published = null;
        if (a.TryGetProperty("publishAt", out var pa) && pa.ValueKind == JsonValueKind.String
            && DateTime.TryParse(pa.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal, out var dt))
            published = dt;

        string? group = null;
        if (c.TryGetProperty("relationships", out var rels))
            foreach (var r in rels.EnumerateArray())
                if (r.TryGetProperty("type", out var rt) && rt.GetString() == "scanlation_group"
                    && r.TryGetProperty("attributes", out var ra)
                    && ra.TryGetProperty("name", out var rn))
                { group = rn.GetString(); break; }

        return new MangaChapterInfo(id, num ?? "", ParseNum(num), vol, titl, lang, group, pages, published);
    }

    private static string ExtractCoverUrl(JsonElement m, string mangaId)
    {
        if (m.TryGetProperty("relationships", out var rels))
            foreach (var r in rels.EnumerateArray())
                if (r.TryGetProperty("type", out var t) && t.GetString() == "cover_art"
                    && r.TryGetProperty("attributes", out var a)
                    && a.TryGetProperty("fileName", out var fn))
                {
                    var file = fn.GetString();
                    if (!string.IsNullOrEmpty(file))
                        // .512.jpg = the resized thumbnail, plenty for a card.
                        return $"{CoverBase}/{mangaId}/{file}.512.jpg";
                }
        return string.Empty;
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static Dictionary<string, string> ReadLocalized(JsonElement obj)
    {
        var map = new Dictionary<string, string>();
        if (obj.ValueKind == JsonValueKind.Object)
            foreach (var p in obj.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String)
                    map[p.Name] = p.Value.GetString() ?? "";
        return map;
    }

    private static Dictionary<string, string> ReadLocalizedList(JsonElement attrs, string prop) =>
        attrs.TryGetProperty(prop, out var el) ? ReadLocalized(el) : new();

    private static List<string> ReadAltTitles(JsonElement attrs)
    {
        var list = new List<string>();
        if (attrs.TryGetProperty("altTitles", out var alts) && alts.ValueKind == JsonValueKind.Array)
            foreach (var entry in alts.EnumerateArray())
                foreach (var v in ReadLocalized(entry).Values)
                    if (!string.IsNullOrWhiteSpace(v)) list.Add(v);
        return list;
    }

    private static string? PickEnglish(Dictionary<string, string> localized) =>
        localized.TryGetValue("en", out var en) && !string.IsNullOrWhiteSpace(en) ? en : null;

    private static double? ParseNum(string? s) =>
        double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : null;
}

