using MediatR;

namespace Sentrychan.Core.Events;

/// <summary>
/// Published after a downloaded file has been confirmed complete
/// and successfully moved to its final library location.
/// UI subscribes to refresh the episode grid and series card.
/// </summary>
public record NewFileArrivedEvent(
    int SeriesId,
    int MalId,
    string SeriesTitle,
    int EpisodeNumber,
    string FinalFilePath,
    bool WasLastEpisodeUpdated   // true if series.LastEpisodeNumber was changed
) : INotification;

/// <summary>
/// Published alongside NewFileArrivedEvent when LastEpisodeNumber was updated.
/// The UI shows an undo toast for 10 seconds; after that this event expires.
/// </summary>
public record UndoableEpisodeUpdateEvent(
    int SeriesId,
    int MalId,
    string SeriesTitle,
    int NewEpisodeNumber,
    int PreviousEpisodeNumber,
    DateTime ExpiresAt          // 10 seconds from publication
) : INotification;
