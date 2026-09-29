using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MonoTorrent;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services.Backends;

namespace Sentrychan.Core.Services;

/// <summary>
/// Repairs a damaged library episode from the torrent it came from, downloading only the
/// pieces it is missing.
///
/// The damaged file goes back to the download folder under its original name, and the torrent
/// is re-added there with a forced hash check: the engine keeps every piece that verifies and
/// fetches the rest. When it finishes, the ordinary completion path files it back into the
/// library. Nothing about the repair is special after the hand-off, so a restart mid-repair
/// resumes like any other download.
/// </summary>
public class EpisodeRepairService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly MonoTorrentBackend _torrents;
    private readonly IConfigService _config;
    private readonly ILogger<EpisodeRepairService> _logger;

    public EpisodeRepairService(
        IDbContextFactory<AppDbContext> dbFactory,
        MonoTorrentBackend torrents,
        IConfigService config,
        ILogger<EpisodeRepairService> logger)
    {
        _dbFactory = dbFactory;
        _torrents  = torrents;
        _config    = config;
        _logger    = logger;
    }

    public async Task<bool> RepairAsync(
        string damagedPath, string torrentPath, int seriesId, int episodeNumber, CancellationToken ct = default)
    {
        var downloadPath = await _config.GetValueAsync("DownloadPath", string.Empty, ct);
        if (string.IsNullOrWhiteSpace(downloadPath) || !File.Exists(damagedPath) || !File.Exists(torrentPath))
            return false;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // The torrent knows the file by the name it downloaded as. A library file renamed by the
        // naming template or Tidy library has to go back under that name, or the hash check
        // finds nothing and starts the whole episode over.
        var fileName = await Library.TidyRecords.OriginalNameAsync(db, damagedPath, ct);
        var staged   = Path.Combine(downloadPath, fileName);
        if (File.Exists(staged))
        {
            _logger.LogWarning("[Repair] '{File}' is already in the download folder — not overwriting it", fileName);
            return false;
        }

        var torrent = await Torrent.LoadAsync(torrentPath);
        var hash = torrent.InfoHashes.V1?.ToHex() ?? torrent.InfoHashes.V2?.ToHex();

        // The job goes in BEFORE the file moves: the folder watcher sees the file arrive, and
        // this row is what tells it an unfinished torrent owns that name.
        var job = new DownloadJob
        {
            SeriesId         = seriesId > 0 ? seriesId : null,
            EpisodeNumber    = episodeNumber,
            DownloadLink     = torrentPath,
            RssTitle         = fileName,
            ExpectedFileName = fileName,
            Backend          = DownloadBackend.MonoTorrent,
            TorrentHash      = hash,
            Status           = JobStatus.Downloading,
            CreatedAt        = DateTime.UtcNow,
        };
        db.DownloadJobs.Add(job);
        await db.SaveChangesAsync(ct);

        // The earlier rows for this torrent say "Completed", but that download never finished —
        // it's what is being repaired. Correct the history so it doesn't read as a second copy.
        await db.DownloadJobs
            .Where(j => j.TorrentHash == hash && j.Id != job.Id && j.Status == JobStatus.Completed)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Failed), ct);

        try
        {
            // Usually a cross-drive copy of a gigabyte or more — keep it off the caller's thread.
            await Task.Run(() => File.Move(damagedPath, staged), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Repair] Couldn't stage '{File}' for repair", fileName);
            job.Status = JobStatus.Failed;
            await db.SaveChangesAsync(CancellationToken.None);
            return false;
        }

        var handle = await _torrents.AddTorrentFileAsync(torrentPath, downloadPath, fileName, forceRecheck: true, ct);
        if (handle == null)
        {
            // Put it back where the user expects it rather than stranding it in Downloads.
            try { File.Move(staged, damagedPath); } catch { /* left in Downloads; still recoverable */ }
            job.Status = JobStatus.Failed;
            await db.SaveChangesAsync(CancellationToken.None);
            return false;
        }

        // The file has left the library; when the repair completes it's filed (and renamed) afresh.
        await db.LibraryFileOrigins.Where(o => o.Path == damagedPath).ExecuteDeleteAsync(CancellationToken.None);

        _logger.LogInformation("[Repair] Repairing '{File}' in place (hash {Hash})", fileName, handle[..Math.Min(12, handle.Length)]);
        return true;
    }
}
