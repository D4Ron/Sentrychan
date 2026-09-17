namespace Sentrychan.Core.Interfaces;

public interface IDownloadFolderWatcher
{
    bool IsWatching { get; }
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
}
