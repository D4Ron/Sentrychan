namespace Sentrychan.Core.Models;

/// <summary>
/// Represents a single result row from a nyaa.si RSS search.
/// Parsed from the nyaa: XML namespace elements in the RSS feed.
/// </summary>
public class NyaaResult
{
    /// <summary>Raw title from RSS. E.g. "[SubsPlease] Frieren - 28 (1080p) [ABCD1234].mkv"</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Magnet link from RSS &lt;link&gt; element.</summary>
    public string MagnetLink { get; set; } = string.Empty;

    /// <summary>Direct .torrent file URL. E.g. https://nyaa.si/download/ABCD.torrent</summary>
    public string TorrentUrl { get; set; } = string.Empty;

    /// <summary>
    /// Torrent info hash from nyaa:infoHash element.
    /// Used for qBittorrent reconciliation — stored in DownloadJob.TorrentHash at enqueue.
    /// </summary>
    public string InfoHash { get; set; } = string.Empty;

    public int Seeders { get; set; }
    public int Leechers { get; set; }

    /// <summary>Raw size string from nyaa:size. E.g. "702 MiB"</summary>
    public string SizeDisplay { get; set; } = string.Empty;

    /// <summary>Size in bytes, parsed from SizeDisplay for sorting.</summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Release group parsed from leading [Brackets] in the title.
    /// E.g. "SubsPlease", "Erai-raws", "Unknown" if no bracket prefix found.
    /// </summary>
    public string ReleaseGroup { get; set; } = string.Empty;

    /// <summary>Resolution parsed from title. E.g. "1080p", "720p", null if unknown.</summary>
    public string? Resolution { get; set; }

    /// <summary>
    /// Episode number parsed via IEpisodeNormalizer.ExtractEpisodeNumber.
    /// Null for batch releases or if parsing fails.
    /// </summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>
    /// True if the title contains episode range syntax (e.g. "01-28") or
    /// the word "batch" (case-insensitive). These should display with a BATCH badge.
    /// </summary>
    public bool IsBatch { get; set; }

    public DateTime PublishedAt { get; set; }

    // ── Computed display helpers ──────────────────────────────────

    /// <summary>Short display label. E.g. "EP 28" or "BATCH" or "EP ?"</summary>
    public string EpisodeDisplay => IsBatch ? "BATCH"
        : EpisodeNumber.HasValue ? $"EP {EpisodeNumber}"
        : "EP ?";

    /// <summary>
    /// A relevance score used for auto-selection when multiple results exist
    /// for the same episode. Higher is better.
    /// PreferredGroup adds 1000, QualityMatch adds 500, Seeders add directly.
    /// Callers should add the PreferredGroup bonus externally.
    /// </summary>
    public int BaseScore { get; set; }
}
