using Newtonsoft.Json;
using Supabase.Realtime.Models;

namespace Sentrychan.Core.Models;

/// <summary>
/// A single viewer's presence in a watch room, sent via Supabase Realtime
/// Presence (CRDT). Every viewer tracks one of these; the channel returns
/// the merged set to every subscriber and auto-removes disconnected
/// viewers. Used to show "3 friends watching now" on co-watch cards.
/// </summary>
public class WatchRoomPresence : BasePresence
{
    [JsonProperty("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonProperty("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonProperty("avatarUrl")]
    public string? AvatarUrl { get; set; }

    [JsonProperty("joinedAt")]
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}
