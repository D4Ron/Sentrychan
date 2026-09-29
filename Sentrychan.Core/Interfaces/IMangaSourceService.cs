namespace Sentrychan.Core.Interfaces;

/// <summary>One manga as returned by a source search — enough to render a result card.</summary>
public record MangaSearchResult(
    string SourceId,
    string Title,
    string? OriginalTitle,
    string? Description,
    string CoverUrl,
    string? Status,
    int? Year,
    double? LastChapter,
    IReadOnlyList<string> AltTitles,
    bool IsAdult);

/// <summary>One chapter from a source's chapter feed.</summary>
public record MangaChapterInfo(
    string SourceId,
    string ChapterNumber,
    double? ChapterSort,
    string? Volume,
    string? Title,
    string Language,
    string? ScanlationGroup,
    int Pages,
    DateTime? PublishedAt);

/// <summary>
/// What the app needs to know about a source to list it: a stable id, its language, whether
/// it's adult, and whether it can list the latest updates.
/// </summary>
/// <param name="Id">
/// Never changes once shipped — library entries and reading history point at it. A bridged
/// Mihon source uses its numeric source id as a string.
/// </param>
/// <param name="Name">Shown to the user; may change between versions.</param>
/// <param name="Language">ISO 639-1 code ("en", "ja"), or "all" for a multi-language source.</param>
/// <param name="IsNsfw">Adult content; only listed while secret mode is on.</param>
/// <param name="SupportsLatest">Can list recently updated titles (<see cref="IMangaSourceService.GetLatestAsync"/>).</param>
public sealed record MangaSourceInfo(string Id, string Name, string Language, bool IsNsfw, bool SupportsLatest);

/// <summary>One page of results, and whether asking for the next page is worth it.</summary>
public sealed record MangaPage(IReadOnlyList<MangaSearchResult> Items, bool HasNextPage)
{
    public static MangaPage Empty { get; } = new([], false);
}

/// <summary>
/// A manga source. Kept behind an interface so sources can live in loadable source packs,
/// the same way the anime side's release providers do (see IReleaseProvider).
///
/// <para><b>Compatibility.</b> Packs are built outside this repository against this interface.
/// Every member added after the first version has a default implementation, so a pack built
/// against an older version keeps loading; a pack only overrides what it can do better.</para>
///
/// <para><b>Contract v2</b> (browse pages and filters) — implement these to take part in the
/// Browse screens and global search:</para>
/// <list type="bullet">
/// <item><see cref="Info"/> — stable id, language, adult flag, whether Latest works.</item>
/// <item><see cref="GetPopularAsync"/> and <see cref="GetLatestAsync"/> — paged listings.</item>
/// <item><see cref="GetFilterList"/> and <see cref="SearchAsync(string, int, FilterList, CancellationToken)"/>
/// — filtered, paged search (<see cref="GetFilterListAsync"/> when the filters have to be fetched).</item>
/// </list>
/// The defaults fall back to the version 1 members (<see cref="BrowseAsync"/> and the
/// query-only <see cref="SearchAsync(string, int, int, CancellationToken)"/>), so a v1 source
/// still shows up, with no filters.
/// </summary>
public interface IMangaSourceService
{
    /// <summary>Human name shown in the UI. Also what a tracked Manga stores as its Source.</summary>
    string SourceName { get; }

    /// <summary>Referer header this source's image CDN requires (hotlink protection), or null.</summary>
    string? ImageReferer => null;

    /// <summary>Adult source — only exposed while secret mode is active.</summary>
    bool IsAdultSource => false;

    /// <summary>
    /// True for prose sources (light/web novels) — chapters are text, not image pages.
    /// The reader renders <see cref="GetChapterTextAsync"/> instead of page images, and
    /// added titles are flagged so they open in text mode.
    /// </summary>
    bool IsNovel => false;

    Task<List<MangaSearchResult>> SearchAsync(string query, int limit = 20, int page = 1, CancellationToken ct = default);

    /// <summary>Whether this source supports query-less browsing (Popular / Latest).</summary>
    bool SupportsBrowse => false;

