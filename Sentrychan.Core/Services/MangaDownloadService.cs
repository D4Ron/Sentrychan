using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.Services;

/// <summary>A chapter download that failed for real (as opposed to a chapter with nothing to download).</summary>
public class MangaDownloadException : Exception
{
    public MangaDownloadException(string message, Exception? inner = null) : base(message, inner) { }
}

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

    // One download per chapter. A second request — a double click, or reopening the manga
    // (switching into secret mode does) while a long chapter is still going — joins the
    // running one. Two writers on one folder is what failed the 500-page chapter on
    // 2026-09-26 ("496.webp is being used by another process").
    private readonly Dictionary<int, Task<string?>> _inFlight = new();

    private const int PageAttempts = 3;

    public Task<string?> DownloadChapterAsync(
        Manga manga, MangaChapter chapter, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        lock (_inFlight)
        {
            if (_inFlight.TryGetValue(chapter.Id, out var running)) return running;
            var task = DownloadChapterCoreAsync(manga, chapter, progress, ct);
            _inFlight[chapter.Id] = task;
            _ = task.ContinueWith(_ => { lock (_inFlight) _inFlight.Remove(chapter.Id); }, TaskScheduler.Default);
            return task;
        }
    }

    private async Task<string?> DownloadChapterCoreAsync(
        Manga manga, MangaChapter chapter, IProgress<double>? progress, CancellationToken ct)
    {
        await Task.Yield(); // the caller's lock above is released before any real work starts

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

        var failed = new List<int>();
        try
        {
            for (int i = 0; i < urls.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var ext  = Path.GetExtension(new Uri(urls[i]).AbsolutePath);
                if (string.IsNullOrEmpty(ext)) ext = ".jpg";
                var file = Path.Combine(dir, $"{(i + 1):D3}{ext}");

                // Resume: keep pages already on disk — but only real images. A page saved
                // by an older build may be a CDN error body that was written as-is.
                if (!(File.Exists(file) && IsImage(await ReadHeadAsync(file, ct))) &&
                    !await DownloadPageAsync(urls[i], file, source.ImageReferer, ct))
                {
                    failed.Add(i + 1);
                }
                progress?.Report((i + 1) / (double)urls.Count);
            }

            if (failed.Count > 0)
            {
                _logger.LogWarning("[MangaDL] {Title} {Chapter}: {Failed} of {Pages} pages failed ({List})",
                    manga.Title, chapterLbl, failed.Count, urls.Count, string.Join(", ", failed.Take(20)));
                throw new MangaDownloadException(
                    $"{failed.Count} of {urls.Count} pages couldn't be downloaded. " +
                    "Try again — pages already saved are kept.");
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
        catch (MangaDownloadException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MangaDL] Failed {Title} {Chapter}", manga.Title, chapterLbl);
            throw new MangaDownloadException($"Download failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Fetches one page, retrying, and writes it only if the bytes are an image — so an
    /// error page never lands on disk as a "page". Written to a temp file and renamed, so an
    /// interrupted write can't leave a truncated page behind either.
    /// </summary>
    private async Task<bool> DownloadPageAsync(string url, string file, string? referer, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= PageAttempts; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(referer)) req.Headers.Referrer = new Uri(referer);
                using var resp = await _http.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

                if (IsImage(bytes))
                {
                    var tmp = file + ".part";
                    await File.WriteAllBytesAsync(tmp, bytes, ct);
                    File.Move(tmp, file, overwrite: true);
                    return true;
                }
                _logger.LogDebug("[MangaDL] Not an image ({Len} bytes) from {Url}", bytes.Length, url);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[MangaDL] Page attempt {Attempt} failed: {Url}", attempt, url);
            }

            if (attempt < PageAttempts)
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
        }
        return false;
    }

    private static async Task<byte[]> ReadHeadAsync(string file, CancellationToken ct)
    {
        var head = new byte[12];
        await using var fs = File.OpenRead(file);
        var n = await fs.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        return head[..n];
    }

    /// <summary>JPEG, PNG, GIF, WebP, BMP or AVIF by magic bytes.</summary>
    private static bool IsImage(ReadOnlySpan<byte> b) =>
        (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) ||
        (b.Length >= 4 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) ||
        (b.Length >= 3 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F') ||
        (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' &&
                           b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') ||
        (b.Length >= 2 && b[0] == 'B' && b[1] == 'M') ||
        (b.Length >= 12 && b[4] == 'f' && b[5] == 't' && b[6] == 'y' && b[7] == 'p' &&
                           b[8] == 'a' && b[9] == 'v' && b[10] == 'i');

    private static bool IsImage(byte[] b) => IsImage(b.AsSpan());

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
