namespace Sentrychan.Core.Models;

/// <summary>A user-made shelf for the manga library ("Reading", "Weekly"…). A title can be on several.</summary>
public class MangaCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Position among the categories, 0 first. The user reorders them.</summary>
    public int Order { get; set; }
}

/// <summary>A manga on a category. A manga on none shows under "Default".</summary>
public class MangaCategoryLink
{
    public int MangaId { get; set; }
    public Manga? Manga { get; set; }
    public int CategoryId { get; set; }
    public MangaCategory? Category { get; set; }
}

/// <summary>A bookmarked chapter. Keyed by chapter id, which chapter syncs keep stable.</summary>
public class MangaChapterBookmark
{
    public int ChapterId { get; set; }
    public MangaChapter? Chapter { get; set; }
    public DateTime BookmarkedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// When a chapter was last opened in the reader, and where. One row per chapter; the History
/// page shows the newest per title, with a resume.
/// </summary>
public class MangaReadingHistory
{
    public int Id { get; set; }
    public int MangaId { get; set; }
    public Manga? Manga { get; set; }
    public int ChapterId { get; set; }
    public MangaChapter? Chapter { get; set; }
    public DateTime ReadAt { get; set; } = DateTime.UtcNow;
    public int LastPage { get; set; }
}
