using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services;

namespace Sentrychan.Tests;

public class MangaFilterTests
{
    private static FilterList Sample() => new(
        new HeaderFilter("Only one header"),
        new SelectFilter("Status", ["Any", "Ongoing", "Completed"]),
        new TextFilter("Author"),
        new CheckBoxFilter("Has chapters"),
        new SeparatorFilter(),
        new SortFilter("Sort", ["Title", "Updated"], new SortSelection(1, false)),
        new GroupFilter("Genres", [new TriStateFilter("Action"), new TriStateFilter("Romance")]));

    [Fact]
    public void A_clone_is_independent_of_the_sources_instance()
    {
        var original = Sample();
        var edited = original.Clone();
        ((SelectFilter)edited[1]).State = 2;
        ((TextFilter)edited[2]).State = "someone";
        ((GroupFilter)edited[6]).Filters.OfType<TriStateFilter>().First().State = TriState.Exclude;

        Assert.False(original.IsChanged);
        Assert.True(edited.IsChanged);
        Assert.Equal("Completed", ((SelectFilter)edited[1]).Selected);
    }

    [Fact]
    public void Reset_returns_to_the_initial_state_not_to_defaults()
    {
        var list = Sample();
        var sort = (SortFilter)list[5];
        sort.State = new SortSelection(0, true);
        ((CheckBoxFilter)list[3]).State = true;
        list.Reset();
        Assert.Equal(new SortSelection(1, false), sort.State);
        Assert.False(list.IsChanged);
    }

    [Fact]
    public void Tri_state_cycles_like_a_tri_state_checkbox()
    {
        var f = new TriStateFilter("Action");
        f.Cycle(); Assert.Equal(TriState.Include, f.State);
        f.Cycle(); Assert.Equal(TriState.Exclude, f.State);
        f.Cycle(); Assert.Equal(TriState.Ignore, f.State);
    }

    [Fact]
    public void Find_looks_inside_groups()
    {
        Assert.NotNull(Sample().Find<TriStateFilter>("Romance"));
        Assert.Null(Sample().Find<TriStateFilter>("Horror"));
    }
}

/// <summary>A source written against the first version of the contract: only the v1 members.</summary>
internal sealed class V1Source(bool browse) : IMangaSourceService
{
    public string SourceName => "Old Pack Source";
    public bool SupportsBrowse => browse;
    public List<(string Kind, int Limit, int Page)> Calls { get; } = [];

    private static List<MangaSearchResult> Results(int n) => Enumerable.Range(1, n)
        .Select(i => new MangaSearchResult($"id{i}", $"Title {i}", null, null, "https://example.test/c.jpg", null, null, null, [], false))
        .ToList();

    public Task<List<MangaSearchResult>> SearchAsync(string query, int limit = 20, int page = 1, CancellationToken ct = default)
    {
        Calls.Add(("search:" + query, limit, page));
        return Task.FromResult(Results(page == 1 ? limit : 3));
    }

    public Task<List<MangaSearchResult>> BrowseAsync(string category, int limit = 24, int page = 1, CancellationToken ct = default)
    {
        Calls.Add((category, limit, page));
        return Task.FromResult(Results(limit));
    }

    public Task<MangaSearchResult?> GetDetailsAsync(string sourceId, CancellationToken ct = default) => Task.FromResult<MangaSearchResult?>(null);
    public Task<List<MangaChapterInfo>> GetChaptersAsync(string sourceId, string language = "en", CancellationToken ct = default) => Task.FromResult(new List<MangaChapterInfo>());
    public Task<List<string>> GetPageUrlsAsync(string chapterSourceId, bool dataSaver = false, CancellationToken ct = default) => Task.FromResult(new List<string>());
    public string GetChapterWebUrl(string chapterSourceId) => "https://example.test/" + chapterSourceId;
}

public class MangaSourceV1FallbackTests
{
    [Fact]
    public void Info_is_derived_from_the_v1_members()
    {
        IMangaSourceService s = new V1Source(browse: true);
        Assert.Equal(new MangaSourceInfo("Old Pack Source", "Old Pack Source", "all", false, true), s.Info);
        Assert.Empty(s.GetFilterList());
    }

