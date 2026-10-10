using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Sentrychan.Core.Services.AniList;

/// <summary>
/// AniList's GraphQL API: show details, search, seasons, airing times and season links. Free, no
/// key, and kept current — unlike the offline database (frozen since its project was archived) and
/// Jikan (whose requests often time out). Its rate limit is low (30 a minute while it's degraded,
/// 90 otherwise), so everything goes through one gate that reads the limit headers, waits out a
/// 429, and lets what the user is waiting for go ahead of background work.
/// </summary>
public sealed class AniListClient
{
    public const string Endpoint = "https://graphql.anilist.co";

    /// <summary>Everything the app reads about a show.</summary>
    internal const string MediaFields = """
        id idMal isAdult title { romaji english native } synonyms format status episodes duration season seasonYear source
        startDate { year month day } endDate { year month day } coverImage { extraLarge large }
        description(asHtml: false) averageScore popularity genres studios(isMain: true) { nodes { name } }
        nextAiringEpisode { episode airingAt }
        relations { edges { relationType node { id idMal type format } } }
        """;

    /// <summary>What the title database needs: names, kind, status, length, season and links — no text or art.</summary>
    internal const string CatalogFields = """
        id idMal isAdult title { romaji english native } synonyms format status episodes season seasonYear popularity countryOfOrigin
        startDate { year month day } endDate { year month day } coverImage { large }
        relations { edges { relationType node { id idMal type format } } }
        """;

    private static readonly TimeSpan MinSpacing = TimeSpan.FromMilliseconds(700);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly ILogger<AniListClient> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _notBefore = DateTime.MinValue;
    private int _waitingInFront;

