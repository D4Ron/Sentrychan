namespace Sentrychan.Core.Interfaces;

/// <summary>
/// Tracks the file paths a torrent backend is actively writing to.
///
/// Why this exists: the download folder watcher and the torrent engine point at the
/// SAME directory. Without this, the watcher sees a half-written .mkv the moment the
/// torrent creates it, tries to take an exclusive lock to move it, fails for the whole
/// duration of the download, and then permanently gives up — so the file never got
/// organised even after the torrent finished. Torrent-owned files are the backend's
/// responsibility; it raises a completion event when they're genuinely done.
/// </summary>
public interface IActiveTorrentFiles
{
    /// <summary>Marks a path as owned by an in-flight torrent.</summary>
    void Register(string path);

    /// <summary>Releases ownership — the backend is done with this path.</summary>
    void Unregister(string path);

    /// <summary>True while a torrent is still writing to this path.</summary>
    bool IsActive(string path);
}
