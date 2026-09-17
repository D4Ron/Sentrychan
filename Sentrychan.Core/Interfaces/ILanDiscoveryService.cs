using System;
using System.Threading;
using System.Threading.Tasks;

namespace Sentrychan.Core.Interfaces;

public record LanParty(string RoomCode, string HostDisplayName, string HostAddress, DateTime LastSeen);

public interface ILanDiscoveryService
{
    Task StartBroadcastingAsync(string roomCode, string displayName, CancellationToken ct);
    Task StopBroadcastingAsync();
    Task StartListeningAsync(CancellationToken ct);
    Task StopListeningAsync();
    
    event Action<LanParty>? PartyDiscovered;
    event Action<string>? PartyLost;
}
