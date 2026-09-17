using MediatR;

namespace Sentrychan.Core.Events;

/// <summary>
/// Published when a secondary feed finds an episode that wasn't covered
/// by any priority feed. The UI surfaces these for manual review rather
/// than auto-downloading.
/// </summary>
public record PendingEpisodeFoundEvent(
    int MalId,
    string SeriesTitle,
    int EpisodeNumber,
    string DownloadLink,
    string RssTitle,
    string FeedUrl
) : INotification;