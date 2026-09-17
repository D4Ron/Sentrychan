using MediatR;

namespace Sentrychan.Core.Events;

public record NewEpisodeFoundEvent(
    int MalId,
    string SeriesTitle,
    int EpisodeNumber,
    string DownloadLink,
    string RssTitle,
    bool IsSecondary,
    bool IsSilent = false
) : INotification;