using MediatR;

namespace Sentrychan.Core.Events;

/// <summary>
/// Published when an episode is found from multiple release groups and no
/// preferred source is configured (or the preferred source wasn't found).
/// The UI should present a picker so the user can choose which source to download.
/// </summary>
public record MultiSourceEpisodeEvent(
    int MalId,
    string SeriesTitle,
    int EpisodeNumber,
    IReadOnlyList<EpisodeSourceCandidate> Sources
) : INotification;

/// <summary>A single release-group candidate for an episode.</summary>
public record EpisodeSourceCandidate(
    string ReleaseGroup,
    string DownloadLink,
    string RssTitle,
    string? Resolution = null,
    string SizeDisplay = "",
    int Seeders = 0
);
