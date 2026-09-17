using MediatR;
using Microsoft.Extensions.Logging;

namespace Sentrychan.Core.Events.Handlers;

public class MonitorStatusHandler : INotificationHandler<MonitorStatusEvent>
{
    private readonly ILogger<MonitorStatusHandler> _logger;

    public MonitorStatusHandler(ILogger<MonitorStatusHandler> logger)
    {
        _logger = logger;
    }

    public Task Handle(MonitorStatusEvent notification, CancellationToken ct)
    {
        switch (notification.Status)
        {
            case MonitorStatus.CheckStarted:
                _logger.LogInformation(
                    "Monitor check started {Manual}",
                    notification.IsManual ? "(manual)" : "(scheduled)");
                break;

            case MonitorStatus.CheckCompleted:
                _logger.LogInformation(
                    "Monitor check completed — {New} new, {Pending} pending",
                    notification.NewEpisodesCount, notification.PendingCount);
                break;

            case MonitorStatus.Error:
                _logger.LogError(
                    "Monitor error: {Message}", notification.ErrorMessage);
                break;
        }

        return Task.CompletedTask;
    }
}