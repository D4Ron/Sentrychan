using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;
using Polly;

namespace Sentrychan.Core.Services;

public class WatchPartyClientService : IWatchPartyClientService, IAsyncDisposable
{
    private readonly ILogger<WatchPartyClientService> _logger;
    private HubConnection? _hubConnection;

    public bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;
    public string? CurrentUsername { get; private set; }

    public event Action<string>? UserJoined;
    public event Action<string>? UserLeft;
    public event Action<string, int>? SessionInfoReceived;
    public event Action? SyncRequested;

    public event Action<string, double>? PlaybackCommandReceived;
    public event Action<string, string>? ChatMessageReceived;
    public event Action<string, string>? ReactionReceived;
    public event Action<string, bool>? ParticipantReadyChanged;
    public event Action<string>? DownloadLinkReceived;
    public event Action? Disconnected;
    public event Action<string?>? Reconnecting;
    public event Action<string?>? Reconnected;

    public WatchPartyClientService(ILogger<WatchPartyClientService> logger)
    {
        _logger = logger;
    }

    public async Task<bool> ConnectAsync(string ip, int port, string username, string password, string sessionId, CancellationToken ct = default)
    {
        try
        {
            if (_hubConnection != null)
            {
                await DisconnectAsync(ct);
            }

            var url = $"http://{ip}:{port}/watchparty?username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}&sessionId={sessionId}";
            
            _hubConnection = new HubConnectionBuilder()
                .WithUrl(url)
                .WithAutomaticReconnect()
                .Build();

            _hubConnection.On<string>("UserJoined", (user) => UserJoined?.Invoke(user));
            _hubConnection.On<string>("UserLeft", (user) => UserLeft?.Invoke(user));
            _hubConnection.On<string, int>("SessionInfoReceived", (title, ep) => SessionInfoReceived?.Invoke(title, ep));
            _hubConnection.On<string, string>("ReceiveMessage", (user, message) => ChatMessageReceived?.Invoke(user, message));
            _hubConnection.On("SyncRequested", () => SyncRequested?.Invoke());
            
            _hubConnection.On<string, double>("PlaybackCommandReceived", (cmd, pos) => PlaybackCommandReceived?.Invoke(cmd, pos));
            _hubConnection.On<string, string>("ReactionReceived", (user, reaction) => ReactionReceived?.Invoke(user, reaction));
            _hubConnection.On<string, bool>("ParticipantReadyChanged", (user, isReady) => ParticipantReadyChanged?.Invoke(user, isReady));
            _hubConnection.On<string>("DownloadLinkReceived", (url) => DownloadLinkReceived?.Invoke(url));
            _hubConnection.On("Disconnected", () => Disconnected?.Invoke());

            _hubConnection.Reconnecting += (error) =>
            {
                _logger.LogWarning(error, "SignalR connection lost, attempting to reconnect...");
                Reconnecting?.Invoke(error?.Message);
                return Task.CompletedTask;
            };

            _hubConnection.Reconnected += (connectionId) =>
            {
                _logger.LogInformation("SignalR reconnected. ConnectionId: {ConnectionId}", connectionId);
                Reconnected?.Invoke(connectionId);
                return Task.CompletedTask;
            };

            _hubConnection.Closed += (error) =>
            {
                _logger.LogError(error, "SignalR connection closed.");
                Disconnected?.Invoke();
                return Task.CompletedTask;
            };

            var retryPolicy = Policy
                .Handle<Exception>()
                .WaitAndRetryAsync(5, retryAttempt => TimeSpan.FromSeconds(Math.Min(2 * retryAttempt, 10)),
                (ex, time) => _logger.LogWarning("Connection attempt failed. Retrying in {Time}s...", time.Seconds));

            await retryPolicy.ExecuteAsync(async () => await _hubConnection.StartAsync(ct));
            
            CurrentUsername = username;
            _logger.LogInformation("Connected to watch party at {Ip}:{Port} as {Username}", ip, port, username);
            
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to watch party at {Ip}:{Port} after retries", ip, port);
            return false;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_hubConnection != null)
        {
            try
            {
                await _hubConnection.StopAsync(ct);
                await _hubConnection.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error occurred while disconnecting from watch party");
            }
            finally
            {
                _hubConnection = null;
                CurrentUsername = null;
            }
        }
    }

    public async Task SendMessageAsync(string message, CancellationToken ct = default)
    {
        if (IsConnected && _hubConnection != null)
        {
            await _hubConnection.SendAsync("SendChatMessage", message, ct);
        }
    }

    public async Task SendReactionAsync(string reactionType, CancellationToken ct = default)
    {
        if (IsConnected && _hubConnection != null)
        {
            await _hubConnection.SendAsync("SendReaction", reactionType, ct);
        }
    }

    public async Task ReportPositionAsync(double positionSeconds, CancellationToken ct = default)
    {
        if (IsConnected && _hubConnection != null)
        {
            await _hubConnection.SendAsync("ReportPosition", positionSeconds, ct);
        }
    }

    public async Task RequestSyncAsync(CancellationToken ct = default)
    {
        if (IsConnected && _hubConnection != null)
        {
            await _hubConnection.SendAsync("RequestSync", ct);
        }
    }

    public async Task SetReadyAsync(bool isReady, CancellationToken ct = default)
    {
        if (IsConnected && _hubConnection != null)
        {
            await _hubConnection.SendAsync("SetReady", isReady, ct);
        }
    }

    public async Task JoinSessionAsync(string participantGuid, CancellationToken ct = default)
    {
        if (IsConnected && _hubConnection != null)
        {
            await _hubConnection.SendAsync("JoinSession", participantGuid, ct);
        }
    }

    public async Task LeaveSessionAsync(CancellationToken ct = default)
    {
        if (IsConnected && _hubConnection != null)
        {
            await _hubConnection.SendAsync("LeaveSession", ct);
        }
    }

    public async Task ShareDownloadLinkAsync(string downloadUrl, CancellationToken ct = default)
    {
        if (IsConnected && _hubConnection != null)
        {
            await _hubConnection.SendAsync("ShareDownloadLink", downloadUrl, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
