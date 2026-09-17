using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MonoTorrent;
using MonoTorrent.Client;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.Services.Backends;

/// <summary>
/// Built-in torrent backend powered by the MonoTorrent library.
/// No external application required — downloads run in-process.
/// Supports both magnet links and .torrent file URLs.
/// </summary>
public class MonoTorrentBackend : IDownloadBackend, IAsyncDisposable
{
    private readonly ILogger<MonoTorrentBackend> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IActiveTorrentFiles _activeFiles;
    private readonly HttpClient _httpClient;

    // Engine is created lazily; semaphore prevents double-creation under concurrency
    private ClientEngine? _engine;
    private readonly SemaphoreSlim _engineLock = new(1, 1);

    // Poll loop management
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private readonly object _pollLock = new();

    // Active managers: hash (lowercase hex) → manager
    // ConcurrentDictionary allows safe reads from poll loop while AddAsync writes
    private readonly ConcurrentDictionary<string, TorrentManager> _managers =
        new(StringComparer.OrdinalIgnoreCase);

    // Managers that completed seeding — held until the engine removes them
    private readonly ConcurrentBag<TorrentManager> _completedManagers = new();

    // ── Networking ────────────────────────────────────────────────
    // Fixed ports so the UPnP mapping survives restarts and can be opened
    // manually in the firewall if the router has UPnP turned off.
    private const int DefaultListenPort = 55123;
    private const int DefaultDhtPort    = 55124;

    // ── Stall detection ───────────────────────────────────────────
    // A torrent that makes no progress for this long is reported as stalled
    // instead of silently sitting in the download folder forever.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Per-handle progress watermark: last progress % and when it last moved.</summary>
    private readonly ConcurrentDictionary<string, (double Progress, DateTime At)> _progressWatermark =
        new(StringComparer.OrdinalIgnoreCase);

    public string Name => "MonoTorrent (Built-in)";
    public string BackendType => "MonoTorrent";

    public event Action<BackendCompletionEvent>? DownloadCompleted;

    /// <summary>
    /// Raised when a torrent makes no progress for <see cref="StallTimeout"/>.
    /// Carries the handle, the human title and how far it got, so the UI can
    /// surface it rather than leaving the user guessing.
    /// </summary>
    public event Action<BackendStallEvent>? DownloadStalled;