    [Fact]
    public async Task Popular_and_latest_fall_back_to_browse()
    {
        var src = new V1Source(browse: true);
        IMangaSourceService s = src;
        var popular = await s.GetPopularAsync(2);
        var latest = await s.GetLatestAsync(1);
        Assert.True(popular.HasNextPage);
        Assert.Equal(IMangaSourceService.DefaultPageSize, latest.Items.Count);
        Assert.Equal([("Popular", 24, 2), ("Latest", 24, 1)], src.Calls);
    }

    [Fact]
    public async Task Without_browse_there_is_nothing_to_list()
    {
        IMangaSourceService s = new V1Source(browse: false);
        Assert.Same(MangaPage.Empty, await s.GetPopularAsync(1));
        Assert.Same(MangaPage.Empty, await s.GetLatestAsync(1));
    }

    [Fact]
    public async Task Filtered_search_falls_back_to_the_query_search()
    {
        var src = new V1Source(browse: false);
        IMangaSourceService s = src;
        var first = await s.SearchAsync("frieren", 1, FilterList.Empty);
        var second = await s.SearchAsync("frieren", 2, FilterList.Empty);
        Assert.True(first.HasNextPage);
        Assert.False(second.HasNextPage);
        Assert.Equal(("search:frieren", 24, 2), src.Calls[1]);
    }
}

public sealed class LocalMangaSourceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sentrychan-localmanga-").FullName;
    private string Manga => Path.Combine(_root, "manga");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class Factory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
    }

    private LocalMangaSourceService Source(int titles)
    {
        for (var i = 1; i <= titles; i++)
        {
            var chapter = Path.Combine(Manga, $"Title {i:00}", "Chapter 1");
            Directory.CreateDirectory(chapter);
            File.WriteAllText(Path.Combine(chapter, "001.jpg"), "x");
            Directory.SetLastWriteTimeUtc(chapter, new DateTime(2026, 1, 1).AddDays(i));
            // The title folder counts too (the newest of it and its chapters). Left at "now", the
            // order came down to creation timing — which on Windows can tie within a tick.
            Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(chapter)!, new DateTime(2026, 1, 1).AddDays(i));
        }
        var factory = new Factory(Path.Combine(_root, "t.db"));
        using (var db = factory.CreateDbContext())
        {
            db.Database.Migrate();
            db.AppConfigs.Add(new AppConfig { Key = LocalMangaSourceService.RootConfigKey, Value = Manga });
            db.SaveChanges();
        }
        return new LocalMangaSourceService(factory, NullLogger<LocalMangaSourceService>.Instance);
    }

    [Fact]
    public async Task Popular_pages_through_everything_by_title()
    {
        var s = Source(30);
        var p1 = await s.GetPopularAsync(1);
        var p2 = await s.GetPopularAsync(2);
        Assert.Equal(24, p1.Items.Count);
        Assert.True(p1.HasNextPage);
        Assert.Equal(6, p2.Items.Count);
        Assert.False(p2.HasNextPage);
        Assert.Equal("Title 01", p1.Items[0].Title);
    }

    [Fact]
    public async Task Latest_is_the_most_recently_changed_first()
    {
        var s = Source(3);
        Assert.True(s.Info.SupportsLatest);
        var latest = await s.GetLatestAsync(1);
        Assert.Equal(["Title 03", "Title 02", "Title 01"], latest.Items.Select(i => i.Title));
    }

    [Fact]
    public async Task Search_honours_the_query_and_the_sort_filter()
    {
        var s = Source(12);
        var filters = s.GetFilterList().Clone();
        filters.Find<SortFilter>("Sort by")!.State = new SortSelection(0, Ascending: false);
        var page = await s.SearchAsync("Title 1", 1, filters);
        Assert.Equal(["Title 12", "Title 11", "Title 10"], page.Items.Select(i => i.Title));
    }
}

public class MangaSourceRegistryTests
{
    [Fact]
    public void Find_matches_id_or_name_and_never_falls_back()
    {
        IMangaSourceRegistry registry = new MangaSourceRegistry([new V1Source(true)]);
        Assert.NotNull(registry.Find("Old Pack Source"));
        Assert.NotNull(registry.Find("old pack source"));
        Assert.Null(registry.Find("Missing"));
        Assert.Null(registry.Find(null));
    }
}
