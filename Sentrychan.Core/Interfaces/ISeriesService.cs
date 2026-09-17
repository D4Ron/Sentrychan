using Sentrychan.Core.Models;

namespace Sentrychan.Core.Interfaces;

public interface ISeriesService
{
    Task<List<Series>> GetAllAsync(CancellationToken ct = default);
    Task<Series?> GetByMalIdAsync(int malId, CancellationToken ct = default);
    Task<Series> AddAsync(Series series, string? imageUrl = null, CancellationToken ct = default);
    Task<bool> RemoveAsync(int malId, CancellationToken ct = default);
    Task UpdateLastEpisodeAsync(int malId, int episodeNumber, CancellationToken ct = default);
    Task AddAliasAsync(int malId, string alias, CancellationToken ct = default);
    Task UpdatePosterPathAsync(int seriesId, string posterPath, CancellationToken ct = default);
}