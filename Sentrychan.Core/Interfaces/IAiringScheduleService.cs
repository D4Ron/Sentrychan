namespace Sentrychan.Core.Interfaces;

/// <summary>One show on the release schedule for a given day.</summary>
public record AiringScheduleEntry(
    string Title,
    string PosterUrl,
    string Time,      // already in the user's local timezone
    string PageUrl);

/// <summary>
/// "What releases today" — sourced from SubsPlease's own schedule API, which is
/// the actual release calendar (times localized) rather than MAL metadata.
/// </summary>
public interface IAiringScheduleService
{
    /// <summary>Shows releasing on the user's local weekday, in local time. Best-effort.</summary>
    Task<List<AiringScheduleEntry>> GetTodayAsync(CancellationToken ct = default);
}
