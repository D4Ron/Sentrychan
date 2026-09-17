using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.Core.Services;

/// <summary>
/// Watches the configured download (staging) folder for newly arrived video files
/// and hands each one to the <see cref="IFileMovementPipeline"/> — the single place
/// that matches, names, and moves files into the library. This covers downloads that
/// don't raise a backend completion event (System Default / FDM hand-off, or files
/// dropped in manually).
/// </summary>
public class DownloadFolderWatcher : BackgroundService, IDownloadFolderWatcher
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IFileMovementPipeline _pipeline;
    private readonly IActiveTorrentFiles _activeFiles;
    private readonly ILogger<DownloadFolderWatcher> _logger;

    private FileSystemWatcher? _watcher;
    private string _downloadPath = string.Empty;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".webm", ".m4v", ".mov", ".flv", ".wmv"
    };

    // Debounce: a single download fires many FS events; track recently handled paths.
    private readonly Dictionary<string, DateTime> _recentlyHandled = new();
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(10);

    public bool IsWatching => _watcher != null && _watcher.EnableRaisingEvents;

    public DownloadFolderWatcher(
        IDbContextFactory<AppDbContext> dbFactory,
        IFileMovementPipeline pipeline,
        IActiveTorrentFiles activeFiles,
        ILogger<DownloadFolderWatcher> logger)
    {
        _dbFactory = dbFactory;
        _pipeline = pipeline;
        _activeFiles = activeFiles;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Small delay to let the app fully initialize
        await Task.Delay(3000, ct);
        await StartAsync(ct);

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await StopAsync();
        }
    }

    public new async Task StartAsync(CancellationToken ct = default)
    {
        await RefreshConfigAsync(ct);

        if (string.IsNullOrEmpty(_downloadPath) || !Directory.Exists(_downloadPath))
        {
            _logger.LogInformation(
                "[DownloadWatcher] Download path not configured or doesn't exist: '{Path}'", _downloadPath);
            return;
        }

        DisposeWatcher();

        _watcher = new FileSystemWatcher(_downloadPath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            IncludeSubdirectories = true,   // backends/torrents often create a per-release subfolder
            EnableRaisingEvents = true
        };

        _watcher.Created += OnFileDetected;
        _watcher.Renamed += (s, e) => OnFileDetected(s, e);

        _logger.LogInformation("[DownloadWatcher] Watching folder (recursive): {Path}", _downloadPath);

        // Catch anything that arrived while the app was closed.
        await ScanExistingFilesAsync(ct);
    }

    public Task StopAsync()
    {
        DisposeWatcher();
        _logger.LogInformation("[DownloadWatcher] Stopped watching");
        return Task.CompletedTask;
    }

    private void DisposeWatcher()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnFileDetected;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    private async void OnFileDetected(object sender, FileSystemEventArgs e)
    {
        try
        {
            var ext = Path.GetExtension(e.FullPath);
            if (!VideoExtensions.Contains(ext)) return;

            // Owned by an in-flight torrent — the backend raises a completion event
            // when it's genuinely done. Touching it here only fights for a lock we
            // can't win, and burns the pipeline's retry budget.
            if (_activeFiles.IsActive(e.FullPath)) return;

            // Torrent engine scratch space, never a finished release.
            if (IsInternalPath(e.FullPath)) return;

            lock (_recentlyHandled)
            {
                if (_recentlyHandled.TryGetValue(e.FullPath, out var lastTime) &&
                    DateTime.UtcNow - lastTime < DebounceWindow)
                    return;
                _recentlyHandled[e.FullPath] = DateTime.UtcNow;
            }

            // Let the file settle; the pipeline also performs an exclusive-lock
            // completeness check and retries while a writer still holds the file.
            await Task.Delay(2000);

            if (!File.Exists(e.FullPath)) return;

            await _pipeline.OnFileSystemEventAsync(e.FullPath, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DownloadWatcher] Error processing file: {Path}", e.FullPath);
        }
    }

    private async Task ScanExistingFilesAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_downloadPath) || !Directory.Exists(_downloadPath)) return;

        try
        {
            var videoFiles = Directory
                .EnumerateFiles(_downloadPath, "*", SearchOption.AllDirectories)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f)))
                .Where(f => !IsInternalPath(f))
                .Where(f => !_activeFiles.IsActive(f))
                .ToList();

            if (videoFiles.Count == 0) return;

            _logger.LogInformation(
                "[DownloadWatcher] Scanning {Count} existing files in download folder", videoFiles.Count);

            foreach (var file in videoFiles)
                await _pipeline.OnFileSystemEventAsync(file, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DownloadWatcher] Error scanning existing files");
        }
    }

    /// <summary>
    /// Skips the torrent engine's own working directories (e.g. <c>.mt_cache</c>)
    /// and any other dot-folder — these hold partial data, never a finished release.
    /// </summary>
    private static bool IsInternalPath(string path)
    {
        var dir = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(dir))
        {
            var name = Path.GetFileName(dir);
            if (!string.IsNullOrEmpty(name) && name.StartsWith('.')) return true;
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }

    private async Task RefreshConfigAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var dlEntry = await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == "DownloadPath", ct);
            _downloadPath = dlEntry?.Value ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DownloadWatcher] Failed to read config");
        }
    }
}
