using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeHollow.FeedReader;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Microsoft.EntityFrameworkCore;
using Sentrychan.Core.Data;

namespace Sentrychan.Core.Services;

public class NyaaSearchService : INyaaSearchService
{
    private readonly IEpisodeNormalizer _normalizer;
    private readonly ILogger<NyaaSearchService> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    // nyaa XML namespace for seeders/leechers/size/infoHash elements
    private static readonly XNamespace NyaaNamespace =
        XNamespace.Get("https://nyaa.si/xmlns/nyaa");

    private static readonly Regex GroupPattern =
        new(@"^\[([^\]]+)\]", RegexOptions.Compiled);


    // Matches the numeric ID in Nyaa view URLs: https://nyaa.si/view/1234567
    private static readonly Regex NyaaViewIdPattern =
        new(@"nyaa\.si/view/(\d+)", RegexOptions.Compiled);

    // Base URLs — category 1_2 = Anime English, 2_2 = Adult English (secret mode)
    private const string NyaaBase    = "https://nyaa.si/?page=rss&c=1_2&f=0&q=";
    private const string SukebeiBase = "https://sukebei.nyaa.si/?page=rss&c=2_2&f=0&q=";

    private readonly ISecretModeService _secretMode;

    public NyaaSearchService(
        IEpisodeNormalizer normalizer, 
        ILogger<NyaaSearchService> logger, 
        IDbContextFactory<AppDbContext> dbFactory,
        ISecretModeService secretMode)
    {
        _normalizer = normalizer;
        _logger     = logger;
        _dbFactory  = dbFactory;
        _secretMode = secretMode;
    }

    private string BaseUrl => _secretMode.IsSecretModeActive
        ? "https://sukebei.nyaa.si"
        : "https://nyaa.si";

    private string DefaultCategory => _secretMode.IsSecretModeActive ? "1_2" : "1_2";

    public async Task<List<NyaaResult>> SearchAsync(
        string query,
        string? quality = null,
        bool secretMode = false,
        CancellationToken ct = default)
    {
        // Build the search query:
        // If quality is specified, append it so nyaa's own search pre-filters.
        // E.g. "Frieren 1080p" → returns mostly 1080p results.
        var fullQuery = quality is not null
            ? $"{query} {quality}"
            : query;

        var url = $"{BaseUrl}/?page=rss&q={Uri.EscapeDataString(fullQuery)}&c={DefaultCategory}&f=0";

        return await FetchAndParseAsync(url, ct);
    }

