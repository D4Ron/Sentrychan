using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace Sentrychan.Core.Models;

/// <summary>
/// Represents one row in the Supabase `user_library` table.
///
/// Required SQL (run in Supabase SQL editor):
/// <code>
/// create table user_library (
///   id uuid primary key default gen_random_uuid(),
///   user_id uuid references auth.users(id) on delete cascade,
///   mal_id integer not null,
///   title text not null,
///   last_episode integer default 0,
///   total_episodes integer default 0,
///   airing_status text,
///   monitoring_state text,
///   added_at timestamptz default now(),
///   updated_at timestamptz default now(),
///   unique(user_id, mal_id)
/// );
/// alter table user_library enable row level security;
/// create policy "users manage own library"
///   on user_library for all using (auth.uid() = user_id);
/// </code>
/// </summary>
[Table("user_library")]
public class CloudSeriesEntry : BaseModel
{
    [Column("user_id")]
    public string UserId { get; set; } = string.Empty;

    [Column("mal_id")]
    public int MalId { get; set; }

    [Column("title")]
    public string Title { get; set; } = string.Empty;

    [Column("last_episode")]
    public int LastEpisode { get; set; }

    [Column("total_episodes")]
    public int TotalEpisodes { get; set; }

    [Column("airing_status")]
    public string? AiringStatus { get; set; }

    [Column("monitoring_state")]
    public string? MonitoringState { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