    public AniListClient(ILogger<AniListClient> log, HttpMessageHandler? handler = null)
    {
        _log = log;
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Sentrychan/2.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Runs a query and returns its <c>data</c>. Throws <see cref="AniListException"/> when AniList
    /// answers with errors, and the usual network exceptions when it can't be reached.
    /// </summary>
    /// <param name="background">Work nobody is waiting on: it waits while a foreground request is queued.</param>
    public async Task<JsonNode?> QueryAsync(string query, object? variables = null, bool background = false, CancellationToken ct = default)
    {
        if (!background) Interlocked.Increment(ref _waitingInFront);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                while (background && Volatile.Read(ref _waitingInFront) > 0)
                    await Task.Delay(250, ct);

                await _gate.WaitAsync(ct);
                if (background && Volatile.Read(ref _waitingInFront) > 0) { _gate.Release(); continue; }
                try
                {
                    var wait = _notBefore - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                    return await SendAsync(query, variables, ct);
                }
                catch (RateLimitedException limited)
                {
                    _notBefore = DateTime.UtcNow + limited.RetryAfter;
                    if (attempt >= 3) throw new AniListException("AniList is rate limiting this app; try again in a minute");
                    _log.LogInformation("[AniList] rate limited — waiting {Seconds:F0}s", limited.RetryAfter.TotalSeconds);
                }
                finally { _gate.Release(); }
            }
        }
        finally
        {
            if (!background) Interlocked.Decrement(ref _waitingInFront);
        }
    }

    private async Task<JsonNode?> SendAsync(string query, object? variables, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { query, variables });
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using var response = await _http.SendAsync(request, ct);

        _notBefore = DateTime.UtcNow + MinSpacing;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new RateLimitedException(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60));
        // Out of requests for this minute: let the next one wait for the window to reset, not hit a 429.
        if (Header(response, "X-RateLimit-Remaining") is <= 1 && Header(response, "X-RateLimit-Reset") is { } reset)
            _notBefore = DateTimeOffset.FromUnixTimeSeconds(reset).UtcDateTime + TimeSpan.FromSeconds(1);

        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? root;
        try { root = JsonNode.Parse(text); }
        catch (JsonException) { throw new AniListException($"AniList answered {(int)response.StatusCode} with something that isn't JSON"); }

        if (root?["errors"] is JsonArray { Count: > 0 } errors)
        {
            var message = string.Join("; ", errors.Select(e => e?["message"]?.GetValue<string>()).Where(m => m != null));
            throw new AniListException($"AniList: {message}");
        }
        if (!response.IsSuccessStatusCode) throw new AniListException($"AniList answered {(int)response.StatusCode}");
        return root?["data"];
    }

    private static long? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && long.TryParse(values.FirstOrDefault(), out var v) ? v : null;

    private sealed class RateLimitedException(TimeSpan retryAfter) : Exception
    {
        public TimeSpan RetryAfter { get; } = retryAfter < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : retryAfter;
    }

    // ── Queries ────────────────────────────────────────────────────

    /// <summary>
    /// Every page of a <c>Page { media(...) }</c> query. <paramref name="filter"/> is the media
    /// arguments, using <c>$page</c> and any variables given.
    /// </summary>
    public async Task<List<AniListMedia>> PagesAsync(string filter, string variableDecls, Dictionary<string, object?> variables,
        string fields, int maxPages, bool background, CancellationToken ct)
    {
        var query = $"query ($page: Int{variableDecls}) {{ Page(page: $page, perPage: 50) {{ pageInfo {{ hasNextPage }} media({filter}) {{ {fields} }} }} }}";
        var all = new List<AniListMedia>();
        for (var page = 1; page <= maxPages; page++)
        {
            variables["page"] = page;
            var data = await QueryAsync(query, variables, background, ct);
            var items = data?["Page"]?["media"]?.Deserialize<List<AniListMedia>>(Json) ?? [];
            all.AddRange(items);
            if (data?["Page"]?["pageInfo"]?["hasNextPage"]?.GetValue<bool>() != true || items.Count == 0) break;
        }
        return all;
    }

    /// <summary>The shows AniList links to these MyAnimeList ids — possibly several per id (see <see cref="AniListMedia.IdMal"/>).</summary>
    public async Task<List<AniListMedia>> ByMalIdsAsync(IReadOnlyCollection<int> malIds, bool background = false, CancellationToken ct = default)
    {
        var all = new List<AniListMedia>();
        foreach (var chunk in malIds.Where(i => i > 0).Distinct().Chunk(50))
            all.AddRange(await PagesAsync("idMal_in: $ids, type: ANIME", ", $ids: [Int]",
                new() { ["ids"] = chunk }, MediaFields, 4, background, ct));
        return all;
    }

    public async Task<List<AniListMedia>> ByIdsAsync(IReadOnlyCollection<int> ids, bool background = false, CancellationToken ct = default,
        string fields = MediaFields)
    {
        var all = new List<AniListMedia>();
        foreach (var chunk in ids.Where(i => i > 0).Distinct().Chunk(50))
            all.AddRange(await PagesAsync("id_in: $ids, type: ANIME", ", $ids: [Int]",
                new() { ["ids"] = chunk }, fields, 2, background, ct));
        return all;
    }

    /// <summary>A title search, best match first. <paramref name="status"/> is an AniList status (RELEASING…).</summary>
    public async Task<List<AniListMedia>> SearchAsync(string search, int perPage = 25, string? status = null, bool background = false, CancellationToken ct = default)
    {
        var filter = "search: $search, type: ANIME, sort: SEARCH_MATCH" + (status != null ? ", status: $status" : "");
        var decls = status != null ? "$search: String, $status: MediaStatus" : "$search: String";
        var query = $"query ({decls}) {{ Page(perPage: {Math.Clamp(perPage, 1, 50)}) {{ media({filter}) {{ {MediaFields} }} }} }}";
        var data = await QueryAsync(query, status != null ? new { search, status } : new { search }, background, ct);
        return data?["Page"]?["media"]?.Deserialize<List<AniListMedia>>(Json) ?? [];
    }

    /// <summary>A season's shows, most popular first. <paramref name="season"/>: winter, spring, summer or fall.</summary>
    public Task<List<AniListMedia>> SeasonAsync(int year, string season, int maxPages = 3, CancellationToken ct = default) =>
        PagesAsync("season: $season, seasonYear: $year, type: ANIME, sort: POPULARITY_DESC", ", $season: MediaSeason, $year: Int",
            new() { ["season"] = season.ToUpperInvariant(), ["year"] = year }, MediaFields, maxPages, false, ct);

    /// <summary>Episodes airing between two moments, in airing order.</summary>
    public async Task<List<AniListAiringSchedule>> AiringBetweenAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var query = $"query ($page: Int, $from: Int, $to: Int) {{ Page(page: $page, perPage: 50) {{ pageInfo {{ hasNextPage }} " +
                    $"airingSchedules(airingAt_greater: $from, airingAt_lesser: $to, sort: TIME) {{ episode airingAt media {{ {CatalogFields} }} }} }} }}";
        var all = new List<AniListAiringSchedule>();
        for (var page = 1; page <= 6; page++)
        {
            var data = await QueryAsync(query, new { page, from = from.ToUnixTimeSeconds(), to = to.ToUnixTimeSeconds() }, false, ct);
            var items = data?["Page"]?["airingSchedules"]?.Deserialize<List<AniListAiringSchedule>>(Json) ?? [];
            all.AddRange(items);
            if (data?["Page"]?["pageInfo"]?["hasNextPage"]?.GetValue<bool>() != true || items.Count == 0) break;
        }
        return all;
    }

    /// <summary>The shows AniList users recommend alongside this one, best rated first.</summary>
    public async Task<List<AniListMedia>> RecommendationsAsync(int aniListId, CancellationToken ct = default)
    {
        var query = $"query ($id: Int) {{ Media(id: $id, type: ANIME) {{ recommendations(perPage: 12, sort: RATING_DESC) {{ nodes {{ mediaRecommendation {{ {MediaFields} }} }} }} }} }}";
        var data = await QueryAsync(query, new { id = aniListId }, false, ct);
        return data?["Media"]?["recommendations"]?["nodes"] is JsonArray nodes
            ? nodes.Select(n => n?["mediaRecommendation"]?.Deserialize<AniListMedia>(Json)).OfType<AniListMedia>().ToList()
            : [];
    }
}

public sealed class AniListException(string message) : Exception(message);
