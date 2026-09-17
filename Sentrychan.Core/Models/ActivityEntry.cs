using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace Sentrychan.Core.Models;

/// <summary>
/// One row in the Supabase `activity_feed` table.
///
/// Required SQL (run in Supabase SQL editor):
/// <code>
/// create table public.activity_feed (
///   id           uuid primary key default gen_random_uuid(),
///   user_id      uuid references auth.users(id) on delete cascade not null,
///   event_type   text not null,   -- watched_episode | added_series | completed_series
///   series_title text not null,
///   mal_id       integer not null,
///   episode      integer,
///   created_at   timestamptz default now()
/// );
/// alter table public.activity_feed enable row level security;
/// create policy "friends can read activity"
///   on public.activity_feed for select
///   using (
///     auth.uid() = user_id
///     or exists (
///       select 1 from public.friendships f
///       where f.status = 'accepted'
///         and ((f.requester_id = auth.uid() and f.addressee_id = activity_feed.user_id)
///           or (f.addressee_id = auth.uid() and f.requester_id = activity_feed.user_id))
///     )
///   );
/// create policy "users insert own activity"
///   on public.activity_feed for insert with check (auth.uid() = user_id);
/// </code>
/// </summary>
[Table("activity_feed")]
public class ActivityEntry : BaseModel
{
    [Column("user_id")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>"watched_episode" | "added_series" | "completed_series"</summary>
    [Column("event_type")]
    public string EventType { get; set; } = string.Empty;

    [Column("series_title")]
    public string SeriesTitle { get; set; } = string.Empty;

    [Column("mal_id")]
    public int MalId { get; set; }

    [Column("episode")]
    public int? Episode { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    // ── Display helpers (not persisted) ───────────────────────────

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string? AuthorDisplayName { get; set; }

    public string Summary =>
        EventType switch
        {
            "watched_episode" => Episode.HasValue
                ? $"watched {SeriesTitle} ep. {Episode}"
                : $"watched {SeriesTitle}",
            "added_series"       => $"added {SeriesTitle} to library",
            "completed_series"   => $"finished {SeriesTitle}",
            _                    => $"{EventType}: {SeriesTitle}"
        };
}
