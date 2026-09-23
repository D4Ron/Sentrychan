namespace Sentrychan.Core.Models;

/// <summary>
/// One release returned by a search provider. Core defines the shape; filling it in is the
/// job of whichever <see cref="Interfaces.IReleaseProvider"/> plug-in produced it.
/// </summary>
public class ReleaseResult
{
    /// <summary>Raw release title as published.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Magnet link, when the provider supplies one.</summary>
    public string MagnetLink { get; set; } = string.Empty;

    /// <summary>Direct .torrent file URL, when the provider supplies one.</summary>
    public string TorrentUrl { get; set; } = string.Empty;

    /// <summary>
    /// Torrent info hash. Used for qBittorrent reconciliation — stored in
    /// DownloadJob.TorrentHash at enqueue.
    /// </summary>
    public string InfoHash { get; set; } = string.Empty;

    public int Seeders { get; set; }
    public int Leechers { get; set; }

    /// <summary>Human-readable size as the provider reported it, e.g. "702 MiB".</summary>
    public string SizeDisplay { get; set; } = string.Empty;

    /// <summary>Size in bytes, parsed from SizeDisplay for sorting.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Release group parsed from a leading [Bracket] in the title, or "Unknown".</summary>
    public string ReleaseGroup { get; set; } = string.Empty;

    /// <summary>Resolution parsed from the title, e.g. "1080p"; null if unknown.</summary>
    public string? Resolution { get; set; }

    /// <summary>Episode number parsed from the title. Null for batches or when parsing fails.</summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>True for multi-episode releases; displayed with a BATCH badge.</summary>
    public bool IsBatch { get; set; }

    /// <summary>Set by the provider for releases it considers trusted. Drives a UI badge only.</summary>
    public bool IsTrusted { get; set; }

    public DateTime PublishedAt { get; set; }

    // ── Computed display helpers ──────────────────────────────────

    /// <summary>Short display label. E.g. "EP 28" or "BATCH" or "EP ?"</summary>
    public string EpisodeDisplay => IsBatch ? "BATCH"
        : EpisodeNumber.HasValue ? $"EP {EpisodeNumber}"
        : "EP ?";

    /// <summary>
    /// Relevance score used for auto-selection when several results cover the same
    /// episode. Higher is better. Providers set it; callers only compare it.
    /// </summary>
    public int BaseScore { get; set; }

    /// <summary>The link a download backend should be handed: the .torrent URL if known, else the magnet.</summary>
    public string DownloadLink => !string.IsNullOrEmpty(TorrentUrl) ? TorrentUrl : MagnetLink;
}
