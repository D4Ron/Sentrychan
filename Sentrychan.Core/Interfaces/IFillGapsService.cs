using Sentrychan.Core.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sentrychan.Core.Interfaces;

public class FillGapResult
{
    public int EpisodeNumber { get; set; }
    public NyaaResult? BestMatch { get; set; }
    public bool IsSelected { get; set; }
}

public interface IFillGapsService
{
    Task<List<FillGapResult>> FindMissingEpisodesAsync(Series series, CancellationToken ct = default);
    Task<NyaaResult?> SearchBatchTorrentAsync(Series series, string quality = "1080p", CancellationToken ct = default);
}
