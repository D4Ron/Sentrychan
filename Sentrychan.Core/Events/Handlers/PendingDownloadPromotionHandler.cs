using MediatR;
using Sentrychan.Core.Services;

namespace Sentrychan.Core.Events.Handlers;

/// <summary>
/// A finished download frees a slot under the MaxConcurrentDownloads limit —
/// start the oldest held (Pending) job, if any. Runs in the background so the
/// file-arrival pipeline never waits on a new torrent spinning up.
/// </summary>
public class PendingDownloadPromotionHandler : INotificationHandler<NewFileArrivedEvent>
{
    private readonly DownloadQueueManager _queue;

    public PendingDownloadPromotionHandler(DownloadQueueManager queue)
    {
        _queue = queue;
    }

    public Task Handle(NewFileArrivedEvent notification, CancellationToken ct)
    {
        _ = Task.Run(() => _queue.PromotePendingAsync(CancellationToken.None), CancellationToken.None);
        return Task.CompletedTask;
    }
}
