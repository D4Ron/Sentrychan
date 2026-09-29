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
    //
    // The queue is an ordered list rather than a semaphore so it can be paused and
    // reordered: running entries stay in it (flagged) until they finish, and the dispatcher
    // always starts the first entry that isn't running.
    private sealed class Entry
    {
        public required Manga Manga { get; init; }
        public required MangaChapter Chapter { get; init; }
        public IProgress<double>? Progress { get; init; }
        public required CancellationTokenSource UserCts { get; init; }
        public TaskCompletionSource<string?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource? RunCts { get; set; }
        public CancellationTokenRegistration CallerCancel { get; set; }
        public bool Running { get; set; }
    }

    private readonly object _queueLock = new();
    private readonly List<Entry> _queue = [];
    private int _running;
    private bool _paused;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, ChapterDownloadStatus> _status = new();

    // Two chapters at a time across the whole app: "Download all" on a long series queues
    // every chapter at once, and each chapter is already dozens of image requests.
    private const int Slots = 2;

    private const int PageAttempts = 3;

    public event Action<ChapterDownloadStatus>? StatusChanged;
    public event Action? QueueChanged;

    public ChapterDownloadStatus? GetStatus(int chapterId) => _status.GetValueOrDefault(chapterId);

    public bool IsPaused { get { lock (_queueLock) return _paused; } }

    public IReadOnlyList<MangaQueueItem> Queue
    {
        get
        {
            lock (_queueLock)
                return _queue.Select(e => new MangaQueueItem(e.Manga, e.Chapter,
                    GetStatus(e.Chapter.Id) ?? new ChapterDownloadStatus(e.Chapter.Id, e.Manga.Id, ChapterDownloadState.Queued),
                    e.Running)).ToList();
        }
    }

    private void Report(ChapterDownloadStatus s)
    {
        _status[s.ChapterId] = s;
        try { StatusChanged?.Invoke(s); } catch (Exception ex) { _logger.LogDebug(ex, "[MangaDL] status listener failed"); }
    }

    private void RaiseQueueChanged()
    {
        try { QueueChanged?.Invoke(); } catch (Exception ex) { _logger.LogDebug(ex, "[MangaDL] queue listener failed"); }
    }

    public void Cancel(int chapterId)
    {
        Entry? waiting = null;
        lock (_queueLock)
        {
            var e = _queue.FirstOrDefault(x => x.Chapter.Id == chapterId);
            if (e == null) return;
            e.UserCts.Cancel();
            // A running entry notices through its token and cleans up itself.
            if (!e.Running) { _queue.Remove(e); waiting = e; }
        }
        if (waiting != null) Finish(waiting, null, cancelled: true);
    }

    public void CancelAll(int mangaId)
    {
        List<int> ids;
        lock (_queueLock) ids = _queue.Where(e => e.Manga.Id == mangaId).Select(e => e.Chapter.Id).ToList();
        foreach (var id in ids) Cancel(id);
    }

    public void CancelEverything()
    {
        List<int> ids;
        lock (_queueLock) ids = _queue.Select(e => e.Chapter.Id).ToList();
        foreach (var id in ids) Cancel(id);
    }

    /// <summary>
    /// Stops starting chapters and interrupts the running ones, which go back to the front of
    /// the queue. Pages already saved are kept, so resuming picks up where each one stopped.
    /// </summary>
    public void PauseAll()
    {
        lock (_queueLock)
        {
            _paused = true;
            foreach (var e in _queue.Where(e => e.Running)) e.RunCts?.Cancel();
        }
        RaiseQueueChanged();
    }

    public void ResumeAll()
    {
        lock (_queueLock) _paused = false;
        Pump();
        RaiseQueueChanged();
    }

    /// <summary>Moves a queued chapter to a position in the queue (0 = next). Running chapters keep running.</summary>
    public void Move(int chapterId, int newIndex)
    {
        lock (_queueLock)
        {
            var e = _queue.FirstOrDefault(x => x.Chapter.Id == chapterId);
            if (e == null) return;
            _queue.Remove(e);
            _queue.Insert(Math.Clamp(newIndex, 0, _queue.Count), e);
        }
        RaiseQueueChanged();
    }

    public Task<string?> DownloadChapterAsync(
        Manga manga, MangaChapter chapter, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (InstanceGuard.PausedForOtherInstance)
        {
            Report(new ChapterDownloadStatus(chapter.Id, manga.Id, ChapterDownloadState.Failed,
                Error: InstanceGuard.PausedMessage));
            return Task.FromException<string?>(new MangaDownloadException(InstanceGuard.PausedMessage));
        }

        Entry entry;
        lock (_queueLock)
        {
            var existing = _queue.FirstOrDefault(e => e.Chapter.Id == chapter.Id);
            if (existing != null) return existing.Result.Task;
            entry = new Entry
            {
                Manga = manga, Chapter = chapter, Progress = progress,
                UserCts = CancellationTokenSource.CreateLinkedTokenSource(ct),
            };
            _queue.Add(entry);
        }
        // The caller's token cancels it too — while waiting as well as while running. Hooked on
        // the caller's token, not UserCts, so Cancel() cancelling UserCts doesn't re-enter itself.
        if (ct.CanBeCanceled) entry.CallerCancel = ct.Register(() => Cancel(chapter.Id));

        Report(new ChapterDownloadStatus(chapter.Id, manga.Id, ChapterDownloadState.Queued));
        RaiseQueueChanged();
        Pump();
        return entry.Result.Task;
    }

    /// <summary>Starts queued chapters while slots are free and the queue isn't paused.</summary>
    private void Pump()
    {
        var start = new List<Entry>();
        lock (_queueLock)
        {
            while (!_paused && _running < Slots && _queue.FirstOrDefault(e => !e.Running) is { } next)
            {
                next.Running = true;
                next.RunCts = CancellationTokenSource.CreateLinkedTokenSource(next.UserCts.Token);
                _running++;
                start.Add(next);
            }
        }
        foreach (var e in start) _ = Task.Run(() => RunAsync(e));
        if (start.Count > 0) RaiseQueueChanged();
    }

    private async Task RunAsync(Entry e)
    {
        string? path = null;
        Exception? failure = null;
        try
        {
            Report(new ChapterDownloadStatus(e.Chapter.Id, e.Manga.Id, ChapterDownloadState.Starting));
            path = await DownloadChapterCoreAsync(e.Manga, e.Chapter, e.Progress, e.RunCts!.Token);
        }
        catch (Exception ex) { failure = ex; }

        var cancelled = e.UserCts.IsCancellationRequested;
        var paused = !cancelled && e.RunCts!.IsCancellationRequested;

        lock (_queueLock)
        {
            _running--;
            e.Running = false;
            e.RunCts?.Dispose();
            e.RunCts = null;
            // Interrupted by a pause: it never left its place in the list, so it simply waits
            // there again — ahead of everything queued after it.
            if (!(paused && failure == null)) _queue.Remove(e);
        }

        if (paused && failure == null)
        {
            var prev = GetStatus(e.Chapter.Id);
            Report(new ChapterDownloadStatus(e.Chapter.Id, e.Manga.Id, ChapterDownloadState.Queued,
                prev?.PagesDone ?? 0, prev?.PagesTotal ?? 0));
        }
        else if (failure != null) Fail(e, failure);
        else Finish(e, path, cancelled);

        RaiseQueueChanged();
        Pump();
    }

    private void Finish(Entry e, string? path, bool cancelled)
    {
        var prev = GetStatus(e.Chapter.Id);
        Report(path != null
            ? new ChapterDownloadStatus(e.Chapter.Id, e.Manga.Id, ChapterDownloadState.Done, prev?.PagesTotal ?? 0, prev?.PagesTotal ?? 0)
            : cancelled
                ? (prev ?? new(e.Chapter.Id, e.Manga.Id, ChapterDownloadState.Cancelled)) with { State = ChapterDownloadState.Cancelled }
                : new ChapterDownloadStatus(e.Chapter.Id, e.Manga.Id, ChapterDownloadState.Failed,
                    Error: "This chapter has no pages the app can download — it's licensed or hosted elsewhere."));
        e.Result.TrySetResult(path);
        e.CallerCancel.Dispose();
        e.UserCts.Dispose();
        RaiseQueueChanged();
    }

    private void Fail(Entry e, Exception ex)
    {
        var prev = GetStatus(e.Chapter.Id);
        var error = ex is MangaDownloadException ? ex : new MangaDownloadException($"Download failed: {ex.Message}", ex);
        Report(new ChapterDownloadStatus(e.Chapter.Id, e.Manga.Id, ChapterDownloadState.Failed,
            prev?.PagesDone ?? 0, prev?.PagesTotal ?? 0, error.Message));
        e.Result.TrySetException(error);
        e.CallerCancel.Dispose();
        e.UserCts.Dispose();
    }

    private async Task<string?> DownloadChapterCoreAsync(
        Manga manga, MangaChapter chapter, IProgress<double>? progress, CancellationToken ct)
    {
        var source = _sources.Get(manga.Source);
        IReadOnlyList<string> urls;
        try { urls = await source.GetPageUrlsAsync(chapter.SourceId, dataSaver: false, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex) { throw new MangaDownloadException($"Couldn't get the page list: {ex.Message}", ex); }
        if (urls.Count == 0)
        {
            _logger.LogInformation("[MangaDL] {Title} ch {Ch} has no in-app pages (external)",
                manga.IsCensored ? Vault.Privacy.Placeholder : manga.Title, chapter.ChapterNumber);
            return null;
        }

        // Local-source pages are already files on disk — nothing to download.
        if (!urls[0].StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return null;

        var chapterLbl = ChapterLabel(chapter);

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
            if (_vault.Collection(target).Count == 0) await AdoptOrphanedPagesAsync(manga, chapter, target, ct);
            have = _vault.Collection(target).Select(e => e.Order).ToHashSet();
            // The index is encrypted, so it can say what this is — the vault page lists it by name.
            await _vault.DescribeCollectionAsync(target, chapterLbl, manga.Title, chapter.ChapterSort, save: false, ct: ct,
                expectedPages: urls.Count);
        }
        else
        {
            var root = await ResolveRootAsync(ct);
            target = Path.Combine(root, "Manga", Sanitize(manga.Title), Sanitize(chapterLbl));
            Directory.CreateDirectory(target);
            have = [];
        }

        var failed = new List<int>();
        void Progress(int done) =>
            Report(new ChapterDownloadStatus(chapter.Id, manga.Id, ChapterDownloadState.Downloading, done, urls.Count));
        Progress(toVault ? have.Count(i => i < urls.Count) : 0);
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
                        else
                        {
                            await _vault!.AddBytesAsync(bytes, $"Page {i + 1}", Vault.VaultKind.Page, target, i, ext, ct, save: false);
                            // Flush now and then so a crash keeps most pages for the resume.
                            if (i % 20 == 19) await _vault.FlushAsync(ct);
                        }
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
                Progress(i + 1);
            }

            if (toVault) await _vault!.FlushAsync(ct);
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
            if (toVault) await _vault!.FlushAsync(CancellationToken.None); // keep the pages that did arrive
            _logger.LogInformation("[MangaDL] Cancelled {Title} {Chapter}", logTitle, chapterLbl);
            return null;
        }
        catch (MangaDownloadException) { throw; }
        catch (Exception ex)
        {
            if (toVault) try { await _vault!.FlushAsync(CancellationToken.None); } catch { }
            _logger.LogWarning("[MangaDL] Failed {Title} {Chapter}: {Error}", logTitle, chapterLbl, ex.Message);
            throw new MangaDownloadException($"Download failed: {ex.Message}", ex);
        }
    }

    /// <summary>A chapter's DownloadedPath when its pages are in the vault: "vault:manga-chapter-&lt;id&gt;".</summary>
    public const string VaultPathPrefix = "vault:";

    private static string VaultCollection(int chapterId) => $"manga-chapter-{chapterId}";

    /// <summary>
    /// Chapter ids used to change whenever the chapter list refreshed, stranding a partial
    /// download under a key naming an id that no longer exists. Take those pages over when
    /// exactly one stranded collection has this title and chapter label.
    /// </summary>
    private async Task AdoptOrphanedPagesAsync(Manga manga, MangaChapter chapter, string target, CancellationToken ct)
    {
        var candidates = _vault!.FindCollections(manga.Title, ChapterLabel(chapter))
            .Where(k => k != target && ChapterIdFromCollection(k) != null).ToList();
        if (candidates.Count == 0) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var ids = candidates.Select(k => ChapterIdFromCollection(k)!.Value).ToList();
        var paths = candidates.Select(k => VaultPathPrefix + k).ToList();
        var owned = await db.MangaChapters
            .Where(c => ids.Contains(c.Id) || (c.DownloadedPath != null && paths.Contains(c.DownloadedPath)))
            .Select(c => new { c.Id, c.DownloadedPath }).ToListAsync(ct);
        var stranded = candidates.Where(k => !owned.Any(o => o.Id == ChapterIdFromCollection(k) || o.DownloadedPath == VaultPathPrefix + k)).ToList();
        if (stranded.Count != 1) return;

        await _vault.RekeyCollectionAsync(stranded[0], target, ct);
        _logger.LogInformation("[MangaDL] Resuming a stranded partial download ({Pages} pages kept)", _vault.Collection(target).Count);
    }

    /// <summary>The chapter id behind a vault collection key, for chapters the downloader put there.</summary>
    public static int? ChapterIdFromCollection(string? collection) =>
        collection != null && collection.StartsWith("manga-chapter-") && int.TryParse(collection["manga-chapter-".Length..], out var id)
            ? id : null;

    public static string ChapterLabel(MangaChapter chapter) =>
        string.IsNullOrEmpty(chapter.ChapterNumber) ? "Oneshot" : $"Chapter {chapter.ChapterNumber}";

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
            .Join(db.Manga.Where(m => m.IsCensored), c => c.MangaId, m => m.Id, (c, m) => new { c, m.Title })
            .ToListAsync(ct);

        var moved = 0;
        var emptied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in chapters)
        {
            ct.ThrowIfCancellationRequested();
            var chapter = row.c;
            var dir = chapter.DownloadedPath!;
            if (!Directory.Exists(dir)) { chapter.DownloadedPath = null; continue; }

            var key = VaultCollection(chapter.Id);
            await _vault.RemoveCollectionAsync(key, ct); // a half-finished earlier move starts over
            await _vault.DescribeCollectionAsync(key, ChapterLabel(chapter), row.Title, chapter.ChapterSort, save: false, ct: ct);
            var pages = Directory.EnumerateFiles(dir).Where(f => !f.EndsWith(".part"))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < pages.Count; i++)
                await _vault.AddFileAsync(pages[i], $"Page {i + 1}", Vault.VaultKind.Page, key, i, deleteSource: true, ct: ct, save: false);
            await _vault.FlushAsync(ct);

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
        _status.TryRemove(chapter.Id, out _);
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

        return AppPaths.DataDir;
    }

    private static string Sanitize(string name)
    {
        var cleaned = Regex.Replace(name, "[" + Regex.Escape(new string(Path.GetInvalidFileNameChars())) + "]", "_");
        return cleaned.Trim().TrimEnd('.'); // Windows dislikes trailing dots/spaces
    }
}
