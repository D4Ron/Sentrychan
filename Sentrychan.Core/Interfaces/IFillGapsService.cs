using Sentrychan.Core.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sentrychan.Core.Interfaces;

public class FillGapResult
{
    public int EpisodeNumber { get; set; }
    public ReleaseResult? BestMatch { get; set; }
    public bool IsSelected { get; set; }

    /// <summary>
    /// The episode IS in the library, but this copy is incomplete (a download that was filed
    /// before it finished). Null for a plain gap.
    /// </summary>
    public string? DamagedPath { get; set; }

    /// <summary>
    /// The torrent the damaged copy came from is still cached, so it can be repaired in
    /// place: only its missing pieces are downloaded.
    /// </summary>
    public string? RepairTorrentPath { get; set; }

    public bool CanDownload => BestMatch != null || RepairTorrentPath != null;
}

public interface IFillGapsService
{
    Task<List<FillGapResult>> FindMissingEpisodesAsync(Series series, CancellationToken ct = default);
    Task<ReleaseResult?> SearchBatchTorrentAsync(Series series, string quality = "1080p", CancellationToken ct = default);
}
