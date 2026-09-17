using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace Sentrychan.Core.Models;

/// <summary>
/// One row in the Supabase `friendships` table.
///
/// Required SQL (run in Supabase SQL editor):
/// <code>
/// create table public.friendships (
///   id           uuid primary key default gen_random_uuid(),
///   requester_id uuid references auth.users(id) on delete cascade not null,
///   addressee_id uuid references auth.users(id) on delete cascade not null,
///   status       text not null default 'pending',  -- pending | accepted | blocked
///   created_at   timestamptz default now(),
///   unique(requester_id, addressee_id)
/// );
/// alter table public.friendships enable row level security;
/// create policy "users manage own friendships"
///   on public.friendships for all
///   using (auth.uid() = requester_id or auth.uid() = addressee_id);
/// </code>
/// </summary>
[Table("friendships")]
public class FriendshipEntry : BaseModel
{
    [PrimaryKey("id", shouldInsert: false)]
    public string? Id { get; set; }

    [Column("requester_id")]
    public string RequesterId { get; set; } = string.Empty;

    [Column("addressee_id")]
    public string AddresseeId { get; set; } = string.Empty;

    /// <summary>"pending" | "accepted" | "blocked"</summary>
    [Column("status")]
    public string Status { get; set; } = "pending";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    // ── Helpers (not persisted) ────────────────────────────────────

    /// <summary>Display name resolved at runtime from user_profiles or truncated ID.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string? FriendDisplayName { get; set; }

    /// <summary>Resolves the peer's user ID relative to the current user.</summary>
    public string PeerIdFor(string currentUserId) =>
        RequesterId == currentUserId ? AddresseeId : RequesterId;
}
