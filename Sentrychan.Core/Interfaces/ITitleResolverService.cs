namespace Sentrychan.Core.Interfaces;

/// <summary>Structured fields extracted from a release/file name.</summary>
public record ParsedRelease(
    string Title,
    int? Episode,
    int Season,
    string? ReleaseGroup,
    string? Resolution,
    bool IsBatch)
{
    /// <summary>Title with the season marker restored for display ("Mushoku Tensei · S2").</summary>
    public string DisplayTitle => Season > 1 ? $"{Title} S{Season}" : Title;
}

/// <summary>An anime entry resolved from the offline database.</summary>
public record ResolvedAnime(
    int MalId,
    string CanonicalTitle,
    int? Episodes,
    string? PictureUrl,
    string? ThumbnailUrl,
    int? Year,
    string? AnimeSeason,
    string? Status = null);   // FINISHED / ONGOING / UPCOMING

/// <summary>
/// Anime name recognition: parses release names into structured fields
/// (AnitomySharp) and maps titles to canonical anime entries using the
/// manami-project anime-offline-database (all synonyms, MAL ids, posters —
/// fully offline after the initial download).
/// </summary>
public interface ITitleResolverService
{
    /// <summary>True once the offline database is downloaded, parsed and indexed.</summary>
    bool IsReady { get; }

    /// <summary>Download (or refresh) and index the offline database. Safe to call repeatedly.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Tokenize a release/file name into structured fields. Works without the DB.</summary>
    ParsedRelease ParseRelease(string releaseName);

    /// <summary>
    /// Full resolution: parse the release name, then look the title up in the
    /// offline database (exact synonym match first, fuzzy fallback).
    /// Returns null when nothing matches confidently.
    /// </summary>
    ResolvedAnime? ResolveRelease(string releaseName);

    /// <summary>Look up an already-clean title. Null when nothing matches confidently.</summary>
    ResolvedAnime? ResolveTitle(string title, int season = 1);

    /// <summary>
    /// Free-text search over the offline database — every title/synonym is matched,
    /// ranked by relevance. Powers Add Series without depending on Jikan's flaky
    /// search endpoint. Returns up to <paramref name="limit"/> results.
    /// </summary>
    List<ResolvedAnime> Search(string query, int limit = 20);
}
