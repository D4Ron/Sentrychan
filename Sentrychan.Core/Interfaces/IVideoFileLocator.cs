using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sentrychan.Core.Interfaces;

public interface IVideoFileLocator
{
    Task<string?> FindVideoFileAsync(string seriesTitle, int episodeNumber, CancellationToken ct);

    /// <summary>
    /// Every episode 1..<paramref name="maxEpisode"/> that's on disk, and its file. The folders are
    /// read once — asking per episode read them once per episode (170 times for a long show).
    /// </summary>
    async Task<IReadOnlyDictionary<int, string>> FindVideoFilesAsync(string seriesTitle, int maxEpisode, CancellationToken ct)
    {
        var found = new Dictionary<int, string>();
        for (var ep = 1; ep <= maxEpisode; ep++)
            if (await FindVideoFileAsync(seriesTitle, ep, ct) is { } path) found[ep] = path;
        return found;
    }
}
