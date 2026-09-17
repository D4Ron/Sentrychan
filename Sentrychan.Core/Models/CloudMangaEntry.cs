using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace Sentrychan.Core.Models;

/// <summary>
/// A row in <c>public.user_manga_library</c> — the cloud mirror of a Manga
/// (or novel) tracked in the user's library. Uses (source, source_id) as the
/// stable identity so a manga survives being removed and re-added locally.
/// </summary>
[Table("user_manga_library")]
public class CloudMangaEntry : BaseModel
{
    [Column("user_id")]
    public string UserId { get; set; } = string.Empty;

    [Column("source")]
    public string Source { get; set; } = string.Empty;

    [Column("source_id")]
    public string SourceId { get; set; } = string.Empty;

    [Column("title")]
    public string Title { get; set; } = string.Empty;

    [Column("cover_url")]
    public string? CoverUrl { get; set; }

    [Column("total_chapters")]
    public double? TotalChapters { get; set; }

    [Column("last_read_chapter")]
    public double LastReadChapter { get; set; }

    [Column("is_novel")]
    public bool IsNovel { get; set; }

    [Column("is_censored")]
    public bool IsCensored { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
