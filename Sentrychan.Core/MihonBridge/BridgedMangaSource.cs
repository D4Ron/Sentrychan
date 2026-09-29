using System.Globalization;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.Core.MihonBridge;

/// <summary>What a bridged source needs from the bridge: a client for a running server.</summary>
public interface IMihonBridge
{
    /// <summary>A client for the server, starting it first if it isn't running.</summary>
    Task<SuwayomiClient> ClientAsync(CancellationToken ct = default);
}

/// <summary>
/// One source from an installed Mihon extension, presented as an ordinary Sentrychan source.
/// There is one of these per bridged source and none of them knows anything about the site
/// behind it: every call goes to the helper server, which runs the extension.
///
/// <para>Ids: a title's <see cref="MangaSearchResult.SourceId"/> is the server's manga id and a
/// chapter's is the server's chapter id. Both live in the server's own database (kept in
/// <see cref="BridgeLayout.DataDir"/> across upgrades), so they stay stable.</para>
/// </summary>
public sealed class BridgedMangaSource(BridgeSource source, string sourceName, IMihonBridge bridge) : IMangaSourceService
{
    /// <summary>
    /// Cover images are stored with this prefix instead of the server's current address: the
    /// server gets a new port when its old one is taken, and the library keeps cover URLs.
    /// The image loader resolves it against the running server (<see cref="MihonBridgeService.ResolveImageUrlAsync"/>).
    /// </summary>
    public const string ImageScheme = "mihon-bridge:";

    // Chapter id → the site's URL for it, for "open in browser". Filled as chapters are listed.
    private readonly Dictionary<string, string> _chapterUrls = new();

    public BridgeSource Source { get; } = source;

    public string SourceName { get; } = sourceName;

    public bool IsAdultSource => Source.IsNsfw;

    public bool SupportsBrowse => true;

    public MangaSourceInfo Info => new(Source.Id, SourceName, Language(Source.Language), Source.IsNsfw, Source.SupportsLatest);

    /// <summary>Mihon marks multi-language sources "all"; everything else is already an ISO code.</summary>
    private static string Language(string lang) => string.IsNullOrWhiteSpace(lang) ? "all" : lang;

    // ── Listing ─────────────────────────────────────────────────────

    public async Task<MangaPage> GetPopularAsync(int page, CancellationToken ct = default) =>
        ToPage(await (await bridge.ClientAsync(ct)).FetchMangaPageAsync(Source.Id, BridgeListing.Popular, page, ct: ct));

    public async Task<MangaPage> GetLatestAsync(int page, CancellationToken ct = default) =>
        Source.SupportsLatest
            ? ToPage(await (await bridge.ClientAsync(ct)).FetchMangaPageAsync(Source.Id, BridgeListing.Latest, page, ct: ct))
            : MangaPage.Empty;

    public async Task<MangaPage> SearchAsync(string query, int page, FilterList filters, CancellationToken ct = default) =>
        ToPage(await (await bridge.ClientAsync(ct)).FetchMangaPageAsync(Source.Id, BridgeListing.Search, page, query, filters, ct));

    public async Task<FilterList> GetFilterListAsync(CancellationToken ct = default) =>
        await (await bridge.ClientAsync(ct)).GetFiltersAsync(Source.Id, ct);

    // The v1 members, for the classic screens.
    public async Task<List<MangaSearchResult>> SearchAsync(string query, int limit = 20, int page = 1, CancellationToken ct = default) =>
        [.. (await SearchAsync(query, page, FilterList.Empty, ct)).Items];

    public async Task<List<MangaSearchResult>> BrowseAsync(string category, int limit = 24, int page = 1, CancellationToken ct = default) =>
        [.. (category == "Latest" ? await GetLatestAsync(page, ct) : await GetPopularAsync(page, ct)).Items];

    // ── A title ─────────────────────────────────────────────────────

    public async Task<MangaSearchResult?> GetDetailsAsync(string sourceId, CancellationToken ct = default)
    {
        if (!int.TryParse(sourceId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return null;
        return ToResult(await (await bridge.ClientAsync(ct)).FetchMangaAsync(id, ct));
    }

    /// <summary>
    /// Every chapter the source lists, ascending. <paramref name="language"/> is ignored: a Mihon
    /// source serves one language (a multi-language extension installs one source per language).
    /// </summary>
    public async Task<List<MangaChapterInfo>> GetChaptersAsync(string sourceId, string language = "en", CancellationToken ct = default)
    {
        if (!int.TryParse(sourceId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return [];
        return ToChapterInfos(await (await bridge.ClientAsync(ct)).FetchChaptersAsync(id, ct));
    }

    /// <summary>The server's chapters as the app's, ascending — also used when importing a backup.</summary>
    public List<MangaChapterInfo> ToChapterInfos(IEnumerable<BridgeChapter> chapters)
    {
        var list = chapters.ToList();
        var lang = Language(Source.Language);
        lock (_chapterUrls)
            foreach (var c in list.Where(c => !string.IsNullOrEmpty(c.RealUrl)))
                _chapterUrls[Id(c.Id)] = c.RealUrl!;

        return list
            // Mihon lists newest first (source order 0); unnumbered chapters (-1) keep that order.
            .OrderBy(c => c.ChapterNumber >= 0 ? c.ChapterNumber : double.MaxValue)
            .ThenByDescending(c => c.SourceOrder)
            .Select(c => new MangaChapterInfo(
                Id(c.Id),
                c.ChapterNumber >= 0 ? c.ChapterNumber.ToString("0.###", CultureInfo.InvariantCulture) : c.Name,
                c.ChapterNumber >= 0 ? c.ChapterNumber : null,
                null, c.Name, lang, c.Scanlator, 0, c.UploadedAt))
            .ToList();
    }

    public async Task<List<string>> GetPageUrlsAsync(string chapterSourceId, bool dataSaver = false, CancellationToken ct = default)
    {
        if (!int.TryParse(chapterSourceId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return [];
        return [.. await (await bridge.ClientAsync(ct)).FetchPagesAsync(id, ct)];
    }

    public string GetChapterWebUrl(string chapterSourceId)
    {
        lock (_chapterUrls) return _chapterUrls.TryGetValue(chapterSourceId, out var url) ? url : string.Empty;
    }

    // ── Mapping ─────────────────────────────────────────────────────

    private MangaPage ToPage(BridgeMangaPage page) => new(page.Mangas.Select(ToResult).ToList(), page.HasNextPage);

    public MangaSearchResult ToResult(BridgeManga m) => new(
        Id(m.Id), m.Title, null,
        m.Description,
        string.IsNullOrEmpty(m.ThumbnailUrl) ? string.Empty : ImageScheme + m.ThumbnailUrl,
        Status(m.Status), null, null, [], Source.IsNsfw);

    private static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);

    private static string? Status(string? s) => s switch
    {
        "ONGOING" => "Ongoing",
        "COMPLETED" or "PUBLISHING_FINISHED" => "Completed",
        "CANCELLED" => "Cancelled",
        "ON_HIATUS" => "Hiatus",
        "LICENSED" => "Licensed",
        _ => null,
    };
}
