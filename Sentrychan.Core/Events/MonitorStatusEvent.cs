using MediatR;

namespace Sentrychan.Core.Events;

public enum MonitorStatus
{
    CheckStarted,
    CheckCompleted,
    Error
}

public record MonitorStatusEvent(
    MonitorStatus Status,
    int NewEpisodesCount = 0,
    int PendingCount = 0,
    string? ErrorMessage = null,
    bool IsManual = false
) : INotification;