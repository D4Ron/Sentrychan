namespace Sentrychan.Core.Interfaces;

public interface IWatchPartyHostService
{
    bool IsHosting { get; }
    string? CurrentSessionId { get; }
    int Port { get; }

    Task<bool> StartPartyAsync(string name, string password, int? seriesId, string seriesTitle, int episodeNumber, CancellationToken ct = default);
    Task StopPartyAsync(CancellationToken ct = default);

    Task<bool> CreateSessionAsync(string name, string password, int? seriesId, string seriesTitle, int episodeNumber, CancellationToken ct = default);
    Task<bool> StartServerAsync(CancellationToken ct = default);
    Task BroadcastPlaybackCommandAsync(string command, double positionSeconds, CancellationToken ct = default);
}
