using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Library;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services;

namespace Sentrychan.Tests;

public class LibraryFilingTests
{
    private static string P(params string[] parts) => Path.Combine(parts);

    private static readonly Series S1 = new() { Id = 1, MalId = 101, Title = "Show", Year = 2020, MediaType = "TV", TotalEpisodes = 12 };
    private static readonly Series S2 = new() { Id = 2, MalId = 102, Title = "Show Season 2", Year = 2022, MediaType = "TV", TotalEpisodes = 12 };

    [Fact]
    public void New_downloads_get_the_template_name_in_the_shows_folder()
    {
        var (rel, renamed) = LibraryFiling.Destination(NamingTemplate.Default, S2, [S1, S2],
            "[Grp] Show S2 - 03 (1080p) [ABCD1234].mkv", season: 2, episode: 3);
        Assert.Equal(P("Show (2020)", "Season 02", "Show S02E03.mkv"), rel);
        Assert.True(renamed);
    }

    [Fact]
    public void A_continued_count_is_filed_season_relative()
    {
        var (rel, _) = LibraryFiling.Destination(NamingTemplate.Default, S2, [S1, S2], "[Grp] Show - 13.mkv", 2, 13);
        Assert.Equal(P("Show (2020)", "Season 02", "Show S02E01.mkv"), rel);
    }

    [Fact]
    public void An_uncertain_number_keeps_the_release_name_in_the_template_folder()
    {
        var airing = new Series { Id = 2, MalId = 102, Title = "Show Season 2", Year = 2022, TotalEpisodes = null };
        var (rel, renamed) = LibraryFiling.Destination(NamingTemplate.Default, airing, [S1, airing], "[Grp] Show - 14.mkv", 2, 14);
        Assert.Equal(P("Show (2020)", "Season 02", "[Grp] Show - 14.mkv"), rel);
        Assert.False(renamed);
    }

    [Fact]
    public void Dont_tidy_keeps_the_old_layout()
    {
        var excluded = new Series { Id = 1, Title = "Show", TidyExcluded = true };
        Assert.Equal((P("Show", "Season 1", "[Grp] Show - 05.mkv"), false),
            LibraryFiling.Destination(NamingTemplate.Default, excluded, [excluded], "[Grp] Show - 05.mkv", 1, 5));
    }

    [Fact]
    public void Movies_are_filed_by_title_and_year()
    {
        var movie = new Series { Id = 9, Title = "Show Movie", Year = 2021, MediaType = "Movie" };
        Assert.Equal(P("Show Movie (2021)", "Show Movie (2021).mkv"),
            LibraryFiling.Destination(NamingTemplate.Default, movie, [movie], "[Grp] Show Movie [BD].mkv", 1, null).Relative);
    }
}

public sealed class VideoFileLocatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sentrychan-locate-").FullName;
    private string Lib => Path.Combine(_root, "Library");
    private string Downloads => Path.Combine(_root, "Downloads");

    public VideoFileLocatorTests()
    {
        Directory.CreateDirectory(Lib);
        Directory.CreateDirectory(Downloads);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class Config(Dictionary<string, string> values) : IConfigService
    {
        public Task<T> GetValueAsync<T>(string key, T defaultValue, CancellationToken ct = default) =>
            Task.FromResult(values.TryGetValue(key, out var v) ? (T)(object)v : defaultValue);
        public Task SetValueAsync<T>(string key, T value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Factory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
    }

    private string Touch(string relative)
    {
        var path = Path.Combine(Lib, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "v");
        return path;
    }

    private VideoFileLocator Locator(out Factory factory)
    {
        factory = new Factory(Path.Combine(_root, "t.db"));
        using (var db = factory.CreateDbContext()) db.Database.Migrate();
        return new VideoFileLocator(
            new Config(new() { ["LibraryPath"] = Lib, ["DownloadPath"] = Downloads }),
            new EpisodeNormalizer(), factory);
    }

    [Fact]
    public async Task Finds_episodes_in_the_old_layout()
    {
        var file = Touch("Show/Season 1/[Grp] Show - 05 (1080p).mkv");
        Assert.Equal(file, await Locator(out _).FindVideoFileAsync("Show", 5, default));
    }

    [Fact]
    public async Task Finds_episodes_in_the_jellyfin_layout()
    {
        var file = Touch("Show (2020)/Season 02/Show S02E05.mkv");
        Touch("Show (2020)/Season 01/Show S01E05.mkv");
        Assert.Equal(file, await Locator(out _).FindVideoFileAsync("Show Season 2", 5, default));
    }

    [Fact]
    public async Task Finds_episodes_in_the_minimal_layout()
    {
        var file = Touch("Show/Season 1/05.mkv");
        Assert.Equal(file, await Locator(out _).FindVideoFileAsync("Show", 5, default));
    }

    [Fact]
    public async Task A_renumbered_file_still_answers_to_its_original_episode()
    {
        var file = Touch("Show (2020)/Season 02/Show S02E01.mkv");
        var locator = Locator(out var factory);
        await using (var db = factory.CreateDbContext())
        {
            db.LibraryFileOrigins.Add(new LibraryFileOrigin { Path = file, OriginalName = "[Grp] Show S2 - 13.mkv" });
            await db.SaveChangesAsync();
        }

        Assert.Equal(file, await locator.FindVideoFileAsync("Show Season 2", 13, default));
        Assert.Equal(file, await locator.FindVideoFileAsync("Show Season 2", 1, default));
    }

    [Fact]
    public async Task Another_seasons_folder_is_not_searched()
    {
        Touch("Show/Season 2/[Grp] Show - 05.mkv");
        Assert.Null(await Locator(out _).FindVideoFileAsync("Show", 5, default));
    }
}

public class LibraryMetadataTests
{
    [Theory]
    [InlineData("MOVIE", "Movie")]
    [InlineData("Movie", "Movie")]
    [InlineData("TV", "TV")]
    [InlineData("SPECIAL", "Special")]
    [InlineData("UNKNOWN", null)]
    [InlineData(null, null)]
    public void Types_from_either_source_are_stored_one_way(string? type, string? expected)
    {
        Assert.Equal(expected, LibraryMetadata.NormalizeType(type));
    }

    [Fact]
    public void Year_comes_from_the_air_date()
    {
        Assert.Equal(2023, LibraryMetadata.YearOf("2023-09-29T00:00:00+00:00"));
        Assert.Null(LibraryMetadata.YearOf(null));
        Assert.Null(LibraryMetadata.YearOf("soon"));
    }
}