    public async Task<List<NyaaResult>> FindEpisodeAsync(
        string seriesTitle,
        int episodeNumber,
        string? quality = null,
        CancellationToken ct = default)
    {
        // Include the episode number formatted as 2-digit to match SubsPlease pattern.
        // E.g. "Frieren 03 1080p" rather than "Frieren 3 1080p"
        var epStr = episodeNumber.ToString("D2");
        var query = quality is not null
            ? $"{seriesTitle} {epStr} {quality}"
            : $"{seriesTitle} {epStr}";

        var url = $"{BaseUrl}/?page=rss&q={Uri.EscapeDataString(query)}&c={DefaultCategory}&f=0";
        var results = await FetchAndParseAsync(url, ct);

        // Load preferred groups
        string preferredGroupsStr = "SubsPlease,Erai-raws,HorribleSubs";
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var config = await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == "PreferredReleaseGroups", ct);
            if (config != null && !string.IsNullOrWhiteSpace(config.Value))
                preferredGroupsStr = config.Value;
        }
        catch { }

        var preferredGroups = preferredGroupsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                                .Select(g => g.ToLowerInvariant())
                                                .ToList();

        foreach (var r in results)
        {
            r.BaseScore = r.Seeders;
            
            if (!string.IsNullOrEmpty(r.ReleaseGroup))
            {
                var groupRank = preferredGroups.IndexOf(r.ReleaseGroup.ToLowerInvariant());
                if (groupRank >= 0)
                {
                    int boost = (preferredGroups.Count - groupRank) * 1000;
                    r.BaseScore += boost;
                }
            }
        }

        // Filter to results that ARE this episode, or a batch that actually contains it.
        // (Previously any batch matched — combined with over-eager batch detection this
        // pulled in wrong-episode releases.)
        return results
            .Where(r => r.EpisodeNumber == episodeNumber
                     || (r.IsBatch && TitleResolverService.BatchContainsEpisode(r.Title, episodeNumber)))
            .OrderByDescending(r => r.BaseScore)
            .ToList();
    }

    // ── Private ───────────────────────────────────────────────────

    private async Task<List<NyaaResult>> FetchAndParseAsync(string url, CancellationToken ct)
    {
        try
        {
            _logger.LogDebug("[Nyaa] Fetching: {Url}", url);
            var feed = await FeedReader.ReadAsync(url, ct);
            var results = new List<NyaaResult>();

            foreach (var item in feed.Items)
            {
                try
                {
                    results.Add(ParseItem(item));
                }
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

    private NyaaResult ParseItem(FeedItem item)
    {
        var title = item.Title ?? string.Empty;

        // ── Parse nyaa: namespace elements ────────────────────────
        // FeedReader stores raw XML for items it doesn't natively parse.
        // We access the underlying XElement to read nyaa: namespace children.
        int    seeders   = 0;
        int    leechers  = 0;
        string sizeRaw   = string.Empty;
        string infoHash  = string.Empty;

        if (item.SpecificItem?.Element is XElement rawElement)
        {
            seeders  = ParseInt(rawElement.Element(NyaaNamespace + "seeders")?.Value);
            leechers = ParseInt(rawElement.Element(NyaaNamespace + "leechers")?.Value);
            sizeRaw  = rawElement.Element(NyaaNamespace + "size")?.Value ?? string.Empty;
            infoHash = rawElement.Element(NyaaNamespace + "infoHash")?.Value ?? string.Empty;
        }

        // ── Determine magnet vs torrent URL ───────────────────────
        // nyaa RSS <link> is the magnet link. The GUID (<guid>) is the
        // view page URL: https://nyaa.si/view/1234567. We extract the
        // numeric ID from the GUID to build a working .torrent download URL.
        // NOTE: hash-based URLs (https://nyaa.si/download/{sha1}.torrent)
        //       return 404 — only numeric ID URLs work.
        var link       = item.Link ?? string.Empty;
        var guidUrl    = item.Id   ?? string.Empty;

        var magnetLink = link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
            ? link
            : string.Empty;

        // Try to extract numeric ID from guid for a working .torrent URL
        string torrentUrl = string.Empty;
        var idMatch = NyaaViewIdPattern.Match(guidUrl);
        if (idMatch.Success)
        {
            var numericId = idMatch.Groups[1].Value;
            var baseHost  = guidUrl.Contains("sukebei") ? "https://sukebei.nyaa.si" : "https://nyaa.si";
            torrentUrl = $"{baseHost}/download/{numericId}.torrent";
        }

        // ── Parse metadata from title ──────────────────────────────
        var releaseGroup = ParseReleaseGroup(title);
        var resolution   = DetectResolution(title);
        var episodeNum   = _normalizer.ExtractEpisodeNumber(title);
        // Single source of truth for batch detection — the strict resolver rule.
        // The old local RangePattern flagged space-padded season markers like
        // "S2 - 04" as batches, which then bypassed episode matching downstream.
        var isBatch      = TitleResolverService.IsBatchRelease(title, 0);

        // ── Parse size to bytes ────────────────────────────────────
        var sizeBytes = ParseSizeToBytes(sizeRaw);

        return new NyaaResult
        {
            Title         = title,
            MagnetLink    = magnetLink,
            TorrentUrl    = torrentUrl,
            InfoHash      = infoHash,
            Seeders       = seeders,
            Leechers      = leechers,
            SizeDisplay   = sizeRaw,
            SizeBytes     = sizeBytes,
            ReleaseGroup  = releaseGroup,
            Resolution    = resolution,
            EpisodeNumber = episodeNum,
            IsBatch       = isBatch,
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
