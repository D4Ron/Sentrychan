using System.Threading;
using System.Threading.Tasks;

namespace Sentrychan.Core.Interfaces;

public interface IVideoFileLocator
{
    Task<string?> FindVideoFileAsync(string seriesTitle, int episodeNumber, CancellationToken ct);
}
