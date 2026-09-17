using Sentrychan.Core.Models.Api;

namespace Sentrychan.Core.Interfaces;

public interface IAniDbApiService
{
    Task<List<AnimeResult>> SearchAsync(string query, CancellationToken ct = default);
    Task<AnimeResult?> GetByIdAsync(int anidbId, CancellationToken ct = default);
}
