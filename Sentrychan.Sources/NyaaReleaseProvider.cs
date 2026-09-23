using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeHollow.FeedReader;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services;
using FeedType = Sentrychan.Core.Models.FeedType;

namespace Sentrychan.Sources;

/// <summary>
/// Release provider for nyaa.si and its adult sibling. Lives in the source pack rather than
/// Core so that the public build, which ships without this assembly, contacts no index at all.
/// Behaviour is unchanged from when this was Core's NyaaSearchService.
/// </summary>
public class NyaaReleaseProvider : IReleaseProvider
{
    private readonly IEpisodeNormalizer _normalizer;
    private readonly ILogger<NyaaReleaseProvider> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ISecretModeService _secretMode;

    // nyaa XML namespace for seeders/leechers/size/infoHash elements
    private static readonly XNamespace NyaaNamespace =
        XNamespace.Get("https://nyaa.si/xmlns/nyaa");

    private static readonly Regex GroupPattern =
        new(@"^\[([^\]]+)\]", RegexOptions.Compiled);

    // Matches the numeric ID in Nyaa view URLs: https://nyaa.si/view/1234567
    private static readonly Regex NyaaViewIdPattern =
        new(@"nyaa\.si/view/(\d+)", RegexOptions.Compiled);

    // "Seeders:</div> ... <span ...>1234</span>" on a nyaa view page.
    private static readonly Regex NyaaSwarmRow =
        new(@"(Seeders|Leechers|Completed)\s*:\s*</div>\s*<div[^>]*>\s*<span[^>]*>\s*([\d,]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // nyaa RSS honours &p=N (75 items/page); fetch a few pages for a deeper Latest list.
    private const int PagesToFetch = 3;

    private const string DefaultGroups = "SubsPlease,Erai-raws,HorribleSubs";

    private static readonly HttpClient PageHttp = CreatePageClient();

    public NyaaReleaseProvider(
        IEpisodeNormalizer normalizer,
        ILogger<NyaaReleaseProvider> logger,
        IDbContextFactory<AppDbContext> dbFactory,
        ISecretModeService secretMode)
    {
        _normalizer = normalizer;
        _logger     = logger;
        _dbFactory  = dbFactory;
        _secretMode = secretMode;
    }

    public string ProviderName => "Nyaa";

    // ── Search ────────────────────────────────────────────────────

    public bool SupportsSearch => true;

    private static string BaseUrlFor(bool secret) => secret ? "https://sukebei.nyaa.si" : "https://nyaa.si";

    private const string Category = "1_2";

    public async Task<List<ReleaseResult>> SearchAsync(
        string query, string? quality, bool secretMode, CancellationToken ct)
    {
        // If quality is specified, append it so nyaa's own search pre-filters.
        var fullQuery = quality is not null ? $"{query} {quality}" : query;
        var url = $"{BaseUrlFor(secretMode)}/?page=rss&q={Uri.EscapeDataString(fullQuery)}&c={Category}&f=0";
        return await FetchAndParseAsync(url, ct);
    }

    public async Task<List<ReleaseResult>> FindEpisodeAsync(
        string seriesTitle, int episodeNumber, string? quality, CancellationToken ct)
    {
        // Two-digit episode to match the common "Title - 03" naming.
        var epStr = episodeNumber.ToString("D2");
        var query = quality is not null
            ? $"{seriesTitle} {epStr} {quality}"
            : $"{seriesTitle} {epStr}";

        var url = $"{BaseUrlFor(_secretMode.IsSecretModeActive)}/?page=rss&q={Uri.EscapeDataString(query)}&c={Category}&f=0";
        var results = await FetchAndParseAsync(url, ct);

        var preferredGroupsStr = DefaultGroups;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var config = await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == "PreferredReleaseGroups", ct);
            if (config != null && !string.IsNullOrWhiteSpace(config.Value))
                preferredGroupsStr = config.Value;
        }
        catch { }

