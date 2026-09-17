using MediatR;

namespace Sentrychan.Core.Events;

public record DownloadConfirmationEvent(
    int SeriesId,
    int MalId,
    string SeriesTitle,
    string PosterPath,          // local file path for the series poster image
    int EpisodeNumber,
    string ReleaseGroup,
    string? Resolution,
    string SizeDisplay,
    int Seeders,
    string DownloadLink,        // magnet or torrent URL
    string RssTitle
) : INotification;
