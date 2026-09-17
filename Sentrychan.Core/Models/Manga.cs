namespace Sentrychan.Core.Models;

/// <summary>
/// A manga tracked in the library. Deliberately separate from <see cref="Series"/>:
/// manga has chapters/volumes and reading progress rather than episodes and airing
/// status, and comes from online sources (MangaDex) rather than torrents.
/// </summary>
public class Manga
{
    public int Id { get; set; }

    /// <summary>Which source this came from — "MangaDex" for now. Lets us add sources later.</summary>
    public string Source { get; set; } = "MangaDex";

    /// <summary>The source's own id (MangaDex UUID). Unique per source.</summary>
    public string SourceId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string? OriginalTitle { get; set; }
    public string AlternativeTitlesJson { get; set; } = "[]";
    public string? Description { get; set; }

    /// <summary>Cover image — a source URL, or a local cache path once downloaded.</summary>
    public string CoverPath { get; set; } = string.Empty;

    /// <summary>ongoing / completed / hiatus / cancelled (source's publication status).</summary>
    public string? Status { get; set; }
    public int? Year { get; set; }

    /// <summary>Highest chapter number the source lists, when known (chapters can be decimal).</summary>
    public double? TotalChapters { get; set; }

    /// <summary>Reading progress: the highest chapter number the user has read. 0 = none.</summary>
    public double LastReadChapter { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastCheckedAt { get; set; }

    /// <summary>Adult content — mirrors Series.IsCensored for the hidden mode.</summary>
    public bool IsCensored { get; set; }

    /// <summary>True for light/web novels — chapters are text, and the reader opens in text mode.</summary>
    public bool IsNovel { get; set; }

    public List<MangaChapter> Chapters { get; set; } = [];
}
