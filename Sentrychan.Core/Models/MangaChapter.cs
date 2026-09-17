namespace Sentrychan.Core.Models;

/// <summary>
/// One chapter of a tracked <see cref="Manga"/>, cached from the source so the chapter
/// list and reading progress work without a round-trip. Stage 2/3 hang the reader and
/// offline downloads off this (page count, downloaded path, read flag).
/// </summary>
public class MangaChapter
{
    public int Id { get; set; }

    public int MangaId { get; set; }
    public Manga? Manga { get; set; }

    /// <summary>The source's chapter id (MangaDex chapter UUID).</summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>Chapter number as the source gives it — usually "10", "10.5", sometimes empty (oneshots).</summary>
    public string ChapterNumber { get; set; } = string.Empty;

    /// <summary>Parsed numeric form of <see cref="ChapterNumber"/> for ordering/progress; null if non-numeric.</summary>
    public double? ChapterSort { get; set; }

    public string? Volume { get; set; }
    public string? Title { get; set; }
    public string Language { get; set; } = "en";
    public string? ScanlationGroup { get; set; }
    public int Pages { get; set; }

    public DateTime? PublishedAt { get; set; }

    public bool IsRead { get; set; }

    /// <summary>Last page the reader was on (0-based), for resume. 0 = start.</summary>
    public int LastReadPage { get; set; }

    /// <summary>Local folder/CBZ once downloaded for offline reading (Stage 3). Null = online only.</summary>
    public string? DownloadedPath { get; set; }
}
