using Refit;
using Sentrychan.Core.Models.Api;

namespace Sentrychan.Core.Services.Api;

public interface IJikanApi
{
    [Get("/anime?q={query}&limit={limit}&order_by=popularity&status={status}")]
    Task<JikanResponse<List<AnimeResult>>> SearchAnimeAsync(
        string query, int limit, string? status = null,
        CancellationToken ct = default);

    [Get("/anime?q={query}&limit={limit}&order_by=popularity")]
    Task<JikanResponse<List<AnimeResult>>> SearchAnimeAllStatusAsync(
        string query, int limit,
        CancellationToken ct = default);

    [Get("/anime/{malId}")]
    Task<JikanResponse<AnimeResult>> GetAnimeByIdAsync(
        int malId,
        CancellationToken ct = default);

    [Get("/anime/{malId}/characters")]
    Task<JikanResponse<List<AnimeCharacter>>> GetAnimeCharactersAsync(
        int malId,
        CancellationToken ct = default);

    [Get("/seasons/{year}/{season}")]
    Task<JikanResponse<List<AnimeResult>>> GetSeasonalAnimeAsync(
        int year, string season,
        CancellationToken ct = default);

    [Get("/schedules?filter={day}&limit=25&sfw=true")]
    Task<JikanResponse<List<AnimeResult>>> GetSchedulesAsync(
        string day,
        CancellationToken ct = default);

    [Get("/anime/{malId}/recommendations")]
    Task<JikanResponse<List<AnimeRecommendation>>> GetAnimeRecommendationsAsync(
        int malId,
        CancellationToken ct = default);

    [Get("/users/{username}/animelist?status={status}")]
    Task<JikanResponse<List<AnimeListEntry>>> GetUserAnimeListAsync(
        string username, string status,
        CancellationToken ct = default);
}