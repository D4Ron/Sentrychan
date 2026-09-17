namespace Sentrychan.Core.Interfaces;

public interface IFileMovementPipeline
{
    /// <summary>
    /// Entry point for qBittorrent / MonoTorrent completions.
    /// The exact file path is known — no filename parsing required.
    /// </summary>
    Task OnBackendCompletedAsync(
        string torrentHash,
        string filePath,
        string originalTitle,
        CancellationToken ct = default);

    /// <summary>
    /// Entry point for filesystem events (FDM / manual file drops).
    /// File lock check is performed here before proceeding.
    /// </summary>
    Task OnFileSystemEventAsync(string filePath, CancellationToken ct = default);
}
