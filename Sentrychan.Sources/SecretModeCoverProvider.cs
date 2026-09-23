using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.Sources;

/// <summary>
/// Cover-art lookups for secret-mode releases, moved out of the UI's Latest Arrivals view so
/// the public build carries no site-specific lookups. Behaviour is unchanged; the UI still
/// caches results on disk, this only memoises within a session.
/// </summary>
public class SecretModeCoverProvider : IReleaseProvider
{
    public string ProviderName => "SecretModeCovers";

    private static readonly HttpClient Http = CreateClient();
    private static readonly ConcurrentDictionary<string, string?> Memo = new();

    // Product codes such as SSIS-001.
    private static readonly Regex CatalogueCodePattern =
        new(@"\b([A-Za-z]{2,6})-(\d{2,5})\b", RegexOptions.Compiled);

    // Gallery links in a torrent description: e-hentai.org/g/{gid}/{token}/
    private static readonly Regex GalleryPattern =
        new(@"(?:e-hentai|exhentai)\.org/g/(\d+)/([0-9a-fA-F]{10})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// A catalogue code maps to a deterministic cover URL on the publisher's image CDN. A
    /// content-length gate rejects the ~19KB "now printing" placeholder served for unknown codes.
    /// </summary>
    public async Task<string?> CoverFromTitleAsync(string rawTitle, CancellationToken ct)
    {
        var m = CatalogueCodePattern.Match(rawTitle);
        if (!m.Success) return null;

        var cid = m.Groups[1].Value.ToLowerInvariant() + m.Groups[2].Value.PadLeft(5, '0');
        if (Memo.TryGetValue($"code:{cid}", out var cached)) return cached;

        string? result = null;
        var url = $"https://pics.dmm.co.jp/digital/video/{cid}/{cid}pl.jpg";
        try
        {
            // Header-only fetch — check size without downloading the whole image.
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.IsSuccessStatusCode && (resp.Content.Headers.ContentLength ?? 0) >= 25000)
                result = url;
        }
        catch { /* CDN miss — leave null */ }

        Memo[$"code:{cid}"] = result;
        return result;
    }

    /// <summary>
    /// Doujin uploads rarely embed a cover image but usually link a gallery, and the gallery
    /// site exposes a keyless metadata API that returns a real thumbnail.
    /// </summary>
    public async Task<string?> CoverFromPageAsync(string pageHtml, CancellationToken ct)
    {
        var m = GalleryPattern.Match(pageHtml);
        if (!m.Success) return null;

        var gid   = m.Groups[1].Value;
        var token = m.Groups[2].Value;
        if (Memo.TryGetValue($"gallery:{gid}:{token}", out var cached)) return cached;

        string? thumb = null;
        try
        {
            var payload = $"{{\"method\":\"gdata\",\"gidlist\":[[{gid},\"{token}\"]],\"namespace\":1}}";
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync("https://api.e-hentai.org/api.php", content, ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("gmetadata", out var meta)
                    && meta.GetArrayLength() > 0
                    && meta[0].TryGetProperty("thumb", out var t))
                {
                    var v = t.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) thumb = v;
                }
            }
        }
        catch { /* gallery removed or API busy */ }

        Memo[$"gallery:{gid}:{token}"] = thumb;
        return thumb;
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip
                                   | System.Net.DecompressionMethods.Deflate
                                   | System.Net.DecompressionMethods.Brotli
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Sentrychan/2.0");
        return c;
    }
}
