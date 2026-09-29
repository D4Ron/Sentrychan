using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.MangaLibrary;

/// <summary>
/// The manga library beyond the basics in <see cref="IMangaService"/>: categories, the
/// library/updates/history listings, bookmarks, bulk read state and migrating a title to
/// another source. Shared by the classic and the Mihon-style screens.
/// </summary>
public sealed class MangaLibraryService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<MangaLibraryService> _logger;

    public MangaLibraryService(IDbContextFactory<AppDbContext> dbFactory, ILogger<MangaLibraryService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    // ── Categories ────────────────────────────────────────────────────

    public async Task<List<MangaCategory>> GetCategoriesAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.MangaCategories.AsNoTracking().OrderBy(c => c.Order).ThenBy(c => c.Id).ToListAsync(ct);
    }

    public async Task<MangaCategory> CreateCategoryAsync(string name, CancellationToken ct = default)
    {
        name = ValidName(name);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (await db.MangaCategories.AnyAsync(c => c.Name == name, ct))
            throw new InvalidOperationException($"There's already a category called \"{name}\".");
        var order = await db.MangaCategories.Select(c => (int?)c.Order).MaxAsync(ct) ?? -1;
        var category = new MangaCategory { Name = name, Order = order + 1 };
        db.MangaCategories.Add(category);
        await db.SaveChangesAsync(ct);
        return category;
    }

    public async Task RenameCategoryAsync(int id, string name, CancellationToken ct = default)
    {
        name = ValidName(name);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (await db.MangaCategories.AnyAsync(c => c.Name == name && c.Id != id, ct))
            throw new InvalidOperationException($"There's already a category called \"{name}\".");
        await db.MangaCategories.Where(c => c.Id == id).ExecuteUpdateAsync(u => u.SetProperty(c => c.Name, name), ct);
    }

    /// <summary>Deletes a category. Its titles aren't touched; those on no other category show under Default.</summary>
    public async Task DeleteCategoryAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.MangaCategories.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
    }

    /// <summary>Puts the categories in the given order; ids not listed keep their relative order after.</summary>
    public async Task ReorderCategoriesAsync(IReadOnlyList<int> orderedIds, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var all = await db.MangaCategories.OrderBy(c => c.Order).ThenBy(c => c.Id).ToListAsync(ct);
        var rank = orderedIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var sorted = all.OrderBy(c => rank.TryGetValue(c.Id, out var r) ? r : int.MaxValue).ThenBy(c => c.Order).ToList();
        for (var i = 0; i < sorted.Count; i++) sorted[i].Order = i;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Sets exactly which categories each of the titles is on.</summary>
    public async Task SetCategoriesAsync(IEnumerable<int> mangaIds, IEnumerable<int> categoryIds, CancellationToken ct = default)
    {
        var ids = mangaIds.Distinct().ToList();
        var cats = categoryIds.Distinct().ToList();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.MangaCategoryLinks.Where(l => ids.Contains(l.MangaId)).ExecuteDeleteAsync(ct);
        foreach (var m in ids)
            foreach (var c in cats)
                db.MangaCategoryLinks.Add(new MangaCategoryLink { MangaId = m, CategoryId = c });
        await db.SaveChangesAsync(ct);
    }

    private static string ValidName(string name)
    {
        name = name?.Trim() ?? string.Empty;
        if (name.Length == 0) throw new InvalidOperationException("A category needs a name.");
        return name;
    }

    // ── Listings ──────────────────────────────────────────────────────

    /// <summary>Every title of one kind (comics or novels) with counts for the library screen.</summary>
    public async Task<List<LibraryEntry>> GetEntriesAsync(bool novels, bool includeAdult, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var manga = await db.Manga.AsNoTracking().Include(m => m.Chapters)
            .Where(m => m.IsNovel == novels && (includeAdult || !m.IsCensored))
            .ToListAsync(ct);
        var ids = manga.Select(m => m.Id).ToList();

        var links = (await db.MangaCategoryLinks.AsNoTracking().Where(l => ids.Contains(l.MangaId)).ToListAsync(ct))
            .GroupBy(l => l.MangaId).ToDictionary(g => g.Key, g => (IReadOnlyList<int>)g.Select(l => l.CategoryId).ToList());
        var lastRead = await db.MangaReadingHistory.AsNoTracking().Where(h => ids.Contains(h.MangaId))
            .GroupBy(h => h.MangaId).Select(g => new { g.Key, At = g.Max(h => h.ReadAt) })
            .ToDictionaryAsync(x => x.Key, x => x.At, ct);

        return manga.Select(m =>
        {
            var latest = m.Chapters.Select(c => c.FetchedAt ?? c.PublishedAt).Where(d => d != null).DefaultIfEmpty(null).Max();
            return new LibraryEntry(m,
                ChapterCount: m.Chapters.Count,
                UnreadCount: m.Chapters.Count(c => !LibraryQuery.IsRead(m, c)),
                DownloadedCount: m.Chapters.Count(c => !string.IsNullOrEmpty(c.DownloadedPath)),
                LastReadAt: lastRead.TryGetValue(m.Id, out var at) ? at : null,
                LatestChapterAt: latest,
                CategoryIds: links.TryGetValue(m.Id, out var cats) ? cats : []);
        }).ToList();
    }

    /// <summary>New chapters across the library, newest first.</summary>
    public async Task<List<ChapterUpdate>> GetUpdatesAsync(bool novels, bool includeAdult, int limit = 300, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.MangaChapters.AsNoTracking()
            .Where(c => c.FetchedAt != null && c.Manga!.IsNovel == novels && (includeAdult || !c.Manga.IsCensored))
            .OrderByDescending(c => c.FetchedAt)
            .Take(limit)
            .Include(c => c.Manga)
            .ToListAsync(ct);
        return rows.Select(c => new ChapterUpdate(c.Manga!, c, c.FetchedAt!.Value)).ToList();
    }

    /// <summary>The most recent reading of each title, newest first.</summary>
    public async Task<List<HistoryEntry>> GetHistoryAsync(bool novels, bool includeAdult, int limit = 200, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.MangaReadingHistory.AsNoTracking()
            .Where(h => h.Manga!.IsNovel == novels && (includeAdult || !h.Manga.IsCensored))
            .Include(h => h.Manga).Include(h => h.Chapter)
            .OrderByDescending(h => h.ReadAt)
            .ToListAsync(ct);
        return rows.GroupBy(h => h.MangaId).Select(g => g.First())
                   .Take(limit)
                   .Select(h => new HistoryEntry(h.Manga!, h.Chapter!, h.ReadAt, h.LastPage))
                   .ToList();
    }

    /// <summary>Forgets a title's reading history (not its read chapters).</summary>
    public async Task RemoveHistoryAsync(int mangaId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.MangaReadingHistory.Where(h => h.MangaId == mangaId).ExecuteDeleteAsync(ct);
    }

    public async Task ClearHistoryAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.MangaReadingHistory.ExecuteDeleteAsync(ct);
    }

    // ── Chapters ──────────────────────────────────────────────────────

    public async Task<HashSet<int>> GetBookmarksAsync(int mangaId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return (await db.MangaChapterBookmarks.AsNoTracking()
            .Where(b => b.Chapter!.MangaId == mangaId).Select(b => b.ChapterId).ToListAsync(ct)).ToHashSet();
    }

    public async Task SetBookmarkedAsync(IEnumerable<int> chapterIds, bool bookmarked, CancellationToken ct = default)
    {
        var ids = chapterIds.Distinct().ToList();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.MangaChapterBookmarks.Where(b => ids.Contains(b.ChapterId)).ExecuteDeleteAsync(ct);
        if (!bookmarked) return;
        foreach (var id in ids) db.MangaChapterBookmarks.Add(new MangaChapterBookmark { ChapterId = id });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Marks chapters read or unread, and keeps the title's progress (the highest chapter read)
    /// consistent: marking unread can lower it, which the one-way progress update never does.
    /// </summary>
    public async Task SetReadAsync(int mangaId, IEnumerable<int> chapterIds, bool read, CancellationToken ct = default)
    {
        var ids = chapterIds.ToHashSet();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var manga = await db.Manga.Include(m => m.Chapters).FirstOrDefaultAsync(m => m.Id == mangaId, ct);
        if (manga == null) return;

        // Unread below the current progress: first make the implicit reads explicit, so
        // lowering the progress doesn't also unread chapters nobody touched.
        if (!read)
            foreach (var c in manga.Chapters.Where(c => LibraryQuery.IsRead(manga, c)))
                c.IsRead = true;

        foreach (var c in manga.Chapters.Where(c => ids.Contains(c.Id)))
        {
            c.IsRead = read;
            if (!read) c.LastReadPage = 0;
        }

        manga.LastReadChapter = manga.Chapters.Where(c => c.IsRead && c.ChapterSort.HasValue)
            .Select(c => c.ChapterSort!.Value).DefaultIfEmpty(0).Max();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Marks every chapter numbered below this one read — "Mark previous as read".</summary>
    public async Task MarkPreviousReadAsync(int chapterId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var chapter = await db.MangaChapters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == chapterId, ct);
        if (chapter?.ChapterSort is not { } n) return;
        var previous = await db.MangaChapters.Where(c => c.MangaId == chapter.MangaId && c.ChapterSort < n)
            .Select(c => c.Id).ToListAsync(ct);
        await db.DisposeAsync();
        await SetReadAsync(chapter.MangaId, previous, true, ct);
    }

    // ── Migration ─────────────────────────────────────────────────────

    /// <summary>
    /// Moves a library title to another source: the title, categories, bookmarks, reading
    /// history and read state carry over, matched chapter for chapter by number. Downloads of
    /// the old source's chapters stay readable where a new chapter has the same number.
    /// Throws when the target is already in the library as a different title.
    /// </summary>
    public async Task<Manga> MigrateAsync(int mangaId, IMangaSourceService target, MangaSearchResult details,
        IReadOnlyList<MangaChapterInfo> chapters, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var manga = await db.Manga.Include(m => m.Chapters).FirstOrDefaultAsync(m => m.Id == mangaId, ct)
            ?? throw new InvalidOperationException("That title is no longer in the library.");
        if (await db.Manga.AnyAsync(m => m.Id != mangaId && m.Source == target.SourceName && m.SourceId == details.SourceId, ct))
            throw new InvalidOperationException($"\"{details.Title}\" from {target.SourceName} is already in your library.");

        var bookmarked = (await db.MangaChapterBookmarks.Where(b => b.Chapter!.MangaId == mangaId)
            .Select(b => b.ChapterId).ToListAsync(ct)).ToHashSet();
        var history = await db.MangaReadingHistory.Where(h => h.MangaId == mangaId).ToListAsync(ct);

        // Old chapters by number. Non-numeric ones (oneshots, extras) can't be matched.
        var old = manga.Chapters.Where(c => c.ChapterSort.HasValue)
            .GroupBy(c => c.ChapterSort!.Value).ToDictionary(g => g.Key, g => g.First());

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var replaced = manga.Chapters.ToList();
        var order = 0;
        var fresh = new List<(MangaChapter Row, MangaChapter? From)>();
        foreach (var info in chapters.DistinctBy(c => c.SourceId))
        {
            var from = info.ChapterSort is { } n && old.TryGetValue(n, out var o) ? o : null;
            var row = new MangaChapter
            {
                MangaId = mangaId, SourceId = info.SourceId, ChapterNumber = info.ChapterNumber,
                ChapterSort = info.ChapterSort, Volume = info.Volume, Title = info.Title, Language = info.Language,
                ScanlationGroup = info.ScanlationGroup, Pages = info.Pages, PublishedAt = info.PublishedAt,
                SourceOrder = order++,
                IsRead = from != null && LibraryQuery.IsRead(manga, from),
                LastReadPage = from?.LastReadPage ?? 0,
                DownloadedPath = from?.DownloadedPath,
            };
            fresh.Add((row, from));
        }

        // New rows first, so bookmarks and history can be pointed at their ids.
        db.MangaChapters.RemoveRange(replaced);
        await db.SaveChangesAsync(ct);
        db.MangaChapters.AddRange(fresh.Select(f => f.Row));

        manga.Source = target.SourceName;
        manga.SourceId = details.SourceId;
        manga.Title = details.Title;
        manga.OriginalTitle = details.OriginalTitle ?? manga.OriginalTitle;
        manga.Description = details.Description ?? manga.Description;
        if (!string.IsNullOrEmpty(details.CoverUrl)) manga.CoverPath = details.CoverUrl;
        manga.Status = details.Status ?? manga.Status;
        manga.Year = details.Year ?? manga.Year;
        manga.AlternativeTitlesJson = JsonSerializer.Serialize(details.AltTitles);
        manga.TotalChapters = chapters.Select(c => c.ChapterSort).Where(n => n.HasValue).DefaultIfEmpty(null).Max() ?? manga.TotalChapters;
        manga.IsNovel = target.IsNovel;
        manga.LastCheckedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var newIdByOld = fresh.Where(f => f.From != null).ToDictionary(f => f.From!.Id, f => f.Row.Id);
        foreach (var oldId in bookmarked)
            if (newIdByOld.TryGetValue(oldId, out var nid))
                db.MangaChapterBookmarks.Add(new MangaChapterBookmark { ChapterId = nid });
        foreach (var h in history)
            if (newIdByOld.TryGetValue(h.ChapterId, out var nid))
                db.MangaReadingHistory.Add(new MangaReadingHistory { MangaId = mangaId, ChapterId = nid, ReadAt = h.ReadAt, LastPage = h.LastPage });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation("[Manga] Migrated {Title} to {Source}: {Matched} of {Old} chapters matched",
            manga.IsCensored ? Vault.Privacy.Placeholder : manga.Title, target.SourceName, newIdByOld.Count, replaced.Count);
        return manga;
    }
}
