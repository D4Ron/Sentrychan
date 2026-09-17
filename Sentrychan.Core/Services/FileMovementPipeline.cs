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

    public FileMovementPipeline(
        IDbContextFactory<AppDbContext> dbFactory,
        IEpisodeNormalizer normalizer,
        ITitleResolverService titleResolver,
        IMediator mediator,
        ILogger<FileMovementPipeline> logger)
    {
        _dbFactory     = dbFactory;
        _normalizer    = normalizer;
        _titleResolver = titleResolver;
        _mediator      = mediator;
        _logger        = logger;
    }

    // ── Entry point 1: Backend completion (qBit / MonoTorrent) ───

    public async Task OnBackendCompletedAsync(
        string torrentHash,
        string filePath,
        string originalTitle,
        CancellationToken ct = default)
    {
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

        // Look up the DownloadJob by torrent hash — exact match, no parsing needed
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var job = await db.DownloadJobs
            .Include(j => j.Series)
            .FirstOrDefaultAsync(j =>
                j.TorrentHash == torrentHash &&
                j.Status != JobStatus.Completed, ct);

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
    /// "[SubsPlease] Mushoku Tensei - 05 (1080p) [ABC].mkv" → "Mushoku Tensei".
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

        var baseTitle    = SeasonDetector.ExtractBaseTitle(series.Title);
        var seriesFolder = SanitizeFolderName(baseTitle);

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
            int seasonNumber = series.SeasonNumber;
            var detected = SeasonDetector.DetectSeason(fileName);
            if (detected > 1 || SeasonDetector.HasSeasonIndicator(fileName))
                seasonNumber = detected;

            var destDir = Path.Combine(libraryPath, seriesFolder, $"Season {seasonNumber}");
            Directory.CreateDirectory(destDir);
            var destPath = Path.Combine(destDir, Path.GetFileName(file));

            if (File.Exists(destPath) && destPath != file)
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
        job.FinalFilePath = Path.Combine(libraryPath, seriesFolder);

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
        _logger.LogInformation("[Pipeline] Filesystem event, file complete: {Path}", filePath);

        var fileName = Path.GetFileNameWithoutExtension(filePath);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // ── Match strategy 1: exact ExpectedFileName lookup ───────
        var exactName = Path.GetFileName(filePath);
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

        // Resolve the file name to a canonical MAL id (offline synonym DB) and
        // match by id — far more accurate than string fuzzy-matching.
        if (_titleResolver.IsReady)
        {
            var resolved = _titleResolver.ResolveRelease(fileName);
            if (resolved is { MalId: > 0 })
                matchedSeries = allSeries.FirstOrDefault(s => s.MalId == resolved.MalId);
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

        // Use the base title (stripped of season qualifiers) for the folder name
        // so "Oshi no Ko 2nd Season" → Oshi no Ko/Season 2 (Plex/Jellyfin compatible)
        var baseTitle    = SeasonDetector.ExtractBaseTitle(series.Title);
        var seriesFolder = SanitizeFolderName(baseTitle);

        // Prefer season detected from RSS title (most accurate for what was actually downloaded)
        // Fall back to the DB-stored SeasonNumber set by user or auto-detection on add
        int seasonNumber = series.SeasonNumber;
        if (!string.IsNullOrEmpty(job.RssTitle))
        {
            var rssDetected = SeasonDetector.DetectSeason(job.RssTitle);
            if (rssDetected > 1 || SeasonDetector.HasSeasonIndicator(job.RssTitle))
                seasonNumber = rssDetected;
        }

        var seasonFolder = $"Season {seasonNumber}";
        var destDir      = Path.Combine(libraryPath, seriesFolder, seasonFolder);
        Directory.CreateDirectory(destDir);

        var destPath = Path.Combine(destDir, Path.GetFileName(sourcePath));

        // Avoid overwriting an existing file
        if (File.Exists(destPath) && destPath != sourcePath)
        {
            var name     = Path.GetFileNameWithoutExtension(sourcePath);
            var ext2     = Path.GetExtension(sourcePath);
            destPath     = Path.Combine(destDir, $"{name}_dup{DateTime.Now.Ticks}{ext2}");
        }

        try
        {
            if (sourcePath != destPath)
            {
                File.Move(sourcePath, destPath);
                _logger.LogInformation(
                    "[Pipeline] Moved '{File}' → '{Dest}'",
                    Path.GetFileName(sourcePath), destPath);
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
        if (GroupBracketAtStart.IsMatch(fileName)) return true;            // [SubsPlease] ...
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

    private static string SanitizeFolderName(string name) =>
        InvalidFolderCharsPattern.Replace(name, string.Empty).Trim();

    private static async Task<string> GetConfigValueAsync(
        AppDbContext db, string key, CancellationToken ct)
    {
        var entry = await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == key, ct);
        return entry?.Value ?? string.Empty;
    }
}
