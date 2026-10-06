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
    /// Year this entry started airing. Names the library folder ("Title (2023)") the way
    /// Jellyfin and Plex expect. Null until known — the folder is then just "Title".
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    /// TV, Movie, OVA, ONA, Special… as the anime database reports it. A Movie is filed as
    /// "Title (Year)/Title (Year).ext" instead of by season and episode.
    /// </summary>
    public string? MediaType { get; set; }

    /// <summary>"Don't tidy": Tidy library leaves this series alone, and new episodes keep the old layout.</summary>
    public bool TidyExcluded { get; set; }

    /// <summary>"Keep full file names": files move into the naming template's folders but keep their release names.</summary>
    public bool KeepFileNames { get; set; }

    /// <summary>
    /// "Separate seasons for parts": each part or cour of this show gets a season folder of its own,
    /// instead of continuing its season's numbers (the default, see SeasonLayout).
    /// </summary>
    public bool SeparateParts { get; set; }

    /// <summary>
    /// The user's word on how groups number this season: episode 1 comes out as offset + 1
    /// ("Show - 13" is episode 1 at 12). Null works it out from the season chain (ReleaseMatcher);
    /// it's there for shows the database splits differently from the groups, or doesn't link at all.
    /// </summary>
    public int? EpisodeNumberOffset { get; set; }

    /// <summary>This series' own release-group rule (Any / Prefer / Only); null follows the app's setting.</summary>
    public string? GroupMode { get; set; }

    /// <summary>This series' own preferred groups, comma separated; null uses the app's list.</summary>
    public string? PreferredGroups { get; set; }

    public bool IsMovie => string.Equals(MediaType, "Movie", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// AniDB cover URL populated at runtime when Secret Mode activates.
    /// Not persisted — fetched on demand by the UI layer.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string? AniDbCoverUrl { get; set; }

    // Navigation
    public List<DownloadJob> DownloadJobs { get; set; } = [];
}
