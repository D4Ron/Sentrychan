using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.Core.Services;

public record MangaUpdateReport(int Checked, int WithNewChapters, List<(string Title, string Message)> Updates);

/// <summary>
/// Checks followed manga for new chapters (mirrors AiringStatusRefreshService on the
/// anime side). Re-syncs each manga's chapter list from the source; when new chapters
/// appear on a manga that already had some, it re-caches them and fires a toast.
/// </summary>
public class MangaUpdateService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IMangaSourceRegistry _sources;
    private readonly IMangaService _mangaService;
    private readonly ILogger<MangaUpdateService> _logger;

    public MangaUpdateService(
        IDbContextFactory<AppDbContext> dbFactory,
        IMangaSourceRegistry sources,
        IMangaService mangaService,
        ILogger<MangaUpdateService> logger)
    {
        _dbFactory = dbFactory;
        _sources = sources;
        _mangaService = mangaService;
        _logger = logger;
    }

    public async Task<MangaUpdateReport> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        List<Models.Manga> library;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            library = await db.Manga.Include(m => m.Chapters).ToListAsync(ct);

        int checkedCount = 0, withNew = 0;
        var updates = new List<(string Title, string Message)>();

        foreach (var manga in library)
        {
            if (ct.IsCancellationRequested) break;
            checkedCount++;

            try
            {
                var infos = await _sources.Get(manga.Source).GetChaptersAsync(manga.SourceId, "en", ct);
                if (infos.Count == 0) continue;

                var knownIds = manga.Chapters.Select(c => c.SourceId).ToHashSet();
                var newOnes  = infos.Where(i => !knownIds.Contains(i.SourceId)).ToList();

                // Only alert when this isn't the first time we've seen the manga's
                // chapters — the initial sync isn't "new chapters".
                bool hadChaptersBefore = manga.Chapters.Count > 0;

                if (newOnes.Count > 0)
                {
                    await _mangaService.SyncChaptersAsync(manga.Id, infos, ct);
                    if (hadChaptersBefore)
                    {
                        withNew++;
                        var latest = newOnes.OrderByDescending(c => c.ChapterSort ?? double.MinValue).First();
                        var msg = newOnes.Count == 1
                            ? $"Chapter {latest.ChapterNumber} is available"
                            : $"{newOnes.Count} new chapters (up to {latest.ChapterNumber})";
                        updates.Add(($"New chapter — {manga.Title}", msg));
                        _logger.LogInformation("[MangaUpdate] {Title}: {N} new chapters", manga.Title, newOnes.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MangaUpdate] Check failed for {Title}", manga.Title);
            }

            await Task.Delay(1500, ct); // pace the source's API
        }

        return new MangaUpdateReport(checkedCount, withNew, updates);
    }
}
