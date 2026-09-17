using System;

namespace Sentrychan.Core.Models;

public class WatchPartyMessage
{
    public int Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime SentAt { get; set; }
    
    public WatchPartySession? Session { get; set; }
}
