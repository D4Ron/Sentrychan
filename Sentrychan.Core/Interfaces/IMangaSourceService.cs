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
/// A manga source (MangaDex to start). Kept behind an interface so more sources can be
/// added the same way the anime side blends SubsPlease + nyaa.
/// </summary>
public interface IMangaSourceService
{
    /// <summary>Human name shown in the UI, e.g. "MangaDex".</summary>
    string SourceName { get; }

    /// <summary>
    /// Referer header this source's image CDN requires (hotlink protection), or null.
    /// MangaPill's CDN 403s every cover/page without it.
    /// </summary>
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
}
