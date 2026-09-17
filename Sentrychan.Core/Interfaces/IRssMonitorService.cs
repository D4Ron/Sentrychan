namespace Sentrychan.Core.Interfaces;

public interface IRssMonitorService
{
    bool IsMonitoring { get; }
    Task ManualCheckAsync(CancellationToken ct = default);
    Task CheckSingleFeedAsync(int feedId, CancellationToken ct = default);

    /// <summary>Stop the periodic background checks (persists across restart).</summary>
    void Pause();

    /// <summary>Resume the periodic background checks.</summary>
    void Resume();
}