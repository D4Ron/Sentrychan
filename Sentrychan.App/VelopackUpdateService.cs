using Microsoft.Extensions.Logging;
using Sentrychan.UI.Interfaces;
using Velopack;
using Velopack.Sources;

namespace Sentrychan.App;

/// <summary>
/// Velopack-backed updater pointed at the GitHub Releases feed. Only does anything for
/// an installed build — a dev/portable run reports IsSupported=false so the UI can say
/// "updates only apply to the installed version" instead of erroring.
/// </summary>
public class VelopackUpdateService : IUpdateService
{
    private const string RepoUrl = "https://github.com/D4Ron/SentryDotNet";

    private readonly ILogger<VelopackUpdateService> _logger;
    private readonly UpdateManager _manager;
    private UpdateInfo? _pending;

    public VelopackUpdateService(ILogger<VelopackUpdateService> logger)
    {
        _logger = logger;
        _manager = new UpdateManager(new GithubSource(RepoUrl, null, prerelease: false));
    }

    public bool IsSupported => _manager.IsInstalled;

    public async Task<string?> CheckForUpdateAsync()
    {
        if (!_manager.IsInstalled) return null;
        try
        {
            _pending = await _manager.CheckForUpdatesAsync();
            return _pending?.TargetFullRelease?.Version?.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Update] Check failed");
            return null;
        }
    }

    public async Task<bool> DownloadAndRestartAsync()
    {
        if (_pending == null) return false;
        try
        {
            await _manager.DownloadUpdatesAsync(_pending);
            _manager.ApplyUpdatesAndRestart(_pending); // exits the process
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Update] Download/apply failed");
            return false;
        }
    }
}
