using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.Services;

/// <summary>DB-backed manga library + reading progress. Mirrors SeriesService.</summary>
public class MangaService : IMangaService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<MangaService> _logger;

    public MangaService(IDbContextFactory<AppDbContext> dbFactory, ILogger<MangaService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<List<Manga>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Manga.AsNoTracking().OrderByDescending(m => m.AddedAt).ToListAsync(ct);
    }

    public async Task<Manga?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Manga.AsNoTracking()
            .Include(m => m.Chapters)
            .FirstOrDefaultAsync(m => m.Id == id, ct);
    }

    public async Task<Manga?> GetBySourceIdAsync(string source, string sourceId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Manga.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Source == source && m.SourceId == sourceId, ct);
    }

    public async Task<Manga> AddAsync(Manga manga, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var existing = await db.Manga
            .FirstOrDefaultAsync(m => m.Source == manga.Source && m.SourceId == manga.SourceId, ct);
        if (existing != null)
        {
            _logger.LogInformation("[Manga] {Title} already in library", manga.Title);
            return existing;
        }

        db.Manga.Add(manga);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("[Manga] Added {Title} ({Source})", manga.Title, manga.Source);
        return manga;
    }

    public async Task<bool> RemoveAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var manga = await db.Manga.FindAsync([id], ct);
        if (manga == null) return false;
        db.Manga.Remove(manga); // chapters cascade
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task UpdateProgressAsync(int mangaId, double lastReadChapter, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var manga = await db.Manga.FindAsync([mangaId], ct);
        if (manga == null) return;

        // Progress only ever rises — re-reading an earlier chapter shouldn't erase it.
        if (lastReadChapter > manga.LastReadChapter)
        {
            manga.LastReadChapter = lastReadChapter;
            manga.LastCheckedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task MarkAllReadAsync(int mangaId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var manga = await db.Manga.Include(m => m.Chapters).FirstOrDefaultAsync(m => m.Id == mangaId, ct);
        if (manga == null) return;

        foreach (var c in manga.Chapters) c.IsRead = true;

        var top = manga.Chapters.Select(c => c.ChapterSort).Where(n => n.HasValue).DefaultIfEmpty(null).Max();
        var target = top ?? manga.TotalChapters ?? manga.LastReadChapter;
        if (target > manga.LastReadChapter) manga.LastReadChapter = target;
        if (manga.TotalChapters is null or 0 && top.HasValue) manga.TotalChapters = top;

        manga.LastCheckedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveReadingPositionAsync(int chapterId, int lastPage, bool markRead, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var chapter = await db.MangaChapters.FindAsync([chapterId], ct);
        if (chapter == null) return;

        if (lastPage > chapter.LastReadPage) chapter.LastReadPage = lastPage;
        if (markRead && !chapter.IsRead) chapter.IsRead = true;

        // Reading history: one row per chapter, moved to "now" each time it's read.
        var history = await db.MangaReadingHistory.FirstOrDefaultAsync(h => h.ChapterId == chapterId, ct);
        if (history == null)
            db.MangaReadingHistory.Add(new MangaReadingHistory { MangaId = chapter.MangaId, ChapterId = chapterId, LastPage = lastPage });
        else
        {
            history.ReadAt = DateTime.UtcNow;
            history.LastPage = lastPage;
        }

        await db.SaveChangesAsync(ct);

        // Finishing a chapter advances the manga's overall progress.
        if (markRead && chapter.ChapterSort.HasValue)
            await UpdateProgressAsync(chapter.MangaId, chapter.ChapterSort.Value, ct);
    }

    public async Task<List<MangaChapter>> SyncChaptersAsync(
        int mangaId, IEnumerable<MangaChapterInfo> chapters, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var manga = await db.Manga.Include(m => m.Chapters).FirstOrDefaultAsync(m => m.Id == mangaId, ct);
        if (manga == null) return [];

        // Update rows in place, matched on the source's chapter id. This used to clear the
        // list and insert fresh rows, so every refresh (and opening a title refreshes) gave
        // every chapter a new database id — dropping each chapter's saved page, and orphaning
        // anything keyed on the id, like a vault download's resume.
        var prior = manga.Chapters.GroupBy(c => c.SourceId).ToDictionary(g => g.Key, g => g.First());
        var listed = new HashSet<string>();
        // Chapters that turn up on a title already synced are news (the Updates page);
        // the first sync of a newly added title is not.
        var isFirstSync = prior.Count == 0;
        var now = DateTime.UtcNow;
        var order = 0;

        foreach (var info in chapters)
        {
            if (!listed.Add(info.SourceId)) continue; // a source listing the same chapter twice
            if (!prior.TryGetValue(info.SourceId, out var row))
            {
                row = new MangaChapter { SourceId = info.SourceId, FetchedAt = isFirstSync ? null : now };
                manga.Chapters.Add(row);
            }
            row.SourceOrder     = order++;
            row.ChapterNumber   = info.ChapterNumber;
            row.ChapterSort     = info.ChapterSort;
            row.Volume          = info.Volume;
            row.Title           = info.Title;
            row.Language        = info.Language;
            row.ScanlationGroup = info.ScanlationGroup;
            row.Pages           = info.Pages;
            row.PublishedAt     = info.PublishedAt;
        }

        // Chapters the source no longer lists go — unless downloaded, which stay readable.
        foreach (var gone in manga.Chapters.Where(c => !listed.Contains(c.SourceId) && string.IsNullOrEmpty(c.DownloadedPath)).ToList())
            manga.Chapters.Remove(gone);

        // Track the highest chapter the source lists, for the library "X / Y" readout.
        var maxCh = chapters.Select(c => c.ChapterSort).Where(n => n.HasValue).DefaultIfEmpty(null).Max();
        if (maxCh.HasValue) manga.TotalChapters = maxCh;
        manga.LastCheckedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return manga.Chapters.OrderBy(c => c.ChapterSort ?? double.MaxValue).ToList();
    }
}
