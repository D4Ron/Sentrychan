using Sentrychan.Core.Models;

namespace Sentrychan.Core.Interfaces;

/// <summary>A feed a provider suggests adding on a fresh install, once it is loaded.</summary>
public record ProviderFeed(string Url, FeedType Type, string? PreferredQuality = null);

/// <summary>Swarm numbers for a release whose feed did not report them.</summary>
public record SwarmStats(int Seeders, int Leechers, int Downloads);

/// <summary>
/// Plug-in contract for talking to a specific release index.
///
/// THE APP SHIPS NO IMPLEMENTATION OF THIS. Providers come only from source packs loaded by
/// PluginSourceLoader — exactly like <see cref="IMangaSourceService"/>. That is what makes
/// the public build genuinely neutral: with no pack installed, nothing below is ever called,
/// no index is contacted and no feed is suggested. Do not add an implementation to Core, UI
/// or App; put it in Sentrychan.Sources.
///
/// Every member except <see cref="ProviderName"/> has a default, so a provider implements
/// only the capabilities it actually offers.
/// </summary>
public interface IReleaseProvider
{
    string ProviderName { get; }

    // ── Search ────────────────────────────────────────────────────
    bool SupportsSearch => false;

    Task<List<ReleaseResult>> SearchAsync(
        string query, string? quality, bool secretMode, CancellationToken ct)
        => Task.FromResult(new List<ReleaseResult>());

    Task<List<ReleaseResult>> FindEpisodeAsync(
        string seriesTitle, int episodeNumber, string? quality, CancellationToken ct)
        => Task.FromResult(new List<ReleaseResult>());

    // ── Feeds ─────────────────────────────────────────────────────
    /// <summary>Feeds to add on a fresh install (only when the user has none yet).</summary>
    IReadOnlyList<ProviderFeed> DefaultFeeds => Array.Empty<ProviderFeed>();

    /// <summary>Release-group preference to seed on a fresh install, comma separated.</summary>
    string? DefaultPreferredGroups => null;

    /// <summary>Whether a feed belongs to an adult index, and so is gated behind secret mode.</summary>
    bool IsAdultFeed(string feedUrl) => false;

    /// <summary>Further page URLs for a feed this provider knows how to page. Excludes the feed itself.</summary>
    IEnumerable<string> ExtraFeedPages(string feedUrl) => Array.Empty<string>();

    /// <summary>Whether a feed should be pre-selected the first time the Latest page opens.</summary>
    bool IsPreferredLatestFeed(string feedUrl) => false;

    // ── Enrichment ────────────────────────────────────────────────
    /// <summary>Cover art derivable from a release title alone. Null if this provider can't tell.</summary>
    Task<string?> CoverFromTitleAsync(string rawTitle, CancellationToken ct)
        => Task.FromResult<string?>(null);

    /// <summary>Cover art derivable from a release page's HTML. Null if this provider can't tell.</summary>
    Task<string?> CoverFromPageAsync(string pageHtml, CancellationToken ct)
        => Task.FromResult<string?>(null);

    /// <summary>Swarm numbers for a release the feed omitted them for. Null if unknown.</summary>
    Task<SwarmStats?> SwarmAsync(string? link, string? viewUrl, CancellationToken ct)
        => Task.FromResult<SwarmStats?>(null);
}

/// <summary>
/// What the rest of the app talks to: every loaded <see cref="IReleaseProvider"/> behind one
/// interface. With none loaded, every call returns empty/false/null and
/// <see cref="HasSearch"/> is false, so callers can say "no search provider installed"
/// instead of failing silently.
/// </summary>
public interface IReleaseProviders
{
    IReadOnlyList<IReleaseProvider> Providers { get; }

    /// <summary>Registers a provider discovered in a loaded source pack. No-op on a duplicate name.</summary>
    void Add(IReleaseProvider provider);

    /// <summary>True once at least one loaded provider supports search.</summary>
    bool HasSearch { get; }

    Task<List<ReleaseResult>> SearchAsync(
        string query, string? quality = null, bool secretMode = false, CancellationToken ct = default);

    /// <summary>
    /// Targeted search for a specific series + episode, ordered best match first.
    /// </summary>
    Task<List<ReleaseResult>> FindEpisodeAsync(
        string seriesTitle, int episodeNumber, string? quality = null, CancellationToken ct = default);

    IReadOnlyList<ProviderFeed> DefaultFeeds { get; }
    string? DefaultPreferredGroups { get; }
    bool IsAdultFeed(string feedUrl);

    /// <summary>The feed URL itself, followed by any extra pages a provider knows how to fetch.</summary>
    IEnumerable<string> FeedPages(string feedUrl);

    bool IsPreferredLatestFeed(string feedUrl);

    Task<string?> CoverFromTitleAsync(string rawTitle, CancellationToken ct = default);
    Task<string?> CoverFromPageAsync(string pageHtml, CancellationToken ct = default);
    Task<SwarmStats?> SwarmAsync(string? link, string? viewUrl, CancellationToken ct = default);
}
