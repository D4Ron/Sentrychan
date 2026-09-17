using System;
using System.Threading;
using System.Threading.Tasks;

namespace Sentrychan.Core.Interfaces;

public record TunnelResult(bool Success, string? PublicAddress = null, string? ErrorMessage = null);

public interface ITunnelService : IDisposable
{
    bool IsActive { get; }
    string? PublicAddress { get; }
    Task<TunnelResult> StartTunnelAsync(int localPort, CancellationToken ct);
    Task StopTunnelAsync();
}
