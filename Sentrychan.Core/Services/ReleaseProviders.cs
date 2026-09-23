using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.Services;

/// <summary>
/// Fans calls out to whatever <see cref="IReleaseProvider"/>s source packs registered.
/// Deliberately holds no provider of its own — see <see cref="IReleaseProvider"/> for why.
/// A failing provider is logged and skipped rather than allowed to break its caller.
/// </summary>
public sealed class ReleaseProviders : IReleaseProviders
{
    private readonly ILogger<ReleaseProviders> _logger;
    private readonly List<IReleaseProvider> _providers = [];
    private readonly object _gate = new();

    public ReleaseProviders(ILogger<ReleaseProviders> logger) => _logger = logger;

    public IReadOnlyList<IReleaseProvider> Providers
    {
        get { lock (_gate) return _providers.ToArray(); }
    }

    public void Add(IReleaseProvider provider)
    {
        lock (_gate)
        {
            if (_providers.Any(p => string.Equals(
                    p.ProviderName, provider.ProviderName, StringComparison.OrdinalIgnoreCase)))
                return;
            _providers.Add(provider);
        }
    }

    private List<IReleaseProvider> Searchers => Providers.Where(p => p.SupportsSearch).ToList();

    public bool HasSearch => Searchers.Count > 0;

    // ── Search ────────────────────────────────────────────────────

    public Task<List<ReleaseResult>> SearchAsync(
        string query, string? quality = null, bool secretMode = false, CancellationToken ct = default)
        => GatherAsync(p => p.SearchAsync(query, quality, secretMode, ct), rankByScore: false);

    public Task<List<ReleaseResult>> FindEpisodeAsync(
        string seriesTitle, int episodeNumber, string? quality = null, CancellationToken ct = default)
        => GatherAsync(p => p.FindEpisodeAsync(seriesTitle, episodeNumber, quality, ct), rankByScore: true);

    private async Task<List<ReleaseResult>> GatherAsync(
        Func<IReleaseProvider, Task<List<ReleaseResult>>> call, bool rankByScore)
    {
        var searchers = Searchers;
        if (searchers.Count == 0) return [];

        // One provider: hand its list back untouched so its own ordering stands.
        if (searchers.Count == 1) return await SafeListAsync(searchers[0], call);

        var batches = await Task.WhenAll(searchers.Select(p => SafeListAsync(p, call)));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = batches.SelectMany(b => b).Where(r => seen.Add(KeyOf(r))).ToList();
        return rankByScore ? merged.OrderByDescending(r => r.BaseScore).ToList() : merged;
    }

    private static string KeyOf(ReleaseResult r) =>
        !string.IsNullOrEmpty(r.InfoHash) ? r.InfoHash : r.Title;

    private async Task<List<ReleaseResult>> SafeListAsync(
        IReleaseProvider provider, Func<IReleaseProvider, Task<List<ReleaseResult>>> call)
    {
        try { return await call(provider) ?? []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Releases] provider {Name} failed", provider.ProviderName);
            return [];
        }
    }

    // ── Feeds ─────────────────────────────────────────────────────

    public IReadOnlyList<ProviderFeed> DefaultFeeds =>
        Providers.SelectMany(p => Guard(() => p.DefaultFeeds, Array.Empty<ProviderFeed>())).ToList();

    public string? DefaultPreferredGroups =>
        Providers.Select(p => Guard(() => p.DefaultPreferredGroups, null))
                 .FirstOrDefault(g => !string.IsNullOrWhiteSpace(g));

    public bool IsAdultFeed(string feedUrl) =>
        !string.IsNullOrEmpty(feedUrl) && Providers.Any(p => Guard(() => p.IsAdultFeed(feedUrl), false));

    public bool IsPreferredLatestFeed(string feedUrl) =>
        !string.IsNullOrEmpty(feedUrl) && Providers.Any(p => Guard(() => p.IsPreferredLatestFeed(feedUrl), false));

    public IEnumerable<string> FeedPages(string feedUrl)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { feedUrl };
        yield return feedUrl;
        foreach (var p in Providers)
            foreach (var extra in Guard(() => p.ExtraFeedPages(feedUrl).ToList(), []))
                if (seen.Add(extra)) yield return extra;
    }

    // ── Enrichment ────────────────────────────────────────────────

    public Task<string?> CoverFromTitleAsync(string rawTitle, CancellationToken ct = default)
        => FirstAsync(p => p.CoverFromTitleAsync(rawTitle, ct));

    public Task<string?> CoverFromPageAsync(string pageHtml, CancellationToken ct = default)
        => FirstAsync(p => p.CoverFromPageAsync(pageHtml, ct));

    public Task<SwarmStats?> SwarmAsync(string? link, string? viewUrl, CancellationToken ct = default)
        => FirstAsync(p => p.SwarmAsync(link, viewUrl, ct));

    /// <summary>First non-null answer across providers, in load order.</summary>
    private async Task<T?> FirstAsync<T>(Func<IReleaseProvider, Task<T?>> call) where T : class
    {
        foreach (var p in Providers)
        {
            try
            {
                var value = await call(p);
                if (value != null) return value;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Releases] provider {Name} enrichment failed", p.ProviderName);
            }
        }
        return null;
    }

    private T Guard<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Releases] provider metadata read failed");
            return fallback;
        }
    }
}
