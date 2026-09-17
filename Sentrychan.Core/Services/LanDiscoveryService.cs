using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.Core.Services;

public class LanDiscoveryService : ILanDiscoveryService, IDisposable
{
    private const int DiscoveryPort = 17742;
    private readonly ILogger<LanDiscoveryService> _logger;
    private readonly ConcurrentDictionary<string, LanParty> _discoveredParties = new();
    
    private UdpClient? _broadcastClient;
    private UdpClient? _listenerClient;
    private CancellationTokenSource? _broadcastCts;
    private CancellationTokenSource? _listenerCts;
    private Timer? _stalenessTimer;

    public event Action<LanParty>? PartyDiscovered;
    public event Action<string>? PartyLost;

    public LanDiscoveryService(ILogger<LanDiscoveryService> logger)
    {
        _logger = logger;
        _stalenessTimer = new Timer(CheckStaleness, null, 2000, 2000);
    }

    public async Task StartBroadcastingAsync(string roomCode, string displayName, CancellationToken ct)
    {
        await StopBroadcastingAsync();
        
        _broadcastCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _broadcastClient = new UdpClient();
        _broadcastClient.EnableBroadcast = true;
        _broadcastClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        var beacon = new
        {
            roomCode,
            hostName = displayName,
            port = 7742,
            appVersion = "2.1"
        };
        
        var json = JsonSerializer.Serialize(beacon);
        var data = Encoding.UTF8.GetBytes(json);

        _ = Task.Run(async () =>
        {
            try
            {
                while (!_broadcastCts.Token.IsCancellationRequested)
                {
                    await _broadcastClient.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
                    await Task.Delay(2000, _broadcastCts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in LAN broadcast");
            }
        }, _broadcastCts.Token);
    }

    public async Task StopBroadcastingAsync()
    {
        _broadcastCts?.Cancel();
        _broadcastCts?.Dispose();
        _broadcastCts = null;
        
        _broadcastClient?.Dispose();
        _broadcastClient = null;
        
        await Task.CompletedTask;
    }

    public async Task StartListeningAsync(CancellationToken ct)
    {
        await StopListeningAsync();
        
        _listenerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listenerClient = new UdpClient();
        _listenerClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, false);
        _listenerClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listenerClient.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

        _ = Task.Run(async () =>
        {
            try
            {
                while (!_listenerCts.Token.IsCancellationRequested)
                {
                    var result = await _listenerClient.ReceiveAsync(_listenerCts.Token);
                    var json = Encoding.UTF8.GetString(result.Buffer);
                    
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        var roomCode = root.GetProperty("roomCode").GetString();
                        if (string.IsNullOrEmpty(roomCode)) continue;

                        var party = new LanParty(
                            roomCode,
                            root.GetProperty("hostName").GetString() ?? "Unknown",
                            $"{result.RemoteEndPoint.Address}:7742",
                            DateTime.UtcNow
                        );

                        _discoveredParties.AddOrUpdate(roomCode, party, (_, old) => party);
                        PartyDiscovered?.Invoke(party);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse LAN discovery beacon");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in LAN listener");
            }
        }, _listenerCts.Token);
    }

    public async Task StopListeningAsync()
    {
        _listenerCts?.Cancel();
        _listenerCts?.Dispose();
        _listenerCts = null;
        
        _listenerClient?.Dispose();
        _listenerClient = null;
        
        _discoveredParties.Clear();
        await Task.CompletedTask;
    }

    private void CheckStaleness(object? state)
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _discoveredParties)
        {
            if ((now - kvp.Value.LastSeen).TotalSeconds > 6)
            {
                if (_discoveredParties.TryRemove(kvp.Key, out _))
                {
                    PartyLost?.Invoke(kvp.Key);
                }
            }
        }
    }

    public void Dispose()
    {
        _stalenessTimer?.Dispose();
        _stalenessTimer = null;
        StopBroadcastingAsync().GetAwaiter().GetResult();
        StopListeningAsync().GetAwaiter().GetResult();
    }
}
