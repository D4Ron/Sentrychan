using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace Sentrychan.Core.Models;

/// <summary>
/// A row in <c>public.series_airings</c> — the push-notification fan-out
/// table. The RSS/monitor pipeline inserts one row per new release; every
/// signed-in client receives it via Realtime and each client decides
/// locally whether the show is in its library.
/// </summary>
[Table("series_airings")]
public class SeriesAiringEntry : BaseModel
{
    [PrimaryKey("id", shouldInsert: false)]
    public string? Id { get; set; }

    [Column("mal_id")]
    public int MalId { get; set; }

    [Column("series_title")]
    public string SeriesTitle { get; set; } = string.Empty;

    [Column("episode")]
    public int? Episode { get; set; }

    [Column("resolution")]
    public string? Resolution { get; set; }

    [Column("source")]
    public string? Source { get; set; }

    [Column("magnet")]
    public string? Magnet { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
