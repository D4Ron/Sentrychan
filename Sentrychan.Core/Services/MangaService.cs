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

        // Preserve per-chapter state (read flag, downloaded path) across a re-sync by
        // keying on the source chapter id.
        var prior = manga.Chapters.ToDictionary(c => c.SourceId, c => c);
        manga.Chapters.Clear();

        foreach (var info in chapters)
        {
            prior.TryGetValue(info.SourceId, out var old);
            manga.Chapters.Add(new MangaChapter
            {
                SourceId        = info.SourceId,
                ChapterNumber   = info.ChapterNumber,
                ChapterSort     = info.ChapterSort,
                Volume          = info.Volume,
                Title           = info.Title,
                Language        = info.Language,
                ScanlationGroup = info.ScanlationGroup,
                Pages           = info.Pages,
                PublishedAt     = info.PublishedAt,
                IsRead          = old?.IsRead ?? false,
                DownloadedPath  = old?.DownloadedPath
            });
        }

        // Track the highest chapter the source lists, for the library "X / Y" readout.
        var maxCh = chapters.Select(c => c.ChapterSort).Where(n => n.HasValue).DefaultIfEmpty(null).Max();
        if (maxCh.HasValue) manga.TotalChapters = maxCh;
        manga.LastCheckedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return manga.Chapters.OrderBy(c => c.ChapterSort ?? double.MaxValue).ToList();
    }
}
