using Microsoft.Extensions.Logging.Abstractions;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models.Api;
using Sentrychan.Core.Services;

namespace Sentrychan.Tests;

public sealed class SeasonFamilyServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sentrychan-families-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private sealed class Resolver : ITitleResolverService
    {
        public bool IsReady => true;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ParsedRelease ParseRelease(string releaseName) => throw new NotSupportedException();
        public ResolvedAnime? ResolveRelease(string releaseName) => null;
        public ResolvedAnime? ResolveTitle(string title, int season = 1) => null;
        public List<ResolvedAnime> Search(string query, int limit = 20) => [];
        public ResolvedAnime? GetByMalId(int malId) => new(malId, $"#{malId}", 12, null, null, 2020, "FALL", "FINISHED", "TV");
    }

    private static AnimeRelation Rel(string relation, params int[] ids) =>
        new() { Relation = relation, Entry = ids.Select(i => new RelationEntry { MalId = i, Type = "anime" }).ToList() };

    // 1 → (sequel) 2 → (sequel) 3, 2 also has a side story (9) and a manga adaptation link.
    private static readonly Dictionary<int, List<AnimeRelation>> Graph = new()
    {
        [1] = [Rel("Sequel", 2), Rel("Adaptation")],
        [2] = [Rel("Prequel", 1), Rel("Sequel", 3), Rel("Side Story", 9)],
        [3] = [Rel("Prequel", 2)],
    };

    [Fact]
    public async Task Sequels_and_prequels_make_one_family_kept_on_disk()
    {
        var calls = 0;
        var file = Path.Combine(_dir, "families.json");
        var service = new SeasonFamilyService((id, _) => { calls++; return Task.FromResult<List<AnimeRelation>?>(Graph.GetValueOrDefault(id, [])); },
            new Resolver(), NullLogger<SeasonFamilyService>.Instance, file);

        await service.EnsureAsync(2);
        Assert.Equal([1, 2, 3], service.FamilyOf(3));
        Assert.Equal(3, calls);
        Assert.False(service.NeedsFetch(1));

        // A new start reads it back without asking again.
        var again = new SeasonFamilyService((_, _) => throw new InvalidOperationException("no network"),
            new Resolver(), NullLogger<SeasonFamilyService>.Instance, file);
        Assert.Equal([1, 2, 3], again.FamilyOf(1));
    }

    [Fact]
    public async Task When_the_API_fails_nothing_is_stored_and_titles_keep_deciding()
    {
        var service = new SeasonFamilyService((_, _) => throw new HttpRequestException("429"),
            new Resolver(), NullLogger<SeasonFamilyService>.Instance, Path.Combine(_dir, "f.json"));
        Assert.Null(await service.EnsureAsync(1));
        Assert.Null(service.FamilyOf(1));
        Assert.True(service.NeedsFetch(1));
    }
}
