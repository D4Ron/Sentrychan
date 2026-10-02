using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.Events;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using System.Text.RegularExpressions;

namespace Sentrychan.Core.Services;

public class FileMovementPipeline : IFileMovementPipeline
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IEpisodeNormalizer _normalizer;
    private readonly ITitleResolverService _titleResolver;
    private readonly IMediator _mediator;
    private readonly ILogger<FileMovementPipeline> _logger;

    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".avi", ".webm", ".m4v", ".mov", ".flv", ".wmv" };

    private static readonly Regex InvalidFolderCharsPattern =
        new(@"[<>:""/\\|?*\x00-\x1F]", RegexOptions.Compiled);

    // Retry state for FDM file lock detection
    private readonly Dictionary<string, int> _retryCount = new();
    private const int MaxRetries = 10;

    // Partial files get re-seen on every watcher event and startup scan; warn once each.
    private readonly HashSet<string> _warnedIncomplete = new(StringComparer.OrdinalIgnoreCase);

    private readonly Vault.VaultService? _vault;

    public FileMovementPipeline(
        IDbContextFactory<AppDbContext> dbFactory,
        IEpisodeNormalizer normalizer,
        ITitleResolverService titleResolver,
        IMediator mediator,
        ILogger<FileMovementPipeline> logger,
        Vault.VaultService? vault = null)
    {
        _dbFactory     = dbFactory;
        _normalizer    = normalizer;
        _titleResolver = titleResolver;
        _mediator      = mediator;
        _logger        = logger;
        _vault         = vault;
    }

    // ── Entry point 1: Backend completion (qBit / MonoTorrent) ───

    public async Task OnBackendCompletedAsync(
        string torrentHash,
        string filePath,
        string originalTitle,
        CancellationToken ct = default)
    {
        // Private (Sentrykun) downloads never reach the library: encrypted into the vault,
        // plaintext removed, and nothing naming them written to the log or the database.
        if (_vault != null) await EnsureVaultAsync(ct);
        if (_vault != null && _vault.IsPrivateDownload(torrentHash))
        {
            await FileIntoVaultAsync(torrentHash, filePath, ct);
            return;
        }

        _logger.LogInformation(
            "[Pipeline] Backend completion: hash={Hash} path={Path}", torrentHash, filePath);

        // Collect every video file this completion refers to. Single-file torrents
        // report the file directly; multi-file (batch) torrents report the folder.
        List<string> videoFiles;
        if (File.Exists(filePath))
        {
            videoFiles = [filePath];
        }
        else if (Directory.Exists(filePath))
        {
            videoFiles = Directory.GetFiles(filePath, "*", SearchOption.AllDirectories)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (videoFiles.Count == 0)
            {
                _logger.LogWarning(
                    "[Pipeline] No video file found in torrent folder: {Path}", filePath);
                return;
            }
        }
        else
        {
            _logger.LogWarning("[Pipeline] File not found at reported path: {Path}", filePath);
            return;
        }

        filePath = videoFiles[0];

        // The engine only reports what it believes. Stale resume data for a file that had
        // been moved away once let a torrent "complete" with a quarter of it never written
        // (Mushoku Tensei S3 13, Sep 2026) — so check before filing, even here.
        var damaged = videoFiles.Where(f => VideoIntegrity.LooksIncomplete(f, ct)).ToList();
        if (damaged.Count > 0)
        {
            _logger.LogError(
                "[Pipeline] Torrent {Hash} reported complete but {Count} file(s) have unwritten regions — " +
                "left in the download folder: {Files}",
                torrentHash, damaged.Count, string.Join(", ", damaged.Select(Path.GetFileName)));
            videoFiles = videoFiles.Except(damaged).ToList();
            if (videoFiles.Count == 0) return;
            filePath = videoFiles[0];
        }

        // Look up the DownloadJob by torrent hash — exact match, no parsing needed
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // A hash can have several rows (a repair or retry re-adds the same torrent); the one
        // being completed is the newest still in flight, not whichever row sorts first.
        var job = await db.DownloadJobs
            .Include(j => j.Series)
            .Where(j => j.TorrentHash == torrentHash && j.Status != JobStatus.Completed)
            .OrderByDescending(j => j.Status == JobStatus.Downloading)
            .ThenByDescending(j => j.Id)
            .FirstOrDefaultAsync(ct);

        if (job == null)
        {
            _logger.LogWarning(
                "[Pipeline] No open DownloadJob found for hash {Hash}. " +
                "Falling through to filesystem path.", torrentHash);
            // Treat as a filesystem event so the fuzzy match can still handle it
            await OnFileSystemEventAsync(filePath, ct);
            return;
        }

        // If DownloadJob was created without a SeriesId (legacy path), try to
        // resolve the series from the file name using the fuzzy match strategy.
        var series = job.Series;
        if (series == null && job.SeriesId is > 0)
        {
            series = await db.Series.FindAsync(new object[] { job.SeriesId.Value }, ct);
        }
        if (series == null)
        {
            // SeriesId 0 = intentional "just download" (not in library): organize
            // into Library/_Standalone/<title> instead of treating it as unmatched.
            _logger.LogInformation(
                "[Pipeline] DownloadJob {Id} has no Series (SeriesId={SId}) — standalone finalize.",
                job.Id, job.SeriesId);
            await FinalizeStandaloneAsync(job, videoFiles, db, ct);
            return;
        }

        if (videoFiles.Count == 1)
        {
            await FinalizeAsync(job, series, filePath, db, ct);
        }
        else
        {
            // Batch torrent: organize every episode inside it.
            await FinalizeBatchAsync(job, series, videoFiles, db, ct);
        }
    }

    private async Task EnsureVaultAsync(CancellationToken ct)
    {
        try { await _vault!.EnsureReadyAsync(ct); }
        catch (Exception ex) { _logger.LogWarning("[Pipeline] Vault unavailable: {Error}", ex.Message); }
    }

    private async Task FileIntoVaultAsync(string torrentHash, string reportedPath, CancellationToken ct)
    {
        var files = File.Exists(reportedPath) ? [reportedPath]
            : Directory.Exists(reportedPath)
                ? Directory.GetFiles(reportedPath, "*", SearchOption.AllDirectories)
                           .Where(f => VideoExtensions.Contains(Path.GetExtension(f)))
                           .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();

        var stored = 0;
        foreach (var file in files)
        {
            try
            {
                await _vault!.AddFileAsync(file, CleanReleaseTitle(Path.GetFileName(file)),
                    Vault.VaultKind.Video, deleteSource: true, ct: ct);
                stored++;
            }
            catch (Exception ex)
            {
                _logger.LogError("[Pipeline] A private download couldn't be encrypted into the vault: {Error}", ex.Message);
            }
        }

        // A batch leaves a folder of extras (.nfo, fonts, covers) — plaintext too.
        if (Directory.Exists(reportedPath) && stored == files.Count)
        {
            try { Directory.Delete(reportedPath, recursive: true); } catch { /* best effort */ }
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var jobs = await db.DownloadJobs.Where(j => j.TorrentHash == torrentHash).ToListAsync(ct);
        foreach (var job in jobs)
        {
            await _vault!.ForgetPrivateAsync([job.DownloadLink, job.ExpectedFileName, torrentHash], ct);
            job.Status           = stored > 0 ? JobStatus.Completed : JobStatus.Failed;
            job.CompletedAt      = DateTime.UtcNow;
            job.RssTitle         = Vault.Privacy.Placeholder;
            job.ExpectedFileName = null;
            job.DownloadLink     = string.Empty;
            job.FinalFilePath    = "vault";
        }
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("[Pipeline] Private download stored in the vault ({Count} file(s))", stored);
        await _mediator.Publish(new NewFileArrivedEvent(
            SeriesId: 0, MalId: 0, SeriesTitle: Vault.Privacy.Placeholder, EpisodeNumber: 0,
            FinalFilePath: "vault", WasLastEpisodeUpdated: false), ct);
    }

    /// <summary>
    /// Completion path for downloads with no library series ("just download" from
    /// the Latest page). Files go to Library/_Standalone/&lt;CleanTitle&gt; so they're
    /// organized and easy to find, without polluting _Unmatched.
    /// </summary>
    private async Task FinalizeStandaloneAsync(
        DownloadJob job,
        List<string> videoFiles,
        AppDbContext db,
        CancellationToken ct)
    {
        var libraryPath = await GetConfigValueAsync(db, "LibraryPath", ct);
        string finalPath = videoFiles[0];
        string folderName = "Unknown";

        if (!string.IsNullOrEmpty(libraryPath) && Directory.Exists(libraryPath))
        {
            var sourceName = !string.IsNullOrEmpty(job.RssTitle)
                ? job.RssTitle
                : Path.GetFileName(videoFiles[0]);
            folderName = SanitizeFolderName(
                SeasonDetector.ExtractBaseTitle(CleanReleaseTitle(sourceName)));

            var destDir = Path.Combine(libraryPath, "_Standalone", folderName);
            Directory.CreateDirectory(destDir);

            foreach (var file in videoFiles)
            {
                var destPath = Path.Combine(destDir, Path.GetFileName(file));
                if (File.Exists(destPath) && destPath != file) continue;
                try
                {
                    if (file != destPath) File.Move(file, destPath);
                    finalPath = destPath;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Pipeline] Standalone move failed for '{File}'", file);
                }
            }

            _logger.LogInformation(
                "[Pipeline] Standalone download organized: {Count} file(s) → _Standalone/{Folder}",
                videoFiles.Count, folderName);
        }
        else
        {
            _logger.LogWarning(
                "[Pipeline] Library path not configured — standalone download stays at {Path}", finalPath);
        }

        job.Status        = JobStatus.Completed;
        job.CompletedAt   = DateTime.UtcNow;
        job.FinalFilePath = finalPath;
        await db.SaveChangesAsync(ct);

        // SeriesId 0 signals "standalone" to the UI handler (different toast).
        await _mediator.Publish(new NewFileArrivedEvent(
            SeriesId: 0,
            MalId: 0,
            SeriesTitle: folderName,
            EpisodeNumber: job.EpisodeNumber,
            FinalFilePath: finalPath,
            WasLastEpisodeUpdated: false), ct);
    }

    /// <summary>
    /// Derives a human-readable folder name from a release title:
    /// "[Group] Mushoku Tensei - 05 (1080p) [ABC].mkv" → "Mushoku Tensei".
    /// </summary>
    private static string CleanReleaseTitle(string raw)
    {
        var s = Path.GetFileNameWithoutExtension(raw);
        s = Regex.Replace(s, @"\[[^\]]*\]", " ");          // [group] / [hash] tags
        s = Regex.Replace(s, @"\([^)]*\)", " ");            // (1080p) etc.
        s = Regex.Replace(s, @"[-_ ]+\d{1,4}(v\d)?\s*$", " "); // trailing episode number
        s = Regex.Replace(s, @"\s{2,}", " ").Trim(' ', '-', '_');
        return string.IsNullOrWhiteSpace(s) ? "Unknown" : s;
    }

    /// <summary>
    /// Completion path for multi-file (batch) torrents. Moves each video into
    /// Library/BaseTitle/Season N, records a per-episode DownloadJob, advances
    /// LastEpisodeNumber to the highest episode found, and completes the batch job.
    /// </summary>
    private async Task FinalizeBatchAsync(
        DownloadJob job,
        Series series,
        List<string> videoFiles,
        AppDbContext db,
        CancellationToken ct)
    {
        var libraryPath = await GetConfigValueAsync(db, "LibraryPath", ct);
        if (string.IsNullOrEmpty(libraryPath) || !Directory.Exists(libraryPath))
        {
            _logger.LogWarning(
                "[Pipeline] Library path not configured — batch stays at source. " +
                "Configure Library Path in Settings.");
            await CompleteJobAsync(job, series, videoFiles[0], db, ct);
            return;
        }

        var naming = await LoadNamingAsync(db, ct);
        var showSeasons = await ShowSeasonsAsync(db, ct);
        string? showFolder = null;

        int previousEpisode = series.LastEpisodeNumber;
        int maxEpisode      = series.LastEpisodeNumber;
        int movedCount      = 0;
        string lastDest     = videoFiles[0];

        foreach (var file in videoFiles)
        {
            if (ct.IsCancellationRequested) break;

            var fileName   = Path.GetFileNameWithoutExtension(file);
            var episodeNum = _normalizer.ExtractEpisodeNumber(fileName);

            // Per-file season detection (batches can span seasons)
            int seasonNumber = SeasonSearch.EffectiveSeason(series.Title, series.SeasonNumber);
            var detected = SeasonDetector.DetectSeason(fileName);
            if (detected > 1 || SeasonDetector.HasSeasonIndicator(fileName))
                seasonNumber = detected;

            var (relative, renamed) = Library.LibraryFiling.Destination(
                naming, series, showSeasons, Path.GetFileName(file), seasonNumber, episodeNum);
            var destPath = Path.Combine(libraryPath, relative);
            var destDir  = Path.GetDirectoryName(destPath)!;
            Directory.CreateDirectory(destDir);
            showFolder ??= Path.Combine(libraryPath, relative.Split(Path.DirectorySeparatorChar)[0]);

            if (File.Exists(destPath) && destPath != file &&
                ResolveCollision(file, destPath) != Collision.ReplaceDamaged)
            {
                _logger.LogInformation(
                    "[Pipeline] Batch: '{File}' already in library, skipping", fileName);
                continue;
            }

            try
            {
                if (file != destPath) File.Move(file, destPath);
                movedCount++;
                lastDest = destPath;
                if (renamed) await Library.TidyRecords.MoveOriginAsync(db, file, destPath, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Pipeline] Batch: move failed for '{File}'", file);
                continue;
            }

            if (episodeNum.HasValue)
            {
                db.DownloadJobs.Add(new DownloadJob
                {
                    SeriesId      = series.Id,
                    EpisodeNumber = episodeNum.Value,
                    DownloadLink  = job.DownloadLink,
                    RssTitle      = Path.GetFileName(file),
                    Backend       = job.Backend,
                    Status        = JobStatus.Completed,
                    CreatedAt     = DateTime.UtcNow,
                    CompletedAt   = DateTime.UtcNow,
                    FinalFilePath = destPath
                });
                if (episodeNum.Value > maxEpisode) maxEpisode = episodeNum.Value;
            }
        }

        job.Status        = JobStatus.Completed;
        job.CompletedAt   = DateTime.UtcNow;
        job.FinalFilePath = showFolder ?? Path.Combine(libraryPath, Library.LibraryFiling.LegacyShowFolder(series.Title));

        bool episodeUpdated = maxEpisode > previousEpisode;
        if (episodeUpdated) series.LastEpisodeNumber = maxEpisode;

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[Pipeline] Batch complete: {Title} — {Count} file(s) organized, LastEp {Old} → {New}",
            series.Title, movedCount, previousEpisode, maxEpisode);

        // One event for the whole batch — refreshes the library UI once.
        await _mediator.Publish(new NewFileArrivedEvent(
            SeriesId: series.Id,
            MalId: series.MalId,
            SeriesTitle: series.Title,
            EpisodeNumber: maxEpisode,
            FinalFilePath: lastDest,
            WasLastEpisodeUpdated: episodeUpdated), ct);
    }

    // ── Entry point 2: Filesystem event (FDM / manual) ───────────

    public async Task OnFileSystemEventAsync(string filePath, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(filePath);
        if (!VideoExtensions.Contains(ext)) return;
        if (!File.Exists(filePath)) return;

        // ── File lock check ───────────────────────────────────────
        // FDM holds an exclusive write lock while downloading.
        // If we can't open with FileShare.None, the file is still being written.
        if (!IsFileComplete(filePath))
        {
            _retryCount.TryGetValue(filePath, out var retries);
            if (retries >= MaxRetries)
            {
                _logger.LogWarning(
                    "[Pipeline] File still locked after {Max} retries, giving up: {Path}",
                    MaxRetries, filePath);
                _retryCount.Remove(filePath);
                return;
            }
            _retryCount[filePath] = retries + 1;
            _logger.LogDebug("[Pipeline] File locked, retry {Retry}/{Max}: {Path}",
                retries + 1, MaxRetries, filePath);

            // Schedule a retry via fire-and-forget after 3 seconds
            _ = Task.Delay(3000, ct).ContinueWith(
                async _ => await OnFileSystemEventAsync(filePath, ct),
                TaskContinuationOptions.OnlyOnRanToCompletion);
            return;
        }

        _retryCount.Remove(filePath);

        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var exactName = Path.GetFileName(filePath);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // An unlocked file is not a finished file. A torrent engine pre-allocates the
        // whole file and lets go of it when the app closes, so after a restart a half-done
        // download looks exactly like a finished one to the lock check above. The torrent's
        // own completion event is the only trustworthy signal for files it owns.
        if (await IsOwnedByUnfinishedTorrentAsync(db, exactName, ct))
        {
            _logger.LogDebug("[Pipeline] '{File}' belongs to an unfinished torrent — waiting for it.", exactName);
            return;
        }

        // Safety net for everything else (external downloaders, torrents we lost track of):
        // a zero-filled region means data that never arrived.
        if (VideoIntegrity.LooksIncomplete(filePath, ct))
        {
            if (_warnedIncomplete.Add(filePath))
                _logger.LogWarning("[Pipeline] '{File}' has unwritten regions — not moving an unfinished download.", exactName);
            return;
        }

        _logger.LogInformation("[Pipeline] Filesystem event, file complete: {Path}", filePath);

        // ── Match strategy 1: exact ExpectedFileName lookup ───────
        var jobByName = await db.DownloadJobs
            .Include(j => j.Series)
            .FirstOrDefaultAsync(j =>
                j.ExpectedFileName == exactName &&
                j.Status != JobStatus.Completed, ct);

        if (jobByName != null)
        {
            await FinalizeAsync(jobByName, jobByName.Series, filePath, db, ct);
            return;
        }

        // Organize mode: "Own" (default) only touches files tied to a Sentrychan
        // download or a tracked library series; "Watch" treats the whole folder as an
        // anime dropzone. Anything else is LEFT ALONE — this is a shared Downloads
        // folder, not ours to scavenge.
        var watchFolderMode = (await GetConfigValueAsync(db, "DownloadOrganizeMode", ct)) == "Watch";

        // ── Match strategy 2: canonical-id resolution + episode ───
        var episodeNum =
            (_titleResolver.IsReady ? _titleResolver.ParseRelease(fileName).Episode : null)
            ?? _normalizer.ExtractEpisodeNumber(fileName);
        if (episodeNum == null)
        {
            // No episode marker → almost never a tracked episode. Leave it be.
            _logger.LogDebug("[Pipeline] '{File}' has no episode marker — ignoring.", fileName);
            return;
        }

        var allSeries = await db.Series
            .Where(s => s.MonitoringState == MonitoringState.Active)
            .ToListAsync(ct);

        Series? matchedSeries = null;

        // Resolve the file name to a canonical MAL id (offline synonym DB) and match by id — far
        // more accurate than string fuzzy-matching — cour-aware, so "Show - 48" counted straight
        // through lands on the cour MAL numbers it 8 in, as episode 8.
        if (_titleResolver.IsReady)
        {
            foreach (var series in allSeries)
            {
                var (verdict, episode) = ReleaseMatcher.Match(_titleResolver, fileName, series);
                if (verdict != ReleaseVerdict.Yes) continue;
                matchedSeries = series;
                if (episode is { } e) episodeNum = e;
                break;
            }
        }

        // Fallback: legacy title fuzzy match
        if (matchedSeries == null)
        {
            foreach (var series in allSeries)
            {
                var allTitles = GetAllTitles(series);
                if (allTitles.Any(t => _normalizer.MatchesTitle(fileName, t)))
                {
                    matchedSeries = series;
                    break;
                }
            }
        }

        if (matchedSeries == null)
        {
            // Not tracked in the library.
            // "Own downloads only" (default): don't touch files we didn't download and
            // the user isn't tracking — leave them in the Downloads folder.
            if (!watchFolderMode)
            {
                _logger.LogDebug(
                    "[Pipeline] '{File}' isn't tracked and wasn't downloaded by Sentrychan — leaving it (Own-downloads mode).",
                    fileName);
                return;
            }

            // "Watch folder" mode: if the resolver can IDENTIFY the anime, file it into
            // _Standalone; if it merely LOOKS like an anime release, send to _Unmatched;
            // otherwise leave it (it's probably not anime at all).
            if (_titleResolver.IsReady)
            {
                var identified = _titleResolver.ResolveRelease(fileName);
                if (identified != null && !string.IsNullOrWhiteSpace(identified.CanonicalTitle))
                {
                    await MoveToStandaloneAsync(filePath, identified, episodeNum, db, ct);
                    return;
                }
            }

            if (LooksLikeAnimeRelease(fileName))
            {
                _logger.LogInformation("[Pipeline] Unidentified anime-looking file '{File}' → _Unmatched.", fileName);
                await MoveToUnmatchedAsync(filePath, ct);
            }
            else
            {
                _logger.LogDebug("[Pipeline] '{File}' doesn't look like an anime release — ignoring.", fileName);
            }
            return;
        }

        // Find or create a DownloadJob for this match
        var jobByEpisode = await db.DownloadJobs
            .FirstOrDefaultAsync(j =>
                j.SeriesId == matchedSeries.Id &&
                j.EpisodeNumber == episodeNum.Value &&
                j.Status != JobStatus.Completed, ct);

        // Same rule as above for releases whose file name differs from the feed title.
        if (jobByEpisode != null && IsTorrentInFlight(jobByEpisode))
        {
            _logger.LogDebug(
                "[Pipeline] '{File}' matches {Title} Ep {Ep}, which a torrent is still downloading — waiting.",
                fileName, matchedSeries.Title, episodeNum.Value);
            return;
        }

        if (jobByEpisode == null)
        {
            // File was downloaded externally (e.g. FDM without going through the hub)
            // Create an implicit job record
            jobByEpisode = new DownloadJob
            {
                SeriesId      = matchedSeries.Id,
                EpisodeNumber = episodeNum.Value,
                DownloadLink  = filePath,
                RssTitle      = fileName,
                Backend       = DownloadBackend.FDM,
                Status        = JobStatus.Downloading,
                CreatedAt     = DateTime.UtcNow,
                Series        = matchedSeries
            };
            db.DownloadJobs.Add(jobByEpisode);
            await db.SaveChangesAsync(ct);
        }

        await FinalizeAsync(jobByEpisode, matchedSeries, filePath, db, ct);
    }

    // ── Shared finalization path ──────────────────────────────────

    private async Task FinalizeAsync(
        DownloadJob job,
        Series series,
        string sourcePath,
        AppDbContext db,
        CancellationToken ct)
    {
        // Guard: the folder-watcher can reach here with an unmatched file (no series).
        // Route it to the unmatched flow instead of NRE-ing on series.Title.
        if (series == null)
        {
            _logger.LogInformation("[Pipeline] Finalize called with no series for '{Path}' — leaving as unmatched.", sourcePath);
            return;
        }

        // Build destination: /Anime/{SeriesTitle}/Season {N}/{filename}
        var libraryPath = await GetConfigValueAsync(db, "LibraryPath", ct);
        if (string.IsNullOrEmpty(libraryPath) || !Directory.Exists(libraryPath))
        {
            _logger.LogWarning(
                "[Pipeline] Library path not configured or not found. " +
                "File stays at {Source}. Configure Library Path in Settings.", sourcePath);
            // Still mark the job complete — the file is downloaded, just not moved
            await CompleteJobAsync(job, series, sourcePath, db, ct);
            return;
        }

        // Prefer season detected from RSS title (most accurate for what was actually downloaded)
        // Fall back to the series' own season — stored, or in its title ("Oshi no Ko 2nd Season").
        int seasonNumber = SeasonSearch.EffectiveSeason(series.Title, series.SeasonNumber);
        if (!string.IsNullOrEmpty(job.RssTitle))
        {
            var rssDetected = SeasonDetector.DetectSeason(job.RssTitle);
            if (rssDetected > 1 || SeasonDetector.HasSeasonIndicator(job.RssTitle))
                seasonNumber = rssDetected;
        }

        // The naming template decides the folders and name — "Show (2023)/Season 02/Show S02E05.mkv"
        // by default — exactly as Tidy library would, so a later tidy has nothing to redo.
        var (relative, renamed) = Library.LibraryFiling.Destination(
            await LoadNamingAsync(db, ct), series, await ShowSeasonsAsync(db, ct),
            Path.GetFileName(sourcePath), seasonNumber, job.EpisodeNumber > 0 ? job.EpisodeNumber : null);
        var destPath = Path.Combine(libraryPath, relative);
        var destDir  = Path.GetDirectoryName(destPath)!;
        Directory.CreateDirectory(destDir);

        var alreadyFiled = false;
        if (File.Exists(destPath) && destPath != sourcePath)
        {
            switch (ResolveCollision(sourcePath, destPath))
            {
                case Collision.ReplaceDamaged:
                    break;   // the damaged copy is gone; move the good one into its place
                case Collision.SameRelease:
                    alreadyFiled = true;
                    break;
                default:
                    var name = Path.GetFileNameWithoutExtension(sourcePath);
                    var ext2 = Path.GetExtension(sourcePath);
                    destPath = Path.Combine(destDir, $"{name}_dup{DateTime.Now.Ticks}{ext2}");
                    break;
            }
        }

        try
        {
            if (alreadyFiled) { /* nothing to move — the library copy stands */ }
            else if (sourcePath != destPath)
            {
                File.Move(sourcePath, destPath);
                _logger.LogInformation(
                    "[Pipeline] Moved '{File}' → '{Dest}'",
                    Path.GetFileName(sourcePath), destPath);
                // Episode repair finds this file's torrent by the name it downloaded as.
                if (renamed) await Library.TidyRecords.MoveOriginAsync(db, sourcePath, destPath, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Pipeline] Move failed for '{Source}'", sourcePath);
            // Mark complete at the source location rather than failing entirely
            destPath = sourcePath;
        }

        await CompleteJobAsync(job, series, destPath, db, ct);
    }

    private async Task CompleteJobAsync(
        DownloadJob job,
        Series series,
        string finalPath,
        AppDbContext db,
        CancellationToken ct)
    {
        job.Status       = JobStatus.Completed;
        job.CompletedAt  = DateTime.UtcNow;
        job.FinalFilePath = finalPath;
        await db.SaveChangesAsync(ct);

        // ── Update LastEpisodeNumber if this episode advances the counter ─
        bool episodeUpdated = false;
        int previousEpisode = series.LastEpisodeNumber;

        if (job.EpisodeNumber > series.LastEpisodeNumber)
        {
            series.LastEpisodeNumber = job.EpisodeNumber;
            await db.SaveChangesAsync(ct);
            episodeUpdated = true;

            _logger.LogInformation(
                "[Pipeline] {Title} LastEpisodeNumber {Old} → {New}",
                series.Title, previousEpisode, job.EpisodeNumber);
        }

        // ── Publish completion event (triggers episode grid refresh) ─
        await _mediator.Publish(new NewFileArrivedEvent(
            SeriesId: series.Id,
            MalId: series.MalId,
            SeriesTitle: series.Title,
            EpisodeNumber: job.EpisodeNumber,
            FinalFilePath: finalPath,
            WasLastEpisodeUpdated: episodeUpdated), ct);

        // ── Publish undo event if episode number changed ──────────
        if (episodeUpdated)
        {
            await _mediator.Publish(new UndoableEpisodeUpdateEvent(
                SeriesId: series.Id,
                MalId: series.MalId,
                SeriesTitle: series.Title,
                NewEpisodeNumber: job.EpisodeNumber,
                PreviousEpisodeNumber: previousEpisode,
                ExpiresAt: DateTime.UtcNow.AddSeconds(10)), ct);
        }
    }

    /// <summary>
    /// A file the resolver identified but that belongs to no library series:
    /// organize into Library/_Standalone/&lt;Canonical Title&gt; so it's findable
    /// without requiring the user to track the show.
    /// </summary>
    private async Task MoveToStandaloneAsync(
        string filePath,
        ResolvedAnime identified,
        int? episodeNum,
        AppDbContext db,
        CancellationToken ct)
    {
        var libraryPath = await GetConfigValueAsync(db, "LibraryPath", ct);
        if (string.IsNullOrEmpty(libraryPath) || !Directory.Exists(libraryPath))
        {
            await MoveToUnmatchedAsync(filePath, ct);
            return;
        }

        try
        {
            var folder  = SanitizeFolderName(identified.CanonicalTitle);
            var destDir = Path.Combine(libraryPath, "_Standalone", folder);
            Directory.CreateDirectory(destDir);

            var destPath = Path.Combine(destDir, Path.GetFileName(filePath));
            if (File.Exists(destPath) && destPath != filePath)
            {
                _logger.LogInformation(
                    "[Pipeline] Standalone: '{File}' already present, leaving source", filePath);
                return;
            }

            if (filePath != destPath) File.Move(filePath, destPath);

            _logger.LogInformation(
                "[Pipeline] Identified but not in library: '{File}' → _Standalone/{Folder}",
                Path.GetFileName(filePath), folder);

            await _mediator.Publish(new NewFileArrivedEvent(
                SeriesId: 0,
                MalId: identified.MalId,
                SeriesTitle: identified.CanonicalTitle,
                EpisodeNumber: episodeNum ?? 0,
                FinalFilePath: destPath,
                WasLastEpisodeUpdated: false), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Pipeline] Standalone move failed, falling back to _Unmatched");
            await MoveToUnmatchedAsync(filePath, ct);
        }
    }

    private async Task MoveToUnmatchedAsync(string filePath, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var libraryPath = await GetConfigValueAsync(db, "LibraryPath", ct);
            if (string.IsNullOrEmpty(libraryPath)) return;

            var unmatchedDir = Path.Combine(libraryPath, "_Unmatched");
            Directory.CreateDirectory(unmatchedDir);
            var dest = Path.Combine(unmatchedDir, Path.GetFileName(filePath));
            File.Move(filePath, dest, overwrite: true);

            var fileName = Path.GetFileName(filePath);
            long fileSize = 0;
            try { fileSize = new FileInfo(dest).Length; } catch { }

            // Persist to DB for the resolver UI — skip if already recorded
            var alreadyTracked = await db.UnmatchedFiles
                .AnyAsync(u => u.FilePath == dest, ct);
            if (!alreadyTracked)
            {
                db.UnmatchedFiles.Add(new Models.UnmatchedFile
                {
                    FileName      = fileName,
                    FilePath      = dest,
                    FileSizeBytes = fileSize,
                    ArrivedAt     = DateTime.UtcNow,
                    IsResolved    = false
                });
                await db.SaveChangesAsync(ct);
            }

            _logger.LogInformation("[Pipeline] Moved unmatched file to _Unmatched: {File}", fileName);
            await _mediator.Publish(new Events.UnmatchedFileEvent(fileName, unmatchedDir), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Pipeline] Failed to move to _Unmatched: {Path}", filePath);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static bool IsTorrentInFlight(DownloadJob job) =>
        job.Backend is DownloadBackend.MonoTorrent or DownloadBackend.QBittorrent &&
        job.Status is JobStatus.Downloading or JobStatus.Pending;

    private static Task<bool> IsOwnedByUnfinishedTorrentAsync(AppDbContext db, string fileName, CancellationToken ct) =>
        db.DownloadJobs.AnyAsync(j =>
            j.ExpectedFileName == fileName &&
            (j.Backend == DownloadBackend.MonoTorrent || j.Backend == DownloadBackend.QBittorrent) &&
            (j.Status == JobStatus.Downloading || j.Status == JobStatus.Pending), ct);

    private enum Collision { KeepBoth, ReplaceDamaged, SameRelease }

    /// <summary>
    /// A file with this name is already in the library. If that copy is damaged (an earlier
    /// half-finished download), it goes to the Recycle Bin and the new one takes its place.
    /// If it is intact and the same size, it is the same release: the new copy is recycled
    /// instead of being filed as "_dup". Anything else keeps both.
    /// </summary>
    private Collision ResolveCollision(string incoming, string existing)
    {
        try
        {
            if (VideoIntegrity.LooksIncomplete(existing))
            {
                if (!RecycleBin.Send(existing)) return Collision.KeepBoth;
                _logger.LogInformation("[Pipeline] Replaced damaged library copy '{File}'", Path.GetFileName(existing));
                return Collision.ReplaceDamaged;
            }

            if (new FileInfo(existing).Length == new FileInfo(incoming).Length && RecycleBin.Send(incoming))
            {
                _logger.LogInformation("[Pipeline] '{File}' is already in the library — dropped the duplicate",
                    Path.GetFileName(incoming));
                return Collision.SameRelease;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Pipeline] Couldn't compare '{File}' with the library copy", Path.GetFileName(incoming));
        }
        return Collision.KeepBoth;
    }

    private static bool IsFileComplete(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;   // exclusive open succeeded → no writer holds the file
        }
        catch (IOException)
        {
            return false;  // file is still locked by FDM or another writer
        }
    }

    // Fansub/scene release naming: "[Group] Title - 04 (1080p) [hash].mkv". The leading
    // [Group] bracket (or a resolution + a bracketed hash) is the strongest cheap signal
    // that a file is an anime release rather than an arbitrary video the user downloaded.
    private static readonly System.Text.RegularExpressions.Regex GroupBracketAtStart =
        new(@"^\s*\[[^\]]+\]", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex ResolutionTag =
        new(@"\b(480p|720p|1080p|2160p|1920x1080|1280x720)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex CrcHashTag =
        new(@"\[[0-9A-Fa-f]{8}\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool LooksLikeAnimeRelease(string fileName)
    {
        if (GroupBracketAtStart.IsMatch(fileName)) return true;            // [Group] ...
        if (CrcHashTag.IsMatch(fileName)) return true;                      // ...[A1B2C3D4]
        // A resolution tag alone is weak; pair it with a bracketed token to be safe.
        return ResolutionTag.IsMatch(fileName) && fileName.Contains('[');
    }

    private static IEnumerable<string> GetAllTitles(Series series)
    {
        var titles = new List<string> { series.Title };
        if (!string.IsNullOrEmpty(series.OriginalTitle)) titles.Add(series.OriginalTitle);
        if (!string.IsNullOrEmpty(series.AlternativeTitlesJson))
        {
            var alts = System.Text.Json.JsonSerializer
                .Deserialize<List<string>>(series.AlternativeTitlesJson);
            if (alts != null) titles.AddRange(alts);
        }
        return titles;
    }

    private static async Task<Library.NamingTemplate> LoadNamingAsync(AppDbContext db, CancellationToken ct) =>
        Library.NamingTemplate.FromConfig(
            await GetConfigValueAsync(db, Library.NamingTemplate.PresetKey, ct),
            await GetConfigValueAsync(db, Library.NamingTemplate.TemplateKey, ct));

    /// <summary>
    /// Every tracked series — the filing rules need the siblings sharing a show folder. Fills in a
    /// missing year and type from the offline anime database first, so the folder gets its year.
    /// </summary>
    private async Task<List<Series>> ShowSeasonsAsync(AppDbContext db, CancellationToken ct)
    {
        var all = await db.Series.ToListAsync(ct);
        if (_titleResolver.IsReady && Library.LibraryMetadata.Backfill(all, _titleResolver) > 0)
            await db.SaveChangesAsync(ct);
        return all;
    }

    private static string SanitizeFolderName(string name) =>
        InvalidFolderCharsPattern.Replace(name, string.Empty).Trim();

    private static async Task<string> GetConfigValueAsync(
        AppDbContext db, string key, CancellationToken ct)
    {
        var entry = await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == key, ct);
        return entry?.Value ?? string.Empty;
    }
}
