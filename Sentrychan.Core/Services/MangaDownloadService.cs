using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.Services;

/// <summary>
/// Downloads a chapter's page images to disk for offline reading. Files land in
/// {root}/Manga/{title}/{Chapter n}/001.jpg… — a plain folder of images, which the
/// reader can then read locally with no network.
/// </summary>
public class MangaDownloadService : IMangaDownloadService
{
    private readonly IMangaSourceRegistry _sources;
    private readonly IMangaService _mangaService;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<MangaDownloadService> _logger;
    private readonly HttpClient _http;

    public MangaDownloadService(
        IMangaSourceRegistry sources,
        IMangaService mangaService,
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<MangaDownloadService> logger)
    {
        _sources = sources;
        _mangaService = mangaService;
        _dbFactory = dbFactory;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 Sentrychan/1.0");
    }

    public async Task<string?> DownloadChapterAsync(
        Manga manga, MangaChapter chapter, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var source = _sources.Get(manga.Source);
        var urls = await source.GetPageUrlsAsync(chapter.SourceId, dataSaver: false, ct);
        if (urls.Count == 0)
        {
            _logger.LogInformation("[MangaDL] {Title} ch {Ch} has no in-app pages (external)", manga.Title, chapter.ChapterNumber);
            return null;
        }

        // Local-source pages are already files on disk — nothing to download.
        if (!urls[0].StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return null;

        var root      = await ResolveRootAsync(ct);
        var chapterLbl = string.IsNullOrEmpty(chapter.ChapterNumber) ? "Oneshot" : $"Chapter {chapter.ChapterNumber}";
        var dir       = Path.Combine(root, "Manga", Sanitize(manga.Title), Sanitize(chapterLbl));
        Directory.CreateDirectory(dir);

        try
        {
            for (int i = 0; i < urls.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var ext  = Path.GetExtension(new Uri(urls[i]).AbsolutePath);
                if (string.IsNullOrEmpty(ext)) ext = ".jpg";
                var file = Path.Combine(dir, $"{(i + 1):D3}{ext}");

                if (!File.Exists(file)) // resume a partial download
                {
                    byte[] bytes;
                    if (!string.IsNullOrEmpty(source.ImageReferer))
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, urls[i]);
                        req.Headers.Referrer = new Uri(source.ImageReferer!);
                        using var resp = await _http.SendAsync(req, ct);
                        bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                    }
                    else bytes = await _http.GetByteArrayAsync(urls[i], ct);

                    await File.WriteAllBytesAsync(file, bytes, ct);
                }
                progress?.Report((i + 1) / (double)urls.Count);
            }

            await SetDownloadedPathAsync(chapter.Id, dir, ct);
            _logger.LogInformation("[MangaDL] {Title} {Chapter} → {Dir} ({Pages} pages)",
                manga.Title, chapterLbl, dir, urls.Count);
            return dir;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[MangaDL] Cancelled {Title} {Chapter}", manga.Title, chapterLbl);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MangaDL] Failed {Title} {Chapter}", manga.Title, chapterLbl);
            return null;
        }
    }

    public async Task DeleteChapterDownloadAsync(MangaChapter chapter, CancellationToken ct = default)
    {
        try
        {
            if (!string.IsNullOrEmpty(chapter.DownloadedPath) && Directory.Exists(chapter.DownloadedPath))
                Directory.Delete(chapter.DownloadedPath, recursive: true);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[MangaDL] Delete failed"); }

        await SetDownloadedPathAsync(chapter.Id, null, ct);
    }

    private async Task SetDownloadedPathAsync(int chapterId, string? path, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var c = await db.MangaChapters.FindAsync([chapterId], ct);
        if (c == null) return;
        c.DownloadedPath = path;
        await db.SaveChangesAsync(ct);
    }

    private async Task<string> ResolveRootAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var lib = (await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == "LibraryPath", ct))?.Value;
        if (!string.IsNullOrWhiteSpace(lib) && Directory.Exists(lib)) return lib!;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sentrychan");
    }

    private static string Sanitize(string name)
    {
        var cleaned = Regex.Replace(name, "[" + Regex.Escape(new string(Path.GetInvalidFileNameChars())) + "]", "_");
        return cleaned.Trim().TrimEnd('.'); // Windows dislikes trailing dots/spaces
    }
}
