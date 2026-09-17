using Sentrychan.Core.Models;

namespace Sentrychan.Core.Interfaces;

/// <summary>
/// Selects and provides the currently active download backend.
/// Registered as a singleton. The active backend is determined by
/// the "SelectedDownloadBackend" AppConfig key.
/// </summary>
public interface IDownloadBackendRouter
{
    IDownloadBackend Active { get; }
    string ActiveBackendType { get; }
    IReadOnlyList<IDownloadBackend> All { get; }
    Task SetActiveAsync(string backend, CancellationToken ct = default);
    Task InitializeAsync(CancellationToken ct = default);
}
