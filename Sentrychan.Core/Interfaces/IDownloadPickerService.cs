using Sentrychan.Core.Models;
using System.Threading;
using System.Threading.Tasks;

namespace Sentrychan.Core.Interfaces;

/// <summary>
/// The context from which a download is being initiated.
/// Used to remember separate backend choices per context.
/// </summary>
public enum DownloadContext
{
    Hub,        // Download Hub search results
    RssFeed,    // RSS auto-download
    FillGaps    // Fill-gaps dialog
}

public interface IDownloadPickerService
{
    /// <summary>
    /// Resolves which backend to use for the given context.
    /// If the user has a remembered choice, returns it immediately.
    /// Otherwise shows the picker dialog and returns the user's selection.
    /// Returns null if the user cancelled.
    /// </summary>
    Task<IDownloadBackend?> PickBackendAsync(
        DownloadContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Clears the remembered choice for the given context.
    /// Used by the "Change download method" link in the UI.
    /// </summary>
    Task ClearRememberedChoiceAsync(
        DownloadContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the remembered backend type string for a context, or null.
    /// </summary>
    Task<string?> GetRememberedChoiceAsync(
        DownloadContext context,
        CancellationToken ct = default);
}
