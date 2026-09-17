using Sentrychan.Core.Models;

namespace Sentrychan.Core.Interfaces;

/// <summary>
/// Represents the live status of a single download as reported by the backend.
/// This is the polling result — it maps to transient fields on DownloadJob.
/// </summary>
public class BackendStatus
{
    public string Handle { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double ProgressPercent { get; set; }   // 0.0 – 100.0
    public long SpeedBps { get; set; }
    public int EtaSeconds { get; set; }
    public BackendState State { get; set; }
    public string? FilePath { get; set; }          // populated on completion
    public long? TotalSizeBytes { get; set; }
}

public enum BackendState
{
    Downloading,
    Paused,
    Seeding,       // completed + uploading (qBit term: "uploading")
    Completed,     // seeding ended or user stopped seed
    Error,
    Checking,      // hash checking / metadata fetching
    Queued
}

/// <summary>
/// Fired by a backend when a torrent transitions to Seeding/Completed state.
/// </summary>
public class BackendCompletionEvent
{
    public string Handle { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string OriginalTitle { get; set; } = string.Empty;
    public string Backend { get; set; } = string.Empty;
}

/// <summary>
/// Fired when a torrent has made no forward progress for a sustained period.
/// A stalled torrent never reaches Seeding, so it would otherwise never raise a
/// completion event — the download would silently sit unfinished with nothing
/// shown to the user. This surfaces it instead.
/// </summary>
public class BackendStallEvent
{
    public string Handle { get; set; } = string.Empty;
    public string OriginalTitle { get; set; } = string.Empty;

    /// <summary>How far it got, 0-100.</summary>
    public double ProgressPercent { get; set; }

    /// <summary>Peers the swarm is offering — 0 strongly implies a connectivity problem.</summary>
    public int PeersAvailable { get; set; }

    public TimeSpan StalledFor { get; set; }
    public string Backend { get; set; } = string.Empty;
}

/// <summary>
/// Unified interface for all download backends (FDM, qBittorrent, MonoTorrent).
/// Each backend implements this; the router selects the active one.
/// </summary>
public interface IDownloadBackend
{
    /// <summary>Human-readable name. E.g. "qBittorrent", "FDM"</summary>
    string Name { get; }

    string BackendType { get; }

    /// <summary>
    /// Check if this backend is reachable and configured.
    /// Called before displaying the backend in Settings and before adding downloads.
    /// Should return false (not throw) if the backend is unreachable.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Add a download. Returns a handle (torrent hash for qBit, job ID for FDM).
    /// The handle is stored in DownloadJob.TorrentHash for future polling/cancellation.
    /// </summary>
    Task<string?> AddAsync(
        string magnetOrUrl,
        string savePath,
        string? expectedFileName,
        CancellationToken ct = default);

    /// <summary>
    /// Poll the current status of a download by its handle.
    /// Returns null if the handle is not found (e.g. qBittorrent was restarted).
    /// Must not throw — return null on any error.
    /// </summary>
    Task<BackendStatus?> GetStatusAsync(string handle, CancellationToken ct = default);

    /// <summary>Get all active/recent downloads. Used to populate the progress panel.</summary>
    Task<List<BackendStatus>> GetAllStatusAsync(CancellationToken ct = default);

    Task PauseAsync(string handle, CancellationToken ct = default);
    Task ResumeAsync(string handle, CancellationToken ct = default);
    Task RemoveAsync(string handle, bool deleteData, CancellationToken ct = default);

    /// <summary>
    /// Fired when a download transitions to Seeding or Completed state.
    /// FDM backend does NOT fire this — completion is detected via filesystem.
    /// qBittorrent and MonoTorrent backends MUST fire this.
    /// </summary>
    event Action<BackendCompletionEvent>? DownloadCompleted;
}
