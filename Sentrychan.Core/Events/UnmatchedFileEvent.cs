using MediatR;

namespace Sentrychan.Core.Events;

/// <summary>
/// Published when a downloaded video file couldn't be matched to any library
/// series and was moved to the _Unmatched folder.
/// The MainWindow shows a dismissible banner so the user can act.
/// </summary>
public record UnmatchedFileEvent(
    string FileName,
    string UnmatchedFolderPath
) : INotification;
