using Sentrychan.Core.Models.Api;

namespace Sentrychan.Core.Interfaces;

public interface IAnimeApiService
{
    Task<List<AnimeResult>> SearchAnimeAsync(string query, string? status = null,
        int limit = 12, CancellationToken ct = default);

    Task<AnimeResult?> GetAnimeByIdAsync(int malId,
        CancellationToken ct = default);

    Task<List<AnimeCharacter>> GetAnimeCharactersAsync(int malId,
        CancellationToken ct = default);

    Task<List<AnimeResult>> GetSeasonalAnimeAsync(int year, string season,
        CancellationToken ct = default);

    /// <summary>Anime broadcasting on a given weekday (Jikan /schedules). Lowercase day, e.g. "monday".</summary>
    Task<List<AnimeResult>> GetScheduleAsync(string day,
        CancellationToken ct = default);

    Task<List<AnimeResult>> GetAnimeRecommendationsAsync(int malId,
        CancellationToken ct = default);

    Task<string?> GetCachedImagePathAsync(string imageUrl,
        CancellationToken ct = default);

    Task<List<AnimeResult>> GetUserWatchingAsync(string username,
        CancellationToken ct = default);
}