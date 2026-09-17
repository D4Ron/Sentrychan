using System;

namespace Sentrychan.Core.Models;

public class WatchPartyParticipant
{
    public int Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public bool IsReady { get; set; }
    public bool IsMuted { get; set; }
    public bool IsHost { get; set; }
    
    public WatchPartySession? Session { get; set; }
}
