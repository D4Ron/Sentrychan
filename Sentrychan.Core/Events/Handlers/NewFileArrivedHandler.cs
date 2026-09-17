using MediatR;
using Microsoft.Extensions.Logging;

namespace Sentrychan.Core.Events.Handlers;

/// <summary>
/// Logs file arrival events. The UI subscribes to this notification
/// directly via the same MediatR registration pattern used for
/// MonitorStatusEvent in MainWindowViewModel.
/// </summary>
public class NewFileArrivedHandler : INotificationHandler<NewFileArrivedEvent>
{
    private readonly ILogger<NewFileArrivedHandler> _logger;

    public NewFileArrivedHandler(ILogger<NewFileArrivedHandler> logger)
    {
        _logger = logger;
    }

    public Task Handle(NewFileArrivedEvent notification, CancellationToken ct)
    {
        _logger.LogInformation(
            "[FileArrived] {Title} EP {Ep} → {Path}",
            notification.SeriesTitle,
            notification.EpisodeNumber,
            notification.FinalFilePath);
        return Task.CompletedTask;
    }
}
