using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.MangaLibrary;

/// <summary>A library title with what the library screen shows and sorts by.</summary>
public sealed record LibraryEntry(
    Manga Manga,
    int ChapterCount,
    int UnreadCount,
    int DownloadedCount,
    DateTime? LastReadAt,
    DateTime? LatestChapterAt,
    IReadOnlyList<int> CategoryIds)
{
    public bool IsStarted   => Manga.LastReadChapter > 0 || UnreadCount < ChapterCount;
    public bool IsCompleted => ChapterCount > 0 && UnreadCount == 0;
}

public enum LibrarySort { Title, LastRead, LatestChapter, UnreadCount, DateAdded }

/// <summary>
/// The library's filters, each include / exclude / ignore like Mihon's: "Downloaded: include"
/// shows only titles with downloads, "exclude" only those without.
/// </summary>
public sealed record LibraryFilters(
    TriState Downloaded = TriState.Ignore,
    TriState Unread = TriState.Ignore,
    TriState Started = TriState.Ignore,
    TriState Completed = TriState.Ignore)
{
    public bool IsActive => this != new LibraryFilters();
}

public static class LibraryQuery
{
    /// <summary>Titles on no category show under Default.</summary>
    public const int DefaultCategory = 0;

    /// <param name="categoryId">A category id, <see cref="DefaultCategory"/> for uncategorised, or null for everything.</param>
    public static List<LibraryEntry> Apply(IEnumerable<LibraryEntry> entries, LibraryFilters filters,
        LibrarySort sort, bool ascending, string? search = null, int? categoryId = null)
    {
        var q = entries;
        if (categoryId is { } cat)
            q = cat == DefaultCategory ? q.Where(e => e.CategoryIds.Count == 0) : q.Where(e => e.CategoryIds.Contains(cat));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(e => e.Manga.Title.Contains(s, StringComparison.OrdinalIgnoreCase)
                          || (e.Manga.OriginalTitle?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        q = Tri(q, filters.Downloaded, e => e.DownloadedCount > 0);
        q = Tri(q, filters.Unread,     e => e.UnreadCount > 0);
        q = Tri(q, filters.Started,    e => e.IsStarted);
        q = Tri(q, filters.Completed,  e => e.IsCompleted);

        // Titles tie-break by name so equal keys don't shuffle between refreshes.
        IOrderedEnumerable<LibraryEntry> ordered = sort switch
        {
            LibrarySort.LastRead      => Order(q, e => e.LastReadAt ?? DateTime.MinValue, ascending),
            LibrarySort.LatestChapter => Order(q, e => e.LatestChapterAt ?? DateTime.MinValue, ascending),
            LibrarySort.UnreadCount   => Order(q, e => e.UnreadCount, ascending),
            LibrarySort.DateAdded     => Order(q, e => e.Manga.AddedAt, ascending),
            _ => ascending
                ? q.OrderBy(e => e.Manga.Title, StringComparer.OrdinalIgnoreCase)
                : q.OrderByDescending(e => e.Manga.Title, StringComparer.OrdinalIgnoreCase),
        };
        return ordered.ThenBy(e => e.Manga.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IOrderedEnumerable<LibraryEntry> Order<T>(IEnumerable<LibraryEntry> q, Func<LibraryEntry, T> key, bool ascending) =>
        ascending ? q.OrderBy(key) : q.OrderByDescending(key);

    private static IEnumerable<LibraryEntry> Tri(IEnumerable<LibraryEntry> q, TriState state, Func<LibraryEntry, bool> test) =>
        state switch
        {
            TriState.Include => q.Where(test),
            TriState.Exclude => q.Where(e => !test(e)),
            _ => q,
        };

    /// <summary>
    /// Whether a chapter counts as read. The chapter's own flag, or anything at or below the
    /// title's progress — marking chapter 20 read in the classic list only moved the progress.
    /// </summary>
    public static bool IsRead(Manga manga, MangaChapter chapter) =>
        chapter.IsRead || (manga.LastReadChapter > 0 && chapter.ChapterSort is { } n && n <= manga.LastReadChapter);
}

public enum ChapterSort { SourceOrder, Number, UploadDate }

/// <summary>A title page's chapter filters, include / exclude / ignore.</summary>
public sealed record ChapterFilters(
    TriState Unread = TriState.Ignore,
    TriState Downloaded = TriState.Ignore,
    TriState Bookmarked = TriState.Ignore);

public static class ChapterQuery
{
    public static List<MangaChapter> Apply(Manga manga, IEnumerable<MangaChapter> chapters, ChapterFilters filters,
        ChapterSort sort, bool descending, IReadOnlySet<int> bookmarks)
    {
        var q = chapters;
        q = Tri(q, filters.Unread,     c => !LibraryQuery.IsRead(manga, c));
        q = Tri(q, filters.Downloaded, c => !string.IsNullOrEmpty(c.DownloadedPath));
        q = Tri(q, filters.Bookmarked, c => bookmarks.Contains(c.Id));

        // Ascending is the source's own listing for "source order" (sources list oldest first,
        // see IMangaSourceService.GetChaptersAsync); the title page shows everything descending.
        IOrderedEnumerable<MangaChapter> ordered = sort switch
        {
            ChapterSort.Number     => q.OrderBy(c => c.ChapterSort ?? double.MaxValue),
            ChapterSort.UploadDate => q.OrderBy(c => c.PublishedAt ?? DateTime.MinValue),
            _                      => q.OrderBy(c => c.SourceOrder),
        };
        var list = ordered.ThenBy(c => c.Id).ToList();
        if (descending) list.Reverse();
        return list;
    }

    /// <summary>
    /// What "Download next N" queues: unread, not yet downloaded, in reading order from the
    /// earliest. Null <paramref name="count"/> means every unread chapter.
    /// </summary>
    public static List<MangaChapter> NextToDownload(Manga manga, IEnumerable<MangaChapter> chapters, int? count)
    {
        var next = chapters
            .Where(c => !LibraryQuery.IsRead(manga, c) && string.IsNullOrEmpty(c.DownloadedPath))
            .OrderBy(c => c.ChapterSort ?? double.MaxValue).ThenBy(c => c.Id);
        return (count is { } n ? next.Take(n) : next).ToList();
    }

    private static IEnumerable<MangaChapter> Tri(IEnumerable<MangaChapter> q, TriState state, Func<MangaChapter, bool> test) =>
        state switch
        {
            TriState.Include => q.Where(test),
            TriState.Exclude => q.Where(c => !test(c)),
            _ => q,
        };
}

/// <summary>A new chapter on a title in the library, for the Updates page.</summary>
public sealed record ChapterUpdate(Manga Manga, MangaChapter Chapter, DateTime FetchedAt);

/// <summary>The newest reading of a title, for the History page.</summary>
public sealed record HistoryEntry(Manga Manga, MangaChapter Chapter, DateTime ReadAt, int LastPage);

public static class UpdatesQuery
{
    /// <summary>Newest first, grouped by the local calendar day they arrived.</summary>
    public static List<(DateOnly Day, List<ChapterUpdate> Items)> ByDay(IEnumerable<ChapterUpdate> updates) =>
        updates.OrderByDescending(u => u.FetchedAt)
               .GroupBy(u => DateOnly.FromDateTime(u.FetchedAt.ToLocalTime()))
               .Select(g => (g.Key, g.ToList()))
               .ToList();

    /// <summary>"Today", "Yesterday", or the date.</summary>
    public static string DayLabel(DateOnly day, DateOnly today) =>
        day == today ? "Today" : day == today.AddDays(-1) ? "Yesterday" : day.ToString("dddd, d MMMM yyyy");
}
