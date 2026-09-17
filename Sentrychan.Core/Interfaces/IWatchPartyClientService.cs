using System;
using System.Threading;
using System.Threading.Tasks;

namespace Sentrychan.Core.Interfaces;

public interface IWatchPartyClientService
{
    bool IsConnected { get; }
    string? CurrentUsername { get; }

    event Action<string>? UserJoined;
    event Action<string>? UserLeft;
    event Action<string, int>? SessionInfoReceived;
    event Action? SyncRequested;

    event Action<string, double>? PlaybackCommandReceived;
    event Action<string, string>? ChatMessageReceived;
    event Action<string, string>? ReactionReceived;
    event Action<string, bool>? ParticipantReadyChanged;
    event Action<string>? DownloadLinkReceived;
    event Action? Disconnected;
    event Action<string?>? Reconnecting;
    event Action<string?>? Reconnected;

    Task<bool> ConnectAsync(string ip, int port, string username, string password, string sessionId, CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    
    Task SendMessageAsync(string message, CancellationToken ct = default);
    Task SendReactionAsync(string reactionType, CancellationToken ct = default);
    Task ReportPositionAsync(double positionSeconds, CancellationToken ct = default);
    Task SetReadyAsync(bool isReady, CancellationToken ct = default);
    Task JoinSessionAsync(string participantGuid, CancellationToken ct = default);
    Task LeaveSessionAsync(CancellationToken ct = default);
    Task ShareDownloadLinkAsync(string downloadUrl, CancellationToken ct = default);
}
