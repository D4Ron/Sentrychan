using Microsoft.Extensions.Logging;
using Sentrychan.Core;
using Sentrychan.UI.Interfaces;
using Velopack;
using Velopack.Sources;

namespace Sentrychan.App;

/// <summary>
/// Velopack-backed updater pointed at the GitHub Releases feed. Only does anything for
/// an installed build — a dev/portable run reports IsSupported=false so the UI can say
/// "updates only apply to the installed version" instead of erroring.
///
/// WARNING: this URL is compiled into every shipped build, so an installed client can only
/// ever look where the binary it was installed from was told to look. Changing it does NOT
/// redirect clients already in the wild — they keep querying the old address forever. It
/// pointed at the pre-rename "SentryDotNet" repo, which was deleted on 2026-09-17, stranding
/// every build released before 1.0.3. Never repoint or delete a repo that shipped builds
/// still query.
/// </summary>
public class VelopackUpdateService : IUpdateService
{
    private const string RepoUrl = "https://github.com/D4Ron/Sentrychan";

    private readonly ILogger<VelopackUpdateService> _logger;
    private readonly UpdateManager _manager;
    private UpdateInfo? _pending;

    public VelopackUpdateService(ILogger<VelopackUpdateService> logger)
    {
        _logger = logger;
        _manager = CreateManager(BuildInfo.IsPreview);
    }

    /// <summary>
    /// Stable reads only full releases on the default channel — unchanged from before the
    /// preview existed. The preview reads prereleases too, pinned to the "preview" channel so it
    /// only ever installs preview packages (which carry their own app id and data folder), never
    /// a stable package that would replace it.
    /// </summary>
    private static UpdateManager CreateManager(bool isPreview) => isPreview
        ? new UpdateManager(new GithubSource(RepoUrl, null, prerelease: true),
                            new UpdateOptions { ExplicitChannel = PreviewChannelFor(AppPaths.CurrentOs) })
        : new UpdateManager(new GithubSource(RepoUrl, null, prerelease: false));

    /// <summary>The Velopack channel Windows preview packages are built with (<c>vpk pack --channel preview</c>).</summary>
    public const string PreviewChannel = "preview";

    /// <summary>
    /// A channel is one release feed, so each system's preview needs its own: Velopack's default
    /// channels are already per system (win, osx, linux), and an explicit one isn't. Windows keeps
    /// "preview", as documented for its packaging; macOS and Linux packs use these.
    /// </summary>
    public static string PreviewChannelFor(AppPaths.Os os) => os switch
    {
        AppPaths.Os.MacOS => PreviewChannel + "-osx",
        AppPaths.Os.Linux => PreviewChannel + "-linux",
        _ => PreviewChannel,
    };

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
