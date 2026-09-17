using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;
using System;

namespace Sentrychan.Core.Services;

public class SyncEngine : ISyncEngine
{
    private readonly ILogger<SyncEngine> _logger;
    private readonly IWatchPartyClientService _clientService;
    private IPlayerBridgeService? _playerBridge;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _videoLoaded = false;
    private double _lastKnownHostPosition = 0;
    private bool _isHost;
    private bool _isRunning;
    private bool _isPlaying;
    private double _syncToleranceSeconds = 2.0;

    public bool IsRunning => _isRunning;

    public event Action<SyncCorrection>? CorrectionRequired;
    public event Action<double>? HostPositionUpdated;
    public event Action<bool>? OnSyncStatusChanged;

    public SyncEngine(ILogger<SyncEngine> logger, IWatchPartyClientService clientService)
    {
        _logger = logger;
        _clientService = clientService;
    }

    public void SetLocalPlayer(IPlayerBridgeService player)
    {
        _playerBridge = player;
    }

    public Task StartAsync(bool isHost, CancellationToken externalCt)
    {
        if (_playerBridge == null)
        {
            _logger.LogError("SyncEngine: Cannot start without a local player bridge.");
            return Task.CompletedTask;
        }

        _isHost = isHost;
        _isRunning = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);

        // Subscribe to player events
        _playerBridge.PositionChanged += OnPositionChanged;
        _playerBridge.PlaybackStateChanged += OnPlaybackStateChanged;
        _clientService.PlaybackCommandReceived += OnPlaybackCommand;

        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token), _cts.Token);
        _logger.LogInformation("SyncEngine started. IsHost: {IsHost}", _isHost);
        return Task.CompletedTask;
    }

    private void OnPositionChanged(double position)
    {
        // SG: Only consider video loaded if we have a non-zero position
        if (position > 0.5) _videoLoaded = true;
    }

    private void OnPlaybackStateChanged(bool isPlaying)
    {
        _isPlaying = isPlaying;
    }

    private void OnPlaybackCommand(string command, double position)
    {
        _lastKnownHostPosition = position;
        // The actual play/pause/seek is handled by WatchPartyViewModel
        // SyncEngine only tracks the host position for drift calculation
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // SG: Use Task.Delay for clean cancellation
                await Task.Delay(2000, ct);
                
                if (_playerBridge == null || !_videoLoaded) continue;

                var position = await _playerBridge.GetPositionAsync();
                
                // If we are host, report our position to others
                if (_isHost)
                {
                    await _clientService.ReportPositionAsync(position);
                    HostPositionUpdated?.Invoke(position);
                }
                else
                {
                    // If we are participant, check if we drifted from host
                    await CheckDriftAsync(position, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SyncEngine loop error, continuing");
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task CheckDriftAsync(double localPosition, CancellationToken ct)
    {
        // _lastKnownHostPosition is updated when PlaybackCommand is received
        if (_lastKnownHostPosition <= 0) return;

        var drift = Math.Abs(localPosition - _lastKnownHostPosition);
        var tolerance = _syncToleranceSeconds;

        if (drift <= tolerance) return;

        var correction = new SyncCorrection
        {
            TargetPositionSeconds = _lastKnownHostPosition,
            DriftSeconds = drift,
            Type = drift > 5.0 ? SyncCorrectionType.Hard : SyncCorrectionType.Soft
        };

        _logger.LogInformation("SyncEngine: Drift detected ({Drift}s). Triggering {Type} correction.", drift, correction.Type);
        CorrectionRequired?.Invoke(correction);

        if (_playerBridge == null) return;

        if (correction.Type == SyncCorrectionType.Soft)
        {
            await _playerBridge.SeekAsync(_lastKnownHostPosition);
        }
        else
        {
            // Hard correction: pause, seek, resume after 500ms
            OnSyncStatusChanged?.Invoke(true);
            await _playerBridge.PauseAsync();
            await _playerBridge.SeekAsync(_lastKnownHostPosition);
            await Task.Delay(500, ct);
            await _playerBridge.PlayAsync();
            OnSyncStatusChanged?.Invoke(false);
        }
    }

    public async Task StopAsync()
    {
        _isRunning = false;
        if (_playerBridge != null)
        {
            _playerBridge.PositionChanged -= OnPositionChanged;
            _playerBridge.PlaybackStateChanged -= OnPlaybackStateChanged;
        }
        _clientService.PlaybackCommandReceived -= OnPlaybackCommand;
        
        _cts?.Cancel();
        if (_loopTask != null)
        {
            try { await _loopTask; } catch { /* ignore cancellation errors */ }
        }
        _cts?.Dispose();
        _cts = null;
        _videoLoaded = false;
        _lastKnownHostPosition = 0;
        _logger.LogInformation("SyncEngine stopped.");
    }
}