    /// <summary>
    /// Browse without a search query. <paramref name="category"/> is "Popular" or
    /// "Latest". <paramref name="page"/> is 1-based. Sources that don't support it
    /// return an empty list.
    /// </summary>
    Task<List<MangaSearchResult>> BrowseAsync(string category, int limit = 24, int page = 1, CancellationToken ct = default)
        => Task.FromResult(new List<MangaSearchResult>());

    Task<MangaSearchResult?> GetDetailsAsync(string sourceId, CancellationToken ct = default);

    /// <summary>All chapters for a manga in the given language, ascending by chapter number.</summary>
    Task<List<MangaChapterInfo>> GetChaptersAsync(string sourceId, string language = "en", CancellationToken ct = default);

    /// <summary>
    /// Ordered page image URLs for a chapter. Empty when the chapter is externally
    /// hosted / licensed (not readable in-app). <paramref name="dataSaver"/> requests
    /// the compressed variant.
    /// </summary>
    Task<List<string>> GetPageUrlsAsync(string chapterSourceId, bool dataSaver = false, CancellationToken ct = default);

    /// <summary>
    /// A novel chapter's readable text (paragraphs separated by blank lines), or null
    /// when unavailable. Only meaningful for <see cref="IsNovel"/> sources; comic sources
    /// return null and use <see cref="GetPageUrlsAsync"/> instead.
    /// </summary>
    Task<string?> GetChapterTextAsync(string chapterSourceId, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    /// <summary>A canonical web URL for a chapter, for the "open externally" fallback.</summary>
    string GetChapterWebUrl(string chapterSourceId);

    // ── Contract v2 ──────────────────────────────────────────────────

    /// <summary>
    /// Identity and capabilities. The default derives it from the v1 members: the id is
    /// <see cref="SourceName"/>, the language "all", Latest follows <see cref="SupportsBrowse"/>.
    /// Override it — at least to give a real language.
    /// </summary>
    MangaSourceInfo Info => new(SourceName, SourceName, "all", IsAdultSource, SupportsBrowse);

    /// <summary>Titles the source ranks as popular. <paramref name="page"/> is 1-based.</summary>
    Task<MangaPage> GetPopularAsync(int page, CancellationToken ct = default) =>
        SupportsBrowse ? PageOf(BrowseAsync("Popular", DefaultPageSize, page, ct)) : Task.FromResult(MangaPage.Empty);

    /// <summary>Recently updated titles, newest first. Only called when <see cref="MangaSourceInfo.SupportsLatest"/>.</summary>
    Task<MangaPage> GetLatestAsync(int page, CancellationToken ct = default) =>
        SupportsBrowse ? PageOf(BrowseAsync("Latest", DefaultPageSize, page, ct)) : Task.FromResult(MangaPage.Empty);

    /// <summary>
    /// The filters this source understands, in display order, in their initial state. The app
    /// clones the list before the user edits it. Empty when the source has none (the default).
    /// </summary>
    FilterList GetFilterList() => FilterList.Empty;

    /// <summary>
    /// <see cref="GetFilterList"/> for a source that has to ask something else for its filters
    /// (a server, a site's genre list). The app calls this one; the default returns
    /// <see cref="GetFilterList"/>, so a source with a fixed list only implements that.
    /// </summary>
    Task<FilterList> GetFilterListAsync(CancellationToken ct = default) => Task.FromResult(GetFilterList());

    /// <summary>
    /// Search with filters. <paramref name="query"/> may be empty (browse by filters alone);
    /// <paramref name="filters"/> is a clone of <see cref="GetFilterList"/> with the user's
    /// choices. The default ignores filters and runs the v1 query search.
    /// </summary>
    Task<MangaPage> SearchAsync(string query, int page, FilterList filters, CancellationToken ct = default) =>
        PageOf(SearchAsync(query, DefaultPageSize, page, ct));

    /// <summary>Page size the v2 defaults ask the v1 members for; a full page implies there may be another.</summary>
    const int DefaultPageSize = 24;

    private static async Task<MangaPage> PageOf(Task<List<MangaSearchResult>> results)
    {
        var items = await results;
        return new MangaPage(items, items.Count >= DefaultPageSize);
    }
}
