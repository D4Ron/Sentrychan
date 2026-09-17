namespace Sentrychan.UI.Interfaces;

/// <summary>
/// App self-update via Velopack + GitHub Releases. Implemented in the App project
/// (which references Velopack); the UI talks to it through this interface.
/// </summary>
public interface IUpdateService
{
    /// <summary>True only when running as a Velopack-installed app (not a dev/portable run).</summary>
    bool IsSupported { get; }

    /// <summary>Checks GitHub for a newer release; returns its version string, or null if up to date.</summary>
    Task<string?> CheckForUpdateAsync();

    /// <summary>Downloads the pending update and restarts into it. Does not return on success.</summary>
    Task<bool> DownloadAndRestartAsync();
}
