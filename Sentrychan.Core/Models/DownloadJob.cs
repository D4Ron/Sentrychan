namespace Sentrychan.Core.Models;

public enum JobStatus { Pending, Downloading, Completed, Failed }
public enum DownloadBackend { SystemDefault, FDM, QBittorrent, MonoTorrent }

public class DownloadJob
{
    public int Id { get; set; }

    /// <summary>Null for standalone downloads (series not in the library).</summary>
    public int? SeriesId { get; set; }
    public int EpisodeNumber { get; set; }
    public string DownloadLink { get; set; } = string.Empty;
    public string RssTitle { get; set; } = string.Empty;
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? TorrentHash { get; set; }
    public string? ExpectedFileName { get; set; }
    public string? FinalFilePath { get; set; }
    public DownloadBackend Backend { get; set; } = DownloadBackend.SystemDefault;

    // Navigation — null for standalone downloads
    public Series? Series { get; set; }
}
