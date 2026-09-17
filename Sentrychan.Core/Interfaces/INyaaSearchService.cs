using Sentrychan.Core.Models;

namespace Sentrychan.Core.Interfaces;

public interface INyaaSearchService
{
    /// <summary>
    /// Search nyaa.si for releases matching the query.
    /// Uses category 1_2 (Anime — English-translated) by default.
    /// In secret mode, uses category 2_2 (Adult — English-translated).
    /// </summary>
    Task<List<NyaaResult>> SearchAsync(
        string query,
        string? quality = null,
        bool secretMode = false,
        CancellationToken ct = default);

    /// <summary>
    /// Targeted search for a specific series + episode number.
    /// Performs one RSS search and returns all results for that episode,
    /// ordered by score descending (best match first).
    /// </summary>
    Task<List<NyaaResult>> FindEpisodeAsync(
        string seriesTitle,
        int episodeNumber,
        string? quality = null,
        CancellationToken ct = default);
}