        var preferredGroups = preferredGroupsStr
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(g => g.ToLowerInvariant())
            .ToList();

        foreach (var r in results)
        {
            r.BaseScore = r.Seeders;
            if (string.IsNullOrEmpty(r.ReleaseGroup)) continue;

            var groupRank = preferredGroups.IndexOf(r.ReleaseGroup.ToLowerInvariant());
            if (groupRank >= 0)
                r.BaseScore += (preferredGroups.Count - groupRank) * 1000;
        }

        // Results that ARE this episode, or a batch that actually contains it.
        return results
            .Where(r => r.EpisodeNumber == episodeNumber
                     || (r.IsBatch && TitleResolverService.BatchContainsEpisode(r.Title, episodeNumber)))
            .OrderByDescending(r => r.BaseScore)
            .ToList();
    }

    // ── Feeds ─────────────────────────────────────────────────────

    public IReadOnlyList<ProviderFeed> DefaultFeeds =>
    [
        new("https://nyaa.si/?page=rss", FeedType.Priority),
        new("https://subsplease.org/rss/?t&h=1080", FeedType.Priority, "1080p"),
    ];

    public string? DefaultPreferredGroups => DefaultGroups;

    public bool IsAdultFeed(string feedUrl) =>
        feedUrl.Contains("sukebei", StringComparison.OrdinalIgnoreCase);

    // SubsPlease's own feed is one clean release per episode, so Latest opens on it.
    public bool IsPreferredLatestFeed(string feedUrl) =>
        feedUrl.Contains("subsplease", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<string> ExtraFeedPages(string feedUrl)
    {
        if (feedUrl.IndexOf("nyaa", StringComparison.OrdinalIgnoreCase) < 0) yield break;
        for (var p = 2; p <= PagesToFetch; p++)
            yield return feedUrl.Contains('?') ? $"{feedUrl}&p={p}" : $"{feedUrl}?p={p}";
    }

    // ── Enrichment ────────────────────────────────────────────────

    /// <summary>
    /// Scrapes seeders/leechers/downloads off a release's nyaa view page, for feeds that
    /// omit swarm data from their RSS. Null when the release has no nyaa page.
    /// </summary>
    public async Task<SwarmStats?> SwarmAsync(string? link, string? viewUrl, CancellationToken ct)
    {
        var m = NyaaViewIdPattern.Match(link ?? string.Empty);
        if (!m.Success) m = NyaaViewIdPattern.Match(viewUrl ?? string.Empty);
        if (!m.Success) return null;

        try
        {
            var html = await PageHttp.GetStringAsync($"https://nyaa.si/view/{m.Groups[1].Value}", ct);

            int seeders = 0, leechers = 0, downloads = 0;
            foreach (Match row in NyaaSwarmRow.Matches(html))
            {
                if (!int.TryParse(row.Groups[2].Value.Replace(",", ""), out var n)) continue;
                switch (row.Groups[1].Value.ToLowerInvariant())
                {
                    case "seeders":   seeders   = n; break;
                    case "leechers":  leechers  = n; break;
                    case "completed": downloads = n; break;
                }
            }
            return seeders > 0 || leechers > 0 || downloads > 0
                ? new SwarmStats(seeders, leechers, downloads)
                : null;
        }
        catch { return null; }   // page down or layout changed
    }

    // ── Private ───────────────────────────────────────────────────

    private static HttpClient CreatePageClient()
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

    private async Task<List<ReleaseResult>> FetchAndParseAsync(string url, CancellationToken ct)
    {
        try
        {
            _logger.LogDebug("[Nyaa] Fetching: {Url}", url);
            var feed = await FeedReader.ReadAsync(url, ct);
            var results = new List<ReleaseResult>();

            foreach (var item in feed.Items)
            {
                try { results.Add(ParseItem(item)); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Nyaa] Failed to parse feed item: {Title}", item.Title);
                }
            }

            _logger.LogDebug("[Nyaa] Parsed {Count} results from {Url}", results.Count, url);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Nyaa] Feed fetch failed for URL: {Url}", url);
            return [];
        }
    }

    private ReleaseResult ParseItem(FeedItem item)
    {
        var title = item.Title ?? string.Empty;

        // FeedReader keeps the raw XML for elements it doesn't parse natively; the
        // nyaa: namespace children live there.
        int    seeders  = 0;
        int    leechers = 0;
        string sizeRaw  = string.Empty;
        string infoHash = string.Empty;

        if (item.SpecificItem?.Element is XElement rawElement)
        {
            seeders  = ParseInt(rawElement.Element(NyaaNamespace + "seeders")?.Value);
            leechers = ParseInt(rawElement.Element(NyaaNamespace + "leechers")?.Value);
            sizeRaw  = rawElement.Element(NyaaNamespace + "size")?.Value ?? string.Empty;
            infoHash = rawElement.Element(NyaaNamespace + "infoHash")?.Value ?? string.Empty;
        }

        // <link> may be a magnet; the <guid> is the view page. Hash-based download URLs
        // 404 on nyaa, so the working .torrent URL is built from the numeric view id.
        var link    = item.Link ?? string.Empty;
        var guidUrl = item.Id   ?? string.Empty;

        var magnetLink = link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ? link : string.Empty;

        var torrentUrl = string.Empty;
        var idMatch = NyaaViewIdPattern.Match(guidUrl);
        if (idMatch.Success)
        {
            var baseHost = guidUrl.Contains("sukebei") ? "https://sukebei.nyaa.si" : "https://nyaa.si";
            torrentUrl = $"{baseHost}/download/{idMatch.Groups[1].Value}.torrent";
        }

        return new ReleaseResult
        {
            Title         = title,
            MagnetLink    = magnetLink,
            TorrentUrl    = torrentUrl,
            InfoHash      = infoHash,
            Seeders       = seeders,
            Leechers      = leechers,
            SizeDisplay   = sizeRaw,
            SizeBytes     = ParseSizeToBytes(sizeRaw),
            ReleaseGroup  = ParseReleaseGroup(title),
            Resolution    = DetectResolution(title),
            EpisodeNumber = _normalizer.ExtractEpisodeNumber(title),
            // Single source of truth for batch detection — the strict resolver rule.
            IsBatch       = TitleResolverService.IsBatchRelease(title, 0),
            IsTrusted     = title.Contains("[SubsPlease]") || title.Contains("[Erai-raws]"),
            PublishedAt   = item.PublishingDate ?? DateTime.UtcNow
        };
    }

    private static string ParseReleaseGroup(string title)
    {
        var match = GroupPattern.Match(title);
        return match.Success ? match.Groups[1].Value : "Unknown";
    }

    private static string? DetectResolution(string title)
    {
        if (title.Contains("1080p", StringComparison.OrdinalIgnoreCase)) return "1080p";
        if (title.Contains("720p",  StringComparison.OrdinalIgnoreCase)) return "720p";
        if (title.Contains("480p",  StringComparison.OrdinalIgnoreCase)) return "480p";
        if (title.Contains("2160p", StringComparison.OrdinalIgnoreCase)) return "2160p";
        return null;
    }

    private static int ParseInt(string? value) =>
        int.TryParse(value, out var n) ? n : 0;

    private static long ParseSizeToBytes(string sizeRaw)
    {
        if (string.IsNullOrWhiteSpace(sizeRaw)) return 0;
        var parts = sizeRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return 0;
        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var value)) return 0;

        return parts[1].ToUpperInvariant() switch
        {
            "GIB" or "GB" => (long)(value * 1_073_741_824),
            "MIB" or "MB" => (long)(value * 1_048_576),
            "KIB" or "KB" => (long)(value * 1_024),
            _             => (long)value
        };
    }
}
