namespace Sentrychan.Core.Models;

public class Series
{
    public int Id { get; set; }
    public int MalId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string OriginalTitle { get; set; } = string.Empty;
    public string TitleJapanese { get; set; } = string.Empty;
    public string AlternativeTitlesJson { get; set; } = "[]";
    public string PosterPath { get; set; } = string.Empty;
    public int LastEpisodeNumber { get; set; } = 0;
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public string? AiringStatus { get; set; }
    public int? TotalEpisodes { get; set; }
    public DateTime? LastCheckedAt { get; set; }
    public string? QualityPreference { get; set; }
    public MonitoringState MonitoringState { get; set; } = MonitoringState.Active;
    public bool AutoCompleteEnabled { get; set; } = true;

    /// <summary>
    /// When true the RSS monitor downloads new episodes immediately without asking the user.
    /// When false (default) a confirmation card is shown for each new episode found.
    /// </summary>
    public bool AutoDownload { get; set; } = false;

    /// <summary>
    /// Season number used for the library folder structure:
    /// /Anime/{Title}/Season {SeasonNumber}/{filename}
    /// Default is 1. User can override per-series in Series Detail.
    /// </summary>
    public int SeasonNumber { get; set; } = 1;
    public bool IsCensored { get; set; } = false;

    /// <summary>
    /// AniDB cover URL populated at runtime when Secret Mode activates.
    /// Not persisted — fetched on demand by the UI layer.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string? AniDbCoverUrl { get; set; }

    // Navigation
    public List<DownloadJob> DownloadJobs { get; set; } = [];
}
