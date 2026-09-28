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
    private readonly Vault.VaultService? _vault;

    public MangaDownloadService(
        IMangaSourceRegistry sources,
        IMangaService mangaService,
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<MangaDownloadService> logger,
        Vault.VaultService? vault = null)
    {
        _vault = vault;
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

        var chapterLbl = string.IsNullOrEmpty(chapter.ChapterNumber) ? "Oneshot" : $"Chapter {chapter.ChapterNumber}";

        // Adult titles go to the vault: encrypted, one entry per page, grouped under a key
        // that names nothing. No folder with the title in it is ever created.
        var toVault = manga.IsCensored && _vault != null;
        var logTitle = toVault ? Vault.Privacy.Placeholder : manga.Title;
        string target;
        HashSet<int> have;
        if (toVault)
        {
            await _vault!.EnsureReadyAsync(ct);
            target = VaultCollection(chapter.Id);
            have = _vault.Collection(target).Select(e => e.Order).ToHashSet();
        }
        else
        {
            var root = await ResolveRootAsync(ct);
            target = Path.Combine(root, "Manga", Sanitize(manga.Title), Sanitize(chapterLbl));
            Directory.CreateDirectory(target);
            have = [];
        }

        var failed = new List<int>();
        try
        {
            for (int i = 0; i < urls.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var ext  = Path.GetExtension(new Uri(urls[i]).AbsolutePath);
                if (string.IsNullOrEmpty(ext)) ext = ".jpg";

                if (toVault)
                {
                    if (!have.Contains(i))
                    {
                        var bytes = await FetchPageAsync(urls[i], source.ImageReferer, ct);
                        if (bytes == null) failed.Add(i + 1);
                        else await _vault!.AddBytesAsync(bytes, $"Page {i + 1}", Vault.VaultKind.Page, target, i, ext, ct);
                    }
                }
                else
                {
                    var file = Path.Combine(target, $"{(i + 1):D3}{ext}");
                    // Resume: keep pages already on disk — but only real images. A page saved
                    // by an older build may be a CDN error body that was written as-is.
                    if (!(File.Exists(file) && IsImage(await ReadHeadAsync(file, ct))))
                    {
                        var bytes = await FetchPageAsync(urls[i], source.ImageReferer, ct);
                        if (bytes == null) failed.Add(i + 1);
                        else
                        {
                            // Temp + rename: an interrupted write can't leave a truncated page.
                            var tmp = file + ".part";
                            await File.WriteAllBytesAsync(tmp, bytes, ct);
                            File.Move(tmp, file, overwrite: true);
                        }
                    }
                }
                progress?.Report((i + 1) / (double)urls.Count);
            }

            if (failed.Count > 0)
            {
                _logger.LogWarning("[MangaDL] {Title} {Chapter}: {Failed} of {Pages} pages failed ({List})",
                    logTitle, chapterLbl, failed.Count, urls.Count, string.Join(", ", failed.Take(20)));
                throw new MangaDownloadException(
                    $"{failed.Count} of {urls.Count} pages couldn't be downloaded. " +
                    "Try again — pages already saved are kept.");
            }

            var stored = toVault ? VaultPathPrefix + target : target;
            await SetDownloadedPathAsync(chapter.Id, stored, ct);
            _logger.LogInformation("[MangaDL] {Title} {Chapter} → {Dir} ({Pages} pages)",
                logTitle, chapterLbl, toVault ? "vault" : target, urls.Count);
            return stored;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[MangaDL] Cancelled {Title} {Chapter}", logTitle, chapterLbl);
            return null;
        }
        catch (MangaDownloadException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("[MangaDL] Failed {Title} {Chapter}: {Error}", logTitle, chapterLbl, ex.Message);
            throw new MangaDownloadException($"Download failed: {ex.Message}", ex);
        }
    }

    /// <summary>A chapter's DownloadedPath when its pages are in the vault: "vault:manga-chapter-&lt;id&gt;".</summary>
    public const string VaultPathPrefix = "vault:";

    private static string VaultCollection(int chapterId) => $"manga-chapter-{chapterId}";

    /// <summary>
    /// Fetches one page, retrying, and returns it only if the bytes are an image — so an
    /// error page never gets saved as a "page". Null after the last attempt fails.
    /// </summary>
    private async Task<byte[]?> FetchPageAsync(string url, string? referer, CancellationToken ct)
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
                if (IsImage(bytes)) return bytes;
                _logger.LogDebug("[MangaDL] Not an image ({Len} bytes)", bytes.Length);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug("[MangaDL] Page attempt {Attempt} failed: {Error}", attempt, ex.Message);
            }

            if (attempt < PageAttempts)
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
        }
        return null;
    }

    /// <summary>
    /// Moves adult chapters that were downloaded as plain folders (before the vault existed)
    /// into the vault, removing the folders. Returns how many chapters moved.
    /// </summary>
    public async Task<int> MoveAdultDownloadsIntoVaultAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (_vault == null) return 0;
        await _vault.EnsureReadyAsync(ct);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var chapters = await db.MangaChapters
            .Where(c => c.DownloadedPath != null && !c.DownloadedPath.StartsWith(VaultPathPrefix))
            .Join(db.Manga.Where(m => m.IsCensored), c => c.MangaId, m => m.Id, (c, m) => c)
            .ToListAsync(ct);

        var moved = 0;
        var emptied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chapter in chapters)
        {
            ct.ThrowIfCancellationRequested();
            var dir = chapter.DownloadedPath!;
            if (!Directory.Exists(dir)) { chapter.DownloadedPath = null; continue; }

            var key = VaultCollection(chapter.Id);
            await _vault.RemoveCollectionAsync(key, ct); // a half-finished earlier move starts over
            var pages = Directory.EnumerateFiles(dir).Where(f => !f.EndsWith(".part"))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < pages.Count; i++)
                await _vault.AddFileAsync(pages[i], $"Page {i + 1}", Vault.VaultKind.Page, key, i, deleteSource: true, ct: ct);

            try { Directory.Delete(dir, recursive: true); } catch { /* leftovers are non-page files */ }
            var parent = Path.GetDirectoryName(dir);
            if (parent != null) emptied.Add(parent);

            chapter.DownloadedPath = VaultPathPrefix + key;
            await db.SaveChangesAsync(ct);
            moved++;
            progress?.Report($"Moved {moved} of {chapters.Count} chapters");
        }

        // The per-title folders held nothing else; remove them so the title is gone from disk.
        foreach (var parent in emptied)
            try { if (!Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent); } catch { }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("[MangaDL] Moved {Count} downloaded chapter(s) into the vault", moved);
        return moved;
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
            if (chapter.DownloadedPath?.StartsWith(VaultPathPrefix) == true && _vault != null)
                await _vault.RemoveCollectionAsync(chapter.DownloadedPath[VaultPathPrefix.Length..], ct);
            else if (!string.IsNullOrEmpty(chapter.DownloadedPath) && Directory.Exists(chapter.DownloadedPath))
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
