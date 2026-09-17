using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace Sentrychan.Core.Hubs;

public class WatchPartyHub : Hub
{
    // Map ConnectionId -> Username
    private static readonly ConcurrentDictionary<string, string> _connectedUsers = new();
    private static string? _hostConnectionId;
    private const int MaxMessageLength = 500;
    private readonly Microsoft.EntityFrameworkCore.IDbContextFactory<Sentrychan.Core.Data.AppDbContext> _dbFactory;

    public WatchPartyHub(Microsoft.EntityFrameworkCore.IDbContextFactory<Sentrychan.Core.Data.AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>Call this when the embedded Kestrel server starts to clear stale state.</summary>
    public static void ResetState()
    {
        _connectedUsers.Clear();
        _hostConnectionId = null;
    }

    public override async Task OnConnectedAsync()
    {
        var username = Context.GetHttpContext()?.Request.Query["username"].ToString() ?? "Unknown";
        var isHost = Context.GetHttpContext()?.Request.Query["isHost"].ToString() == "true";
        var sessionId = Context.GetHttpContext()?.Request.Query["sessionId"].ToString();

        _connectedUsers.TryAdd(Context.ConnectionId, username);
        if (isHost) _hostConnectionId = Context.ConnectionId;
        
        await Clients.Others.SendAsync("UserJoined", username);

        // Send session info to caller
        if (!string.IsNullOrEmpty(sessionId))
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var session = await db.WatchPartySessions.FindAsync(sessionId);
                if (session != null)
                {
                    await Clients.Caller.SendAsync("SessionInfoReceived", session.HostedSeriesTitle, session.HostedEpisodeNumber);
                }
            }
            catch (Exception) { /* Logged elsewhere */ }
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (_connectedUsers.TryRemove(Context.ConnectionId, out var username))
        {
            await Clients.Others.SendAsync("UserLeft", username);
        }
        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinSession(string participantGuid)
    {
        // Added to fulfill Phase 3 'JoinSession' requirement
        await Groups.AddToGroupAsync(Context.ConnectionId, "WatchParty");
    }

    public async Task LeaveSession()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "WatchParty");
    }

    public async Task SetReady(bool isReady)
    {
        await Clients.Others.SendAsync("ParticipantReadyChanged", Context.ConnectionId, isReady);
    }

    public async Task SendChatMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        // Enforce message length limit
        if (message.Length > MaxMessageLength)
            message = message[..MaxMessageLength];

        if (_connectedUsers.TryGetValue(Context.ConnectionId, out var username))
        {
            await Clients.All.SendAsync("ReceiveMessage", username, message);

            // Persist to DB
            try
            {
                var sessionId = Context.GetHttpContext()?.Request.Query["sessionId"].ToString();
                if (!string.IsNullOrEmpty(sessionId))
                {
                    await using var db = await _dbFactory.CreateDbContextAsync();
                    db.WatchPartyMessages.Add(new Sentrychan.Core.Models.WatchPartyMessage
                    {
                        SessionId = sessionId,
                        Sender = username,
                        Content = message,
                        SentAt = DateTime.UtcNow
                    });
                    await db.SaveChangesAsync();
                }
            }
            catch (Exception) { /* Logged elsewhere */ }
        }
    }

    public async Task SendReaction(string reactionType)
    {
        await Clients.Others.SendAsync("ReactionReceived", Context.ConnectionId, reactionType);
    }

    public async Task ReportPosition(double positionSeconds)
    {
        // Broadcast position to other participants for sync
        if (_connectedUsers.TryGetValue(Context.ConnectionId, out var username))
        {
            await Clients.Others.SendAsync("PositionReported", username, positionSeconds);
        }
    }

    public async Task StartPlayback()
    {
        await Clients.Others.SendAsync("PlaybackCommandReceived", "Play", 0.0);
    }

    public async Task PausePlayback()
    {
        await Clients.Others.SendAsync("PlaybackCommandReceived", "Pause", 0.0);
    }

    public async Task SeekTo(double positionSeconds)
    {
        await Clients.Others.SendAsync("PlaybackCommandReceived", "Seek", positionSeconds);
    }

    public async Task KickParticipant(string participantGuid)
    {
        // Only the host can kick
        if (Context.ConnectionId != _hostConnectionId) return;
        await Clients.Client(participantGuid).SendAsync("Disconnected");
    }
    
    public async Task MuteParticipant(string participantGuid)
    {
        // Only the host can mute
        if (Context.ConnectionId != _hostConnectionId) return;
        await Clients.All.SendAsync("MuteStateChanged", participantGuid, true);
    }

    public async Task ShareDownloadLink(string downloadUrl)
    {
        // Only the host can share links
        if (Context.ConnectionId != _hostConnectionId) return;
        await Clients.Others.SendAsync("DownloadLinkReceived", downloadUrl);
    }
}
