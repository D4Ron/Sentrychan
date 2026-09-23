namespace Sentrychan.Core.Interfaces;

/// <summary>One show on the release schedule for a given day.</summary>
public record AiringScheduleEntry(
    string Title,
    string PosterUrl,
    string Time,      // already in the user's local timezone
    string PageUrl,
    int MalId = 0);   // known outright for MAL-sourced entries; 0 means "resolve by title"

/// <summary>"What airs today", in the user's local time. Best-effort.</summary>
public interface IAiringScheduleService
{
    /// <summary>Shows airing on the user's local weekday, in local time. Best-effort.</summary>
    Task<List<AiringScheduleEntry>> GetTodayAsync(CancellationToken ct = default);
}

/// <summary>
/// Lets a loaded source pack supply its own schedule. The app always has a MAL-based schedule
/// to fall back on, so this is an override rather than a requirement.
/// </summary>
public interface IAiringScheduleRegistry
{
    void Add(IAiringScheduleService provider);
}
