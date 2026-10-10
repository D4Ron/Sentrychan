using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Sentrychan.Core.Models.Api;
using Sentrychan.Core.Services;
using Sentrychan.Core.Services.AniList;

namespace Sentrychan.Tests;

public sealed class AniListTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sentrychan-anilist-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── A miniature offline database, frozen with stale entries ───────────────

    private static object Entry(string title, string type, int episodes, string status, string season, int year,
        int? mal = null, int? aniList = null, params string[] synonyms) => new
    {
        sources = new[]
        {
            mal is { } m ? $"https://myanimelist.net/anime/{m}" : null,
            aniList is { } a ? $"https://anilist.co/anime/{a}" : null,
        }.OfType<string>().ToArray(),
        title, type, episodes, status, synonyms,
        animeSeason = new { season, year },
        relatedAnime = Array.Empty<string>(),
    };

    private TitleResolverService Frozen()
    {
        var data = new[]
        {
            // A one-episode first part the database still had as airing.
            Entry("Ball Run", "ONA", 1, "ONGOING", "WINTER", 2026, mal: 100, aniList: 1000),
            // Its sequel, on MyAnimeList only — and separately, AniList's own entry for it.
            Entry("Ball Run 2nd Stage", "ONA", 11, "UPCOMING", "SUMMER", 2026, mal: 200, synonyms: "Ball Run: Second Stage"),
            Entry("Ball Run - 2nd STAGE", "ONA", 0, "UPCOMING", "FALL", 2026, aniList: 2000),
            // A season that started after the freeze, with MAL's one-episode placeholder.
            Entry("Kusu Diaries 3rd Season", "TV", 1, "UPCOMING", "FALL", 2026, mal: 300, aniList: 3000),
            Entry("Kusu Diaries", "TV", 24, "FINISHED", "FALL", 2023, mal: 290, aniList: 2900),
            // Finished, with MAL's length.
            Entry("Long Show", "TV", 24, "FINISHED", "SPRING", 2025, mal: 500, aniList: 5000),
        };
        var file = Path.Combine(_dir, "db.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new { lastUpdate = "2026-07-04", data }));
        var r = new TitleResolverService(NullLogger<TitleResolverService>.Instance);
        r.LoadFile(file);
        return r;
    }

    private static AniListMedia Media(int id, int? idMal, string romaji, string format, string status, int? episodes,
        string season, int year, (string Type, int Id, int? Mal)[]? relations = null, params string[] synonyms) => new()
    {
        Id = id, IdMal = idMal, Format = format, Status = status, Episodes = episodes, Season = season, SeasonYear = year,
        Title = new AniListTitle { Romaji = romaji },
        Synonyms = synonyms.ToList(),
        Relations = new AniListRelations
        {
            Edges = (relations ?? []).Select(r => new AniListRelationEdge
            {
                RelationType = r.Type, Node = new AniListRelationNode { Id = r.Id, IdMal = r.Mal, Type = "ANIME" },
            }).ToList(),
        },
    };

    private static List<AniListMedia> TopUp() =>
    [
        Media(1000, 100, "Ball Run - 1st STAGE", "ONA", "FINISHED", 1, "WINTER", 2026, [("SEQUEL", 2000, 100)]),
        // AniList gives the sequel its first part's MyAnimeList id.
        Media(2000, 100, "Ball Run - 2nd STAGE", "ONA", "RELEASING", 11, "FALL", 2026, [("PREQUEL", 1000, 100)], "Ball Run 2nd Stage"),
        Media(3000, 300, "Kusu Diaries 3rd Season", "TV", "RELEASING", 12, "FALL", 2026),
        Media(5000, 500, "Long Show", "TV", "FINISHED", 26, "SPRING", 2025),
        Media(6000, 600, "Brand New Show", "TV", "RELEASING", 12, "FALL", 2026),
    ];

    [Fact]
    public void The_top_up_refreshes_stale_entries_and_adds_new_shows()
    {
        var r = Frozen();
        Assert.Equal("UPCOMING", r.GetByMalId(300)!.Status);

        r.ApplySupplement(TopUp());

        Assert.Equal(("ONGOING", 12), (r.GetByMalId(300)!.Status, r.GetByMalId(300)!.Episodes));
        Assert.Equal(("FINISHED", 1), (r.GetByMalId(100)!.Status, r.GetByMalId(100)!.Episodes));
        Assert.Equal(600, r.ResolveTitle("Brand New Show")!.MalId);
        // A finished show keeps the length the database had (MAL's split), whatever AniList counts.
        Assert.Equal(24, r.GetByMalId(500)!.Episodes);
    }

    [Fact]
    public void A_show_AniList_links_to_the_wrong_id_is_tied_to_its_own_entry_by_name()
    {
        var r = Frozen();
        r.ApplySupplement(TopUp());

        Assert.Equal(2000, r.AniListIdForMal(200));
        Assert.Equal(200, r.MalIdFor(new AniListMedia { Id = 2000, IdMal = 100 }));
        Assert.Equal(100, r.MalIdFor(new AniListMedia { Id = 1000, IdMal = 100 }));
        Assert.Equal(("ONGOING", "FALL"), (r.GetByMalId(200)!.Status, r.GetByMalId(200)!.AnimeSeason));

        // AniList's prequel link brings in the one-episode first part, which groups count.
        Assert.Equal([100, 200], r.GetSeasonChain(200).Select(a => a.MalId));
        var series = new Series { MalId = 200, Title = "Ball Run 2nd Stage", TotalEpisodes = 11 };
        Assert.Equal((ReleaseVerdict.Yes, 3), ReleaseMatcher.Match(r, "[Grp] Ball Run - 04 (1080p).mkv", series));
    }

    [Fact]
    public void Applying_the_same_top_up_again_changes_nothing()
    {
        var r = Frozen();
        r.ApplySupplement(TopUp());
        var once = Enumerable.Range(1, 700).Select(r.GetByMalId).OfType<ResolvedAnime>().ToList();
        r.ApplySupplement(TopUp());
        Assert.Equal(once, Enumerable.Range(1, 700).Select(r.GetByMalId).OfType<ResolvedAnime>().ToList());
        Assert.Equal(2000, r.AniListIdForMal(200));
    }

    [Fact]
    public void The_database_links_it_already_had_are_kept()
    {
        var r = Frozen();
        Assert.Equal(3000, r.AniListIdForMal(300));
        Assert.Null(r.AniListIdForMal(200));
        Assert.Equal(new DateTime(2026, 7, 4), r.SnapshotDate!.Value.Date);
        Assert.Equal(new DateTime(2026, 4, 5), r.CatalogSince);
        Assert.Contains(1000, r.UnsettledAniListIds);
        Assert.DoesNotContain(5000, r.UnsettledAniListIds);
    }

    // ── Mapping ─────────────────────────────────────────────────────

    [Fact]
    public void An_AniList_show_reads_like_a_Jikan_one()
    {
        var m = Media(3000, 300, "Kusu Diaries 3rd Season", "TV", "RELEASING", 12, "FALL", 2026,
            [("PREQUEL", 2900, 290), ("SIDE_STORY", 7000, null)], "The Diaries III");
        m.Title.English = "The Kusu Diaries Season 3";
        m.Description = "A <i>medicine</i> girl.<br><br>Season 3 &amp; more.";
        m.AverageScore = 86;
        m.NextAiringEpisode = new AniListAiring { Episode = 3, AiringAt = new DateTimeOffset(2026, 10, 16, 14, 0, 0, TimeSpan.FromHours(9)).ToUnixTimeSeconds() };

        var a = AniListMapper.ToAnimeResult(m, 300);

        Assert.Equal((300, "Kusu Diaries 3rd Season", "The Kusu Diaries Season 3"), (a.MalId, a.Title, a.TitleEnglish));
        Assert.Equal((AiringStatusNormalizer.Airing, "TV", 12, 8.6), (a.Status, a.Type, a.Episodes, a.Score));
        Assert.Equal(("fall", 2026), (a.Season, a.Year));
        Assert.Equal("A medicine girl.\n\nSeason 3 & more.", a.Synopsis);
        Assert.Equal(("Fridays", "14:00"), (a.Broadcast!.Day, a.Broadcast.Time));
        Assert.Contains(a.Titles, t => t is { Type: "Synonym", Title: "The Diaries III" });
        var prequel = Assert.Single(a.Relations);
        Assert.Equal(("Prequel", 290), (prequel.Relation, prequel.Entry.Single().MalId));
    }

    [Theory]
    [InlineData("RELEASING", "Currently Airing", "ONGOING")]
    [InlineData("HIATUS", "Currently Airing", "ONGOING")]
    [InlineData("FINISHED", "Finished Airing", "FINISHED")]
    [InlineData("NOT_YET_RELEASED", "Not yet aired", "UPCOMING")]
    [InlineData("CANCELLED", "Finished Airing", "UNKNOWN")]
    public void Statuses_map_to_both_vocabularies(string aniList, string jikan, string catalog)
    {
        Assert.Equal(jikan, AniListMapper.JikanStatus(aniList));
        Assert.Equal(catalog, AniListMapper.CatalogStatus(aniList));
    }

    // ── Linking by name ─────────────────────────────────────────────

    [Fact]
    public void A_search_result_is_taken_only_when_it_is_clearly_the_show()
    {
        var r = Frozen();
        var shows = new AniListShows(new AniListClient(NullLogger<AniListClient>.Instance), r,
            NullLogger<AniListShows>.Instance, Path.Combine(_dir, "links.json"));
        var first = Media(1000, 100, "Ball Run - 1st STAGE", "ONA", "FINISHED", 1, "WINTER", 2026);
        var second = Media(2000, 100, "Ball Run - 2nd STAGE", "ONA", "RELEASING", 11, "FALL", 2026);

        Assert.Same(second, shows.PickByName(200, "Ball Run 2nd Stage", [first, second]));
        // The first part is held by its own id, so it's never taken for another show.
        Assert.Null(shows.PickByName(200, "Ball Run 2nd Stage", [first]));
        Assert.Null(shows.PickByName(200, "Ball Run 2nd Stage", [Media(9000, null, "Ball Run Stories", "ONA", "RELEASING", 6, "FALL", 2026)]));
        Assert.Equal(0, AniListShows.Similarity("Show 2nd Season", "Show 3rd Season"));
    }

    // ── The client ──────────────────────────────────────────────────

    private sealed class Script(params Func<HttpResponseMessage>[] answers) : HttpMessageHandler
    {
        public int Calls;
        public readonly List<string> Bodies = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return answers[Math.Min(Calls++, answers.Length - 1)]();
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task A_rate_limit_answer_is_waited_out_and_retried()
    {
        var script = new Script(
            () => { var r = Json("{}", HttpStatusCode.TooManyRequests); r.Headers.RetryAfter = new(TimeSpan.FromSeconds(1)); return r; },
            () => Json("""{"data":{"Page":{"pageInfo":{"hasNextPage":false},"media":[{"id":1,"idMal":2,"title":{"romaji":"A"}}]}}}"""));
        var client = new AniListClient(NullLogger<AniListClient>.Instance, script);

        var media = await client.ByMalIdsAsync([2]);

        Assert.Equal(2, script.Calls);
        Assert.Equal(1, Assert.Single(media).Id);
        Assert.Contains("idMal_in", script.Bodies[0]);
    }

    [Fact]
    public async Task GraphQL_errors_are_reported_not_swallowed()
    {
        var client = new AniListClient(NullLogger<AniListClient>.Instance,
            new Script(() => Json("""{"errors":[{"message":"Invalid query"}],"data":null}""", HttpStatusCode.BadRequest)));
        var ex = await Assert.ThrowsAsync<AniListException>(() => client.SearchAsync("x"));
        Assert.Contains("Invalid query", ex.Message);
    }

    // ── The library refresh ─────────────────────────────────────────

    private sealed class Factory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
    }

    private sealed class NoJikan : IAnimeApiService
    {
        public int Asked;
        public Task<List<AnimeResult>> SearchAnimeAsync(string q, string? s = null, int l = 12, CancellationToken ct = default) => Task.FromResult(new List<AnimeResult>());
        public Task<AnimeResult?> GetAnimeByIdAsync(int malId, CancellationToken ct = default) { Asked++; return Task.FromResult<AnimeResult?>(null); }
        public Task<List<AnimeCharacter>> GetAnimeCharactersAsync(int malId, CancellationToken ct = default) => Task.FromResult(new List<AnimeCharacter>());
        public Task<List<AnimeResult>> GetSeasonalAnimeAsync(int y, string s, CancellationToken ct = default) => Task.FromResult(new List<AnimeResult>());
        public Task<List<AnimeResult>> GetScheduleAsync(string d, CancellationToken ct = default) => Task.FromResult(new List<AnimeResult>());
        public Task<List<AnimeResult>> GetAnimeRecommendationsAsync(int m, CancellationToken ct = default) => Task.FromResult(new List<AnimeResult>());
        public Task<string?> GetCachedImagePathAsync(string u, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<List<AnimeResult>> GetUserWatchingAsync(string u, CancellationToken ct = default) => Task.FromResult(new List<AnimeResult>());
    }

    [Fact]
    public async Task The_library_refresh_replaces_placeholders_but_not_settled_counts_or_folder_names()
    {
        var factory = new Factory(Path.Combine(_dir, "lib.db"));
        await using (var db = factory.CreateDbContext())
        {
            db.Database.Migrate();
            db.Series.AddRange(
                new Series { Title = "Kusu Diaries 3rd Season", MalId = 300, TotalEpisodes = 1, AiringStatus = "Not yet aired", Year = 2026 },
                new Series { Title = "Long Show", MalId = 500, TotalEpisodes = 24, AiringStatus = "Finished Airing", Year = 2024 },
                new Series { Title = "Ball Run 2nd Stage", MalId = 200, TotalEpisodes = 11, AiringStatus = "Not yet aired" });
            await db.SaveChangesAsync();
        }

        // AniList's answers: the two it links by MAL id, then by AniList id the one the database linked by name.
        var byId = """
            {"data":{"Page":{"pageInfo":{"hasNextPage":false},"media":[
            {"id":3000,"idMal":300,"title":{"romaji":"Kusu Diaries 3rd Season"},"format":"TV","status":"RELEASING","episodes":12,"season":"FALL","seasonYear":2026},
            {"id":5000,"idMal":500,"title":{"romaji":"Long Show"},"format":"TV","status":"FINISHED","episodes":26,"season":"SPRING","seasonYear":2025},
            {"id":2000,"idMal":100,"title":{"romaji":"Ball Run - 2nd STAGE"},"format":"ONA","status":"RELEASING","episodes":11,"season":"FALL","seasonYear":2026}]}}}
            """;
        var r = Frozen();
        r.ApplySupplement(TopUp());
        var shows = new AniListShows(new AniListClient(NullLogger<AniListClient>.Instance, new Script(() => Json(byId))), r,
            NullLogger<AniListShows>.Instance, Path.Combine(_dir, "links.json"));
        var jikan = new NoJikan();
        var refresher = new AiringStatusRefreshService(factory, jikan, NullLogger<AiringStatusRefreshService>.Instance, shows);

        var report = await refresher.RefreshAsync();

        // Corrected: Kusu's placeholder count, and the kind none of them had recorded.
        Assert.Equal((2, 3, 3), (report.Refreshed, report.Corrected, report.Checked));
        Assert.Equal(0, jikan.Asked);
        await using var check = factory.CreateDbContext();
        var s = check.Series.OrderBy(x => x.MalId).ToDictionary(x => x.MalId);
        Assert.Equal(("Currently Airing", 12, 2026), (s[300].AiringStatus, s[300].TotalEpisodes, s[300].Year));
        Assert.Equal(("Currently Airing", 11), (s[200].AiringStatus, s[200].TotalEpisodes));
        Assert.Equal((24, 2024), (s[500].TotalEpisodes, s[500].Year));
        Assert.Equal("ONA", s[200].MediaType);
    }
}