    public MonoTorrentBackend(
        ILogger<MonoTorrentBackend> logger,
        IDbContextFactory<AppDbContext> dbFactory,
        IActiveTorrentFiles activeFiles)
    {
        _logger      = logger;
        _dbFactory   = dbFactory;
        _activeFiles = activeFiles;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.UserAgent
            .ParseAdd("Sentrychan/2.0 (MonoTorrent)");
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(true);

    public async Task<string?> AddAsync(
        string magnetOrUrl,
        string savePath,
        string? expectedFileName,
        CancellationToken ct = default)
    {
        try
        {
            await EnsureEngineAsync(savePath, ct);
            Directory.CreateDirectory(savePath);

            TorrentManager manager;

            if (MagnetLink.TryParse(magnetOrUrl, out var magnet))
            {
                // ── Magnet link path ──────────────────────────────────
                var infoHash = magnet.InfoHashes.V1?.ToHex()
                            ?? magnet.InfoHashes.V2?.ToHex();

                if (infoHash != null && _managers.ContainsKey(infoHash))
                {
                    _logger.LogInformation("[MonoTorrent] Already tracking {Hash}", infoHash);
                    return infoHash;
                }

                manager = await _engine!.AddAsync(magnet, savePath);
            }
            else if (magnetOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                // ── .torrent URL path — download bytes and load ───────
                _logger.LogInformation("[MonoTorrent] Downloading .torrent from {Url}",
                    magnetOrUrl[..Math.Min(120, magnetOrUrl.Length)]);

                HttpResponseMessage response;
                try
                {
                    response = await _httpClient.GetAsync(magnetOrUrl, ct);
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex, "[MonoTorrent] HTTP error fetching .torrent: {Url}",
                        magnetOrUrl[..Math.Min(120, magnetOrUrl.Length)]);
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError(
                        "[MonoTorrent] .torrent fetch returned {Status} for {Url}. " +
                        "If this came from a hash-based Nyaa URL, prefer magnet links.",
                        (int)response.StatusCode,
                        magnetOrUrl[..Math.Min(120, magnetOrUrl.Length)]);
                    return null;
                }

                var torrentBytes = await response.Content.ReadAsByteArrayAsync(ct);

                Torrent torrent;
                try
                {
                    torrent = await Torrent.LoadAsync(torrentBytes);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[MonoTorrent] Failed to parse .torrent bytes from {Url}",
                        magnetOrUrl[..Math.Min(120, magnetOrUrl.Length)]);
                    return null;
                }

                var infoHash = torrent.InfoHashes.V1?.ToHex()
                            ?? torrent.InfoHashes.V2?.ToHex();

                if (infoHash != null && _managers.ContainsKey(infoHash))
                {
                    _logger.LogInformation("[MonoTorrent] Already tracking {Hash}", infoHash);
                    return infoHash;
                }

                manager = await _engine!.AddAsync(torrent, savePath);
            }
            else
            {
                _logger.LogWarning("[MonoTorrent] Cannot parse as magnet or URL: {Input}",
                    magnetOrUrl[..Math.Min(80, magnetOrUrl.Length)]);
                return null;
            }

            await manager.StartAsync();

            var handle = manager.InfoHashes.V1?.ToHex()
                      ?? manager.InfoHashes.V2?.ToHex()
                      ?? Guid.NewGuid().ToString("N");

            _managers[handle] = manager;

            // Claim the output path(s) so the download-folder watcher leaves them
            // alone until we raise a completion event — otherwise it fights us for
            // an exclusive lock on a file we're still writing.
            RegisterActivePaths(manager);

            EnsurePollingStarted();

            _logger.LogInformation("[MonoTorrent] Added {Handle}: {Name}",
                handle[..Math.Min(12, handle.Length)],
                expectedFileName ?? magnetOrUrl[..Math.Min(60, magnetOrUrl.Length)]);

            return handle;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MonoTorrent] AddAsync failed for {Url}",
                magnetOrUrl[..Math.Min(80, magnetOrUrl.Length)]);
            return null;
        }
    }

    public Task<BackendStatus?> GetStatusAsync(string handle, CancellationToken ct = default)
    {
        if (_managers.TryGetValue(handle, out var manager))
            return Task.FromResult<BackendStatus?>(MapToBackendStatus(handle, manager));
        return Task.FromResult<BackendStatus?>(null);
    }

    public Task<List<BackendStatus>> GetAllStatusAsync(CancellationToken ct = default)
    {
        var result = _managers
            .Select(kv => MapToBackendStatus(kv.Key, kv.Value))
            .ToList();
        return Task.FromResult(result);
    }

    public async Task PauseAsync(string handle, CancellationToken ct = default)
    {
        if (_managers.TryGetValue(handle, out var manager))
            await manager.PauseAsync();
    }

    public async Task ResumeAsync(string handle, CancellationToken ct = default)
    {
        if (_managers.TryGetValue(handle, out var manager))
            await manager.StartAsync();
    }

    public async Task RemoveAsync(string handle, bool deleteData, CancellationToken ct = default)
    {
        if (!_managers.TryRemove(handle, out var manager)) return;

        await manager.StopAsync();
        await _engine!.RemoveAsync(manager,
            deleteData ? RemoveMode.CacheDataAndDownloadedData : RemoveMode.CacheDataOnly);
    }

    // ── Speed limits ───────────────────────────────────────────────
    // Stored in AppConfigs as KB/s; MonoTorrent wants bytes/sec, where 0 = unlimited.
    // Unlimited is the default, so an untouched install behaves exactly as before.
    public const string MaxDownloadKey = "MaxDownloadRateKbps";
    public const string MaxUploadKey   = "MaxUploadRateKbps";

    private readonly record struct RateLimits(int DownBytesPerSec = 0, int UpBytesPerSec = 0);

    private static async Task<RateLimits> ReadRateLimitsAsync(AppDbContext db, CancellationToken ct)
    {
        var down = await ReadKbpsAsync(db, MaxDownloadKey, ct);
        var up   = await ReadKbpsAsync(db, MaxUploadKey, ct);
        return new RateLimits(down, up);
    }

    private static async Task<int> ReadKbpsAsync(AppDbContext db, string key, CancellationToken ct)
    {
        var row = await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == key, ct);
        if (row == null || !int.TryParse(row.Value, out var kbps) || kbps <= 0) return 0; // unlimited
        return kbps * 1024;
    }

    private static string DescribeRate(int bytesPerSec) =>
        bytesPerSec <= 0 ? "unlimited" : $"{bytesPerSec / 1024} KB/s";

    /// <summary>
    /// Re-reads the configured speed limits and applies them to the live engine.
    /// Safe to call while downloads are running — MonoTorrent applies the new caps
    /// in place rather than restarting transfers.
    /// </summary>
    public async Task ApplyRateLimitsAsync(CancellationToken ct = default)
    {
        if (_engine == null) return;

        RateLimits limits;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            limits = await ReadRateLimitsAsync(db, ct);
        }
        catch { return; }

        var updated = new EngineSettingsBuilder(_engine.Settings)
        {
            MaximumDownloadRate = limits.DownBytesPerSec,
            MaximumUploadRate   = limits.UpBytesPerSec,
        }.ToSettings();

        await _engine.UpdateSettingsAsync(updated);
        _logger.LogInformation("[MonoTorrent] Speed limits applied — down {Down}, up {Up}",
            DescribeRate(limits.DownBytesPerSec), DescribeRate(limits.UpBytesPerSec));
    }

    // ── Private ────────────────────────────────────────────────────

    private async Task EnsureEngineAsync(string savePath, CancellationToken ct)
    {
        if (_engine != null) return;

        await _engineLock.WaitAsync(ct);
        try
        {
            if (_engine != null) return; // double-checked after acquiring lock

            // Prefer LibraryPath from DB; fall back to the download save path
            string resolvedSave = savePath;
            var limits = new RateLimits();
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var entry = await db.AppConfigs
                    .FirstOrDefaultAsync(c => c.Key == "DownloadPath", ct);
                if (!string.IsNullOrEmpty(entry?.Value))
                    resolvedSave = entry.Value;

                limits = await ReadRateLimitsAsync(db, ct);
            }
            catch { /* use savePath + unlimited */ }

            Directory.CreateDirectory(resolvedSave);

            var settings = new EngineSettingsBuilder
            {
                CacheDirectory       = Path.Combine(resolvedSave, ".mt_cache"),

                // Ask the router (UPnP / NAT-PMP) to map our listen port. Without this
                // the NAT drops every INBOUND peer connection, so we can only reach peers
                // we dial out to — which roughly quarters the usable swarm and is what
                // made downloads crawl and then stall near the end.
                AllowPortForwarding  = true,

                // Pin the listen port instead of taking a random ephemeral one, so the
                // forwarded mapping stays valid across restarts (and can be opened in the
                // firewall manually if UPnP is disabled on the router).
                ListenEndPoints = new Dictionary<string, System.Net.IPEndPoint>
                {
                    { "ipv4", new System.Net.IPEndPoint(System.Net.IPAddress.Any,     DefaultListenPort) },
                    { "ipv6", new System.Net.IPEndPoint(System.Net.IPAddress.IPv6Any, DefaultListenPort) },
                },
                DhtEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Any, DefaultDhtPort),

                // Default is 8, which throttles how quickly we can probe a swarm for
                // peers that actually answer. Raising it dramatically shortens the ramp.
                MaximumHalfOpenConnections = 32,
                MaximumConnections         = 250,

                AutoSaveLoadDhtCache    = true,
                AutoSaveLoadFastResume  = true,
                MaximumDownloadRate  = limits.DownBytesPerSec,
                MaximumUploadRate    = limits.UpBytesPerSec,
            }.ToSettings();

            _engine = new ClientEngine(settings);
            _logger.LogInformation(
                "[MonoTorrent] Engine initialized, cache: {Cache} (down {Down}, up {Up}, " +
                "port {Port}, forwarding on)",
                settings.CacheDirectory, DescribeRate(limits.DownBytesPerSec),
                DescribeRate(limits.UpBytesPerSec), DefaultListenPort);
        }
        finally
        {
            _engineLock.Release();
        }
    }

    private void EnsurePollingStarted()
    {
        lock (_pollLock)
        {
            if (_pollTask is { IsCompleted: false }) return;

            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts  = new CancellationTokenSource();
            _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token));
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        _logger.LogDebug("[MonoTorrent] Poll loop started ({Count} active)", _managers.Count);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2_000, ct);

                // Drain completed managers from the engine to free resources
                while (_completedManagers.TryTake(out var completed))
                {
                    try
                    {
                        await completed.StopAsync();
                        if (_engine != null)
                            await _engine.RemoveAsync(completed, RemoveMode.CacheDataOnly);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[MonoTorrent] Cleanup of completed manager failed");
                    }
                }

                // Snapshot to avoid modifying while iterating
                foreach (var (handle, manager) in _managers.ToArray())
                {
                    switch (manager.State)
                    {
                        case TorrentState.Seeding:
                            _progressWatermark.TryRemove(handle, out _);
                            await OnTorrentSeedingAsync(handle, manager);
                            break;

                        case TorrentState.Error:
                            _logger.LogWarning(
                                "[MonoTorrent] Torrent in error state {Handle}: {Reason}",
                                handle[..Math.Min(12, handle.Length)],
                                manager.Error?.Reason);
                            _progressWatermark.TryRemove(handle, out _);
                            _managers.TryRemove(handle, out _);
                            ReleaseActivePaths(manager);
                            _completedManagers.Add(manager);
                            break;

                        case TorrentState.Downloading or TorrentState.Metadata:
                            // Metadata for a bare magnet resolves after the add, so the
                            // first registration attempt may have had no file list yet.
                            RegisterActivePaths(manager);
                            CheckForStall(handle, manager);
                            break;
                    }
                }

                // Stop poll loop when nothing is active
                if (_managers.IsEmpty)
                {
                    _logger.LogDebug("[MonoTorrent] No active torrents, poll loop exiting");
                    return;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MonoTorrent] Poll loop error, continuing");
            }
        }

        _logger.LogDebug("[MonoTorrent] Poll loop stopped");
    }

    /// <summary>
    /// Claims every path this torrent will write, so the download-folder watcher
    /// skips them while the download is in flight.
    /// </summary>
    private void RegisterActivePaths(TorrentManager manager)
    {
        try
        {
            if (manager.Files.Count == 1)
                _activeFiles.Register(manager.Files[0].FullPath);
            else if (!string.IsNullOrEmpty(manager.ContainingDirectory))
                _activeFiles.Register(manager.ContainingDirectory);
        }
        catch (Exception ex)
        {
            // Metadata may not be resolved yet for a bare magnet — the poll loop
            // re-registers once files are known.
            _logger.LogDebug(ex, "[MonoTorrent] Could not register active paths yet");
        }
    }

    private void ReleaseActivePaths(TorrentManager manager)
    {
        try
        {
            if (manager.Files.Count == 1)
                _activeFiles.Unregister(manager.Files[0].FullPath);
            else if (!string.IsNullOrEmpty(manager.ContainingDirectory))
                _activeFiles.Unregister(manager.ContainingDirectory);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Compares the torrent's progress against the last watermark. If it hasn't
    /// moved in <see cref="StallTimeout"/>, raise <see cref="DownloadStalled"/> once
    /// so the UI can show it as stuck instead of the download silently rotting in
    /// the download folder with no completion event ever firing.
    /// </summary>
    private void CheckForStall(string handle, TorrentManager manager)
    {
        var now      = DateTime.UtcNow;
        var progress = manager.Progress;

        if (!_progressWatermark.TryGetValue(handle, out var mark))
        {
            _progressWatermark[handle] = (progress, now);
            return;
        }

        // Any forward movement resets the clock.
        if (progress > mark.Progress)
        {
            _progressWatermark[handle] = (progress, now);
            return;
        }

        if (now - mark.At < StallTimeout) return;

        var peers = 0;
        try { peers = manager.Peers.Available; } catch { /* best effort */ }

        _logger.LogWarning(
            "[MonoTorrent] STALLED {Handle} at {Progress:F1}% for {Mins:F0} min " +
            "({Peers} peers available): {Name}",
            handle[..Math.Min(12, handle.Length)], progress,
            (now - mark.At).TotalMinutes, peers, manager.Torrent?.Name);

        DownloadStalled?.Invoke(new BackendStallEvent
        {
            Handle          = handle,
            OriginalTitle   = manager.Torrent?.Name ?? handle,
            ProgressPercent = progress,
            PeersAvailable  = peers,
            StalledFor      = now - mark.At,
            Backend         = BackendType,
        });

        // Re-arm so we warn again only after another full StallTimeout, rather
        // than spamming every poll tick.
        _progressWatermark[handle] = (progress, now);
    }

    private async Task OnTorrentSeedingAsync(string handle, TorrentManager manager)
    {
        // Remove from active tracking first to prevent duplicate events
        if (!_managers.TryRemove(handle, out _)) return;

        string? filePath = manager.Files.Count == 1
            ? manager.Files[0].FullPath
            : manager.ContainingDirectory;

        // CRITICAL ordering: stop the torrent BEFORE announcing completion.
        // While seeding, MonoTorrent keeps the file open, so the pipeline's
        // exclusive-lock check would fail and it would give up on a file that
        // is actually finished — the download would silently never be filed.
        try
        {
            await manager.StopAsync();
            if (_engine != null)
                await _engine.RemoveAsync(manager, RemoveMode.CacheDataOnly);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[MonoTorrent] Stop before completion handoff failed");
        }

        // Hand the file back to the watcher/pipeline now that we've let go of it.
        ReleaseActivePaths(manager);

        _logger.LogInformation(
            "[MonoTorrent] Download complete: {Handle} → {Path}",
            handle[..Math.Min(12, handle.Length)], filePath);

        DownloadCompleted?.Invoke(new BackendCompletionEvent
        {
            Handle        = handle,
            FilePath      = filePath ?? string.Empty,
            OriginalTitle = manager.Torrent?.Name ?? handle,
            Backend       = BackendType
        });
    }

    private static BackendStatus MapToBackendStatus(string handle, TorrentManager manager)
    {
        var state = manager.State switch
        {
            TorrentState.Downloading or TorrentState.Metadata => BackendState.Downloading,
            TorrentState.Paused                               => BackendState.Paused,
            TorrentState.Seeding                              => BackendState.Seeding,
            TorrentState.Stopped or TorrentState.Stopping     => BackendState.Queued,
            TorrentState.Error                                => BackendState.Error,
            TorrentState.Hashing or TorrentState.HashingPaused => BackendState.Checking,
            _                                                  => BackendState.Downloading
        };

        string? filePath = null;
        if (state is BackendState.Seeding or BackendState.Downloading)
        {
            filePath = manager.Files.Count == 1
                ? manager.Files[0].FullPath
                : manager.ContainingDirectory;
        }

        // ETA: use long arithmetic to avoid int overflow on large torrents
        long etaSeconds = 0;
        if (manager.Monitor.DownloadRate > 0 && manager.Torrent != null)
        {
            var remaining = manager.Torrent.Size * (1.0 - manager.Progress / 100.0);
            var etaRaw    = (long)(remaining / manager.Monitor.DownloadRate);
            etaSeconds    = Math.Clamp(etaRaw, 0, int.MaxValue);
        }

        return new BackendStatus
        {
            Handle          = handle,
            Name            = manager.Torrent?.Name ?? handle,
            ProgressPercent = manager.Progress,
            SpeedBps        = (long)manager.Monitor.DownloadRate,
            EtaSeconds      = (int)etaSeconds,
            State           = state,
            TotalSizeBytes  = manager.Torrent?.Size ?? 0,
            FilePath        = filePath
        };
    }

    public async ValueTask DisposeAsync()
    {
        // Signal poll loop to exit
        lock (_pollLock)
        {
            _pollCts?.Cancel();
        }

        if (_pollTask != null)
        {
            try { await _pollTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* best effort */ }
        }

        _pollCts?.Dispose();

        if (_engine != null)
        {
            // Stop all active managers gracefully
            var allManagers = _managers.Values
                .Concat(_completedManagers)
                .Distinct()
                .ToArray();

            var stopTasks = allManagers.Select(m => m.StopAsync()).ToArray();
            try { await Task.WhenAll(stopTasks).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* best effort */ }

            await _engine.StopAllAsync();
            _engine.Dispose();
        }

        _engineLock.Dispose();
        _httpClient.Dispose();
    }
}
