using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace Sentrychan.Core.Models;

/// <summary>
/// One row in the Supabase `watch_rooms` table.
///
/// Required SQL (run in Supabase SQL editor):
/// <code>
/// create table public.watch_rooms (
///   id              uuid primary key default gen_random_uuid(),
///   host_id         uuid references auth.users(id) on delete cascade not null,
///   host_name       text not null,
///   series_title    text not null,
///   mal_id          integer not null default 0,
///   episode         integer,
///   hb_session_id   text not null,
///   embed_url       text not null,
///   is_active       boolean not null default true,
///   created_at      timestamptz default now()
/// );
/// alter table public.watch_rooms enable row level security;
/// -- Friends + self can see active rooms
/// create policy "friends see active rooms"
///   on public.watch_rooms for select
///   using (
///     is_active = true and (
///       auth.uid() = host_id
///       or exists (
///         select 1 from public.friendships f
///         where f.status = 'accepted'
///           and ((f.requester_id = auth.uid() and f.addressee_id = watch_rooms.host_id)
///             or (f.addressee_id = auth.uid() and f.requester_id = watch_rooms.host_id))
///       )
///     )
///   );
/// -- Only host can insert/update/delete own rooms
/// create policy "host manages own rooms"
///   on public.watch_rooms for all
///   using (auth.uid() = host_id);
/// </code>
/// </summary>
[Table("watch_rooms")]
public class WatchRoomEntry : BaseModel
{
    [PrimaryKey("id", shouldInsert: false)]
    public string? Id { get; set; }

    [Column("host_id")]
    public string HostId { get; set; } = string.Empty;

    [Column("host_name")]
    public string HostName { get; set; } = string.Empty;

    [Column("series_title")]
    public string SeriesTitle { get; set; } = string.Empty;

    [Column("mal_id")]
    public int MalId { get; set; }

    [Column("episode")]
    public int? Episode { get; set; }

    [Column("hb_session_id")]
    public string HbSessionId { get; set; } = string.Empty;

    [Column("embed_url")]
    public string EmbedUrl { get; set; } = string.Empty;

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
