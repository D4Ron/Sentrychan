using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;

namespace Sentrychan.Core.Services.Backends;

/// <summary>
/// "System Default" backend. Passes magnet links and torrent URLs to the OS
/// shell, which delegates to whatever the user has set as their default
/// torrent handler (FDM, qBittorrent, Transmission, µTorrent, etc.).
/// No configuration required. No progress polling — downloads are handed off
/// to the external application entirely.
/// </summary>
public class FdmBackend : IDownloadBackend
{
    private readonly ILogger<FdmBackend> _logger;

    public string Name => "System Default";
    public string BackendType => "SystemDefault";

#pragma warning disable CS0067 // Required by IDownloadBackend; FDM uses system handler, not callbacks
    public event Action<BackendCompletionEvent>? DownloadCompleted;
#pragma warning restore CS0067

    public FdmBackend(ILogger<FdmBackend> logger)
    {
        _logger = logger;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        // The OS shell is always available on Windows and macOS/Linux
        return Task.FromResult(true);
    }

    public Task<string?> AddAsync(
        string magnetOrUrl,
        string savePath,
        string? displayName,
        CancellationToken ct = default)
    {
        try
        {
            // For magnet links: open directly via OS shell
            // For .torrent URLs: this will open the URL in the browser
            // which then triggers the torrent handler if one is registered.
            // For actual .torrent file paths: Process.Start opens the file
            // with the registered handler.
            var psi = new ProcessStartInfo
            {
                FileName        = magnetOrUrl,
                UseShellExecute = true
            };
            Process.Start(psi);

            _logger.LogInformation(
                "[SystemDefault] Sent to OS handler: {Link}",
                magnetOrUrl.Length > 60
                    ? magnetOrUrl[..60] + "..."
                    : magnetOrUrl);

            // Return a pseudo-handle so callers can track this request.
            // Since we have no API to poll, the handle is just a timestamp-based ID.
            var handle = $"sysdefault_{DateTime.UtcNow.Ticks}";
            return Task.FromResult<string?>(handle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SystemDefault] Failed to open: {Link}", magnetOrUrl);
            return Task.FromResult<string?>(null);
        }
    }

    // ── No-op implementations (no API to control the external handler) ──

    public Task PauseAsync(string handle, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task ResumeAsync(string handle, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task RemoveAsync(
        string handle, bool deleteData, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<List<BackendStatus>> GetAllStatusAsync(
        CancellationToken ct = default)
        => Task.FromResult(new List<BackendStatus>());

    public Task<BackendStatus?> GetStatusAsync(
        string handle, CancellationToken ct = default)
        => Task.FromResult<BackendStatus?>(null);
}
