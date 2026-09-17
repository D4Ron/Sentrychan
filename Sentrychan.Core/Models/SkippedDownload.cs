namespace Sentrychan.Core.Models;

/// <summary>
/// Records a user decision to skip downloading a specific episode.
/// The RSS monitor checks this table before queuing or showing a confirmation card,
/// preventing the same episode from being repeatedly offered.
/// </summary>
public class SkippedDownload
{
    public int Id { get; set; }

    public int SeriesId { get; set; }
    public Series? Series { get; set; }

    public int EpisodeNumber { get; set; }

    /// <summary>
    /// When true the episode is permanently skipped (filler, recap, etc.).
    /// When false it was skipped for one cycle only and will appear again next RSS check.
    /// </summary>
    public bool IsPermanent { get; set; } = false;

    public DateTime SkippedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Cycle count — incremented each time the user picks "Skip Once".</summary>
    public int SkipCount { get; set; } = 1;
}
