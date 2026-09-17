using System;

namespace Sentrychan.Core.Interfaces;

public interface ISyncEngine
{
    bool IsRunning { get; }
    
    Task StartAsync(bool isHost, System.Threading.CancellationToken ct);
    Task StopAsync();
    void SetLocalPlayer(IPlayerBridgeService player);

    event Action<SyncCorrection>? CorrectionRequired;
    event Action<double>? HostPositionUpdated;
    event Action<bool>? OnSyncStatusChanged;
}

public class SyncCorrection
{
    public double TargetPositionSeconds { get; set; }
    public double DriftSeconds { get; set; }
    public SyncCorrectionType Type { get; set; }
}

public enum SyncCorrectionType
{
    Soft, // Small drift: just seek
    Hard  // Large drift: pause, seek, wait, play
}
