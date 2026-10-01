using System.Xml;
using System.Xml.Linq;

namespace Sentrychan.Core.Sources;

/// <summary>What a pasted feed link turned out to be, in words a first-time user can act on.</summary>
public sealed record FeedCheck(bool IsUsable, string Message, int Items = 0, int Downloadable = 0, string? Title = null);

/// <summary>
/// Checks a link someone pasted as an RSS feed before it's added: is it a feed at all (and not the
/// web page it came from), does it list anything, and do its items carry something to download — a
/// .torrent file or a magnet link. The app knows no sites; this only looks at what comes back.
/// </summary>
public static class RssFeedProbe
{
    public static async Task<FeedCheck> CheckAsync(HttpClient http, string url, CancellationToken ct = default)
    {
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new(false, "That isn't a web address. Copy the whole link, starting with https://.");

        string body;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await http.GetAsync(uri, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return new(false, $"The site answered {(int)response.StatusCode} ({response.ReasonPhrase}). Check that the link opens in your browser.");
            body = await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, "The site didn't answer in time. Try again, or check the link in your browser.");
        }
        catch (HttpRequestException ex)
        {
            return new(false, "Couldn't reach it: " + ex.Message);
        }

        return Inspect(body);
    }

    /// <summary>Judges a downloaded body. Separate so it can be tested without a network.</summary>
    public static FeedCheck Inspect(string body)
    {
        XDocument doc;
        try { doc = XDocument.Parse(body, LoadOptions.None); }
        catch (XmlException)
        {
            return new(false, LooksLikeHtml(body)
                ? "That's a web page, not a feed. On that page, look for the RSS link or the orange RSS icon and copy that link instead."
                : "That isn't a feed — what came back isn't RSS. Copy the RSS link itself.");
        }

        var root = doc.Root!;
        if (root.Name.LocalName.Equals("html", StringComparison.OrdinalIgnoreCase))
            return new(false, "That's a web page, not a feed. On that page, look for the RSS link or the orange RSS icon and copy that link instead.");

        var items = root.Descendants().Where(e => e.Name.LocalName is "item" or "entry").ToList();
        var title = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value.Trim();
        if (root.Name.LocalName is not ("rss" or "feed" or "RDF"))
            return new(false, "That's XML, but not an RSS feed. Copy the link marked RSS.");

        if (items.Count == 0)
            return new(true, "It's a feed, but it lists nothing right now. That can be fine for a narrow search; it'll pick up new releases as they appear.", 0, 0, title);

        var downloadable = items.Count(HasDownload);
        if (downloadable == 0)
            return new(false, $"It's a feed of {items.Count} items, but none has a torrent or magnet link, so there's nothing Sentrychan can download from it. Look for a feed of releases (not news or a blog).", items.Count, 0, title);

        return new(true, downloadable == items.Count
            ? $"Looks good: {items.Count} releases, each with a download link."
            : $"Looks usable: {downloadable} of {items.Count} items have a download link.", items.Count, downloadable, title);
    }

    private static bool HasDownload(XElement item) =>
        item.Descendants().Any(e =>
            e.Name.LocalName is "infoHash" or "magnetURI" ||
            (e.Name.LocalName == "enclosure" &&
                ((string?)e.Attribute("type"))?.Contains("bittorrent", StringComparison.OrdinalIgnoreCase) == true) ||
            Mentions(e.Value) || e.Attributes().Any(a => Mentions(a.Value)));

    private static bool Mentions(string text) =>
        text.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ||
        text.Contains(".torrent", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeHtml(string body)
    {
        var head = body.Length > 512 ? body[..512] : body;
        return head.Contains("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) ||
               head.Contains("<html", StringComparison.OrdinalIgnoreCase);
    }
}
