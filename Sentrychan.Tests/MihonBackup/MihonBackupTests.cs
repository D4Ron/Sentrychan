using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.MangaLibrary;
using Sentrychan.Core.MihonBackup;
using Sentrychan.Core.MihonBridge;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services;
using Sentrychan.Tests.MihonBridge;

namespace Sentrychan.Tests.MihonBackup;

public class TachibkReaderTests
{
    internal static BackupChapter Ch(float n, bool read = false, bool bookmark = false, string? url = null) =>
        new(url ?? $"/c/{n}", $"Chapter {n}", "Group", read, bookmark, 0, 1_700_000_000_000, n, 0);

    private static MihonBackupData Sample() => new(
        [
            new BackupManga(-4611686018427387904, "/title/1", "Invented Title", "Someone", "About it", ["Action", "Drama"], 1,
                "https://example.test/cover.jpg", 1_690_000_000_000, [Ch(1, read: true), Ch(2.5f, bookmark: true)], [0, 3], true,
                [new BackupHistory("/c/1", 1_700_000_100_000)]),
            new BackupManga(42, "/title/2", "Only Browsed", null, null, [], 0, null, 0, [], [], false, []),
        ],
        [new BackupCategory("Reading", 0), new BackupCategory("Later", 3)],
        [new BackupSource("Example Source", -4611686018427387904)]);

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void Everything_the_import_needs_survives_the_wire_format(bool gzip, bool noise, bool packed)
    {
        var read = TachibkReader.Read(TachibkWriter.Write(Sample(), gzip, noise, packed));
        var m = read.Manga[0];
        Assert.Equal(-4611686018427387904, m.Source); // negative ids are ten-byte varints
        Assert.Equal("/title/1", m.Url);
        Assert.Equal(["Action", "Drama"], m.Genres);
        Assert.Equal([0L, 3L], m.Categories);
        Assert.True(m.Favorite);
        Assert.Equal(2.5f, m.Chapters[1].ChapterNumber);
        Assert.True(m.Chapters[0].Read);
        Assert.True(m.Chapters[1].Bookmark);
        Assert.Equal(1_700_000_100_000, m.History[0].LastRead);
        Assert.False(read.Manga[1].Favorite);
        Assert.Equal(["Reading", "Later"], read.Categories.Select(c => c.Name));
        Assert.Equal("Example Source", read.SourceName(-4611686018427387904));
        Assert.Null(read.SourceName(7));
    }

    [Fact]
    public void Something_else_is_refused_plainly()
    {
        var ex = Assert.Throws<InvalidDataException>(() => TachibkReader.Read([0x0a, 0xff, 0xff, 0xff, 0x0f, 0x01]));
        Assert.Contains("Mihon backup", ex.Message);
    }
}

public sealed class MihonBackupImporterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sentrychan-mihonimport-").FullName;
    private readonly Factory _factory;
    private readonly MangaService _manga;
    private readonly MangaLibraryService _library;

    private sealed class Factory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
    }

    public MihonBackupImporterTests()
    {
        _factory = new Factory(Path.Combine(_dir, "t.db"));
        using (var db = _factory.CreateDbContext()) db.Database.Migrate();
        _manga = new MangaService(_factory, NullLogger<MangaService>.Instance);
        _library = new MangaLibraryService(_factory, NullLogger<MangaLibraryService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static MangaChapterInfo Info(int n) => new($"c{n}", n.ToString(), n, null, null, "en", "Group", 20, null);

    private async Task<Manga> InLibraryAsync(string title, string source = "Old Pack Source", int chapters = 5)
    {
        var m = await _manga.AddAsync(new Manga { Source = source, SourceId = title, Title = title });
        await _manga.SyncChaptersAsync(m.Id, Enumerable.Range(1, chapters).Select(Info));
        return m;
    }

    private MihonBackupImporter Importer(IMangaSourceRegistry? registry = null, MihonBridgeService? bridge = null) =>
        new(_factory, _manga, _library, registry ?? new MangaSourceRegistry([]), bridge, NullLogger<MihonBackupImporter>.Instance);

    private static BackupManga Backup(string title, long source = 9, bool favorite = true, BackupChapter[]? chapters = null,
        long[]? categories = null, BackupHistory[]? history = null, string? url = null) =>
        new(source, url ?? "/" + title, title, null, null, [], 0, null, 0, chapters ?? [], categories ?? [], favorite, history ?? []);

    [Fact]
    public async Task Without_the_bridge_titles_in_the_library_get_categories_and_reading_state()
    {
        var frieren = await InLibraryAsync("Frieren");
        await _library.CreateCategoryAsync("Reading"); // exists already: reused, not duplicated
        var backup = new MihonBackupData(
            [
                Backup("FRIEREN!", chapters: [TachibkReaderTests.Ch(1, read: true), TachibkReaderTests.Ch(2, read: true),
                        TachibkReaderTests.Ch(3, bookmark: true), TachibkReaderTests.Ch(99, read: true)],
                    categories: [0, 1], history: [new BackupHistory("/c/2", 1_700_000_000_000)]),
                Backup("Not Here"),
                Backup("Only Browsed", favorite: false),
            ],
            [new BackupCategory("Reading", 0), new BackupCategory("Weekly", 1)],
            [new BackupSource("Example Source", 9)]);

        var importer = Importer();
        var plan = await importer.PlanAsync(TachibkWriter.Write(backup));
        Assert.Equal(1, plan.InLibrary);
        Assert.Equal(0, plan.ToAdd);
        var missing = Assert.Single(plan.Unmatched);
        Assert.Equal("Not Here", missing.Title);
        Assert.Contains("Mihon extensions are off", missing.Reason);
        Assert.Equal([("Example Source", 1)], plan.MissingSources);
        Assert.Equal(["Weekly"], plan.NewCategories);
        Assert.DoesNotContain(plan.Titles, t => t.Title == "Only Browsed"); // never in their library

        var report = await importer.ApplyAsync(plan);
        Assert.Equal(1, report.Updated);
        Assert.Equal(2, report.ChaptersRead); // chapter 99 doesn't exist here
        Assert.Equal(1, report.Bookmarks);
        Assert.Equal(1, report.History);
        Assert.Equal(1, report.CategoriesCreated);

        var after = (await _manga.GetByIdAsync(frieren.Id))!;
        Assert.Equal(["1", "2"], after.Chapters.Where(c => c.IsRead).Select(c => c.ChapterNumber).Order());
        Assert.Equal(2, after.LastReadChapter);
        var bookmarks = await _library.GetBookmarksAsync(frieren.Id);
        Assert.Equal("3", after.Chapters.Single(c => bookmarks.Contains(c.Id)).ChapterNumber);
        var entries = await _library.GetEntriesAsync(novels: false, includeAdult: true);
        Assert.Equal(2, entries.Single().CategoryIds.Count);
        var history = await _library.GetHistoryAsync(novels: false, includeAdult: true);
        Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), history.Single().ReadAt);

        // Importing again changes nothing and duplicates nothing.
        var again = await importer.ApplyAsync(await importer.PlanAsync(TachibkWriter.Write(backup)));
        Assert.Equal((0, 0, 0, 0), (again.ChaptersRead, again.Bookmarks, again.History, again.CategoriesCreated));
        Assert.Equal(2, (await _library.GetCategoriesAsync()).Count);
    }

    [Fact]
    public async Task Reading_state_only_moves_forward()
    {
        var m = await InLibraryAsync("Frieren");
        await _library.SetReadAsync(m.Id, (await _manga.GetByIdAsync(m.Id))!.Chapters.Where(c => c.ChapterSort <= 4).Select(c => c.Id), true);
        var backup = new MihonBackupData([Backup("Frieren", chapters: [TachibkReaderTests.Ch(1), TachibkReaderTests.Ch(5, read: true)])], [], []);
        var importer = Importer();
        await importer.ApplyAsync(await importer.PlanAsync(TachibkWriter.Write(backup)));
        Assert.All((await _manga.GetByIdAsync(m.Id))!.Chapters, c => Assert.True(c.IsRead));
    }

    [Fact]
    public async Task Two_library_titles_with_the_name_are_left_alone_unless_the_source_decides()
    {
        await InLibraryAsync("Twin", source: "A");
        var onB = await InLibraryAsync("Twin", source: "B");
        var importer = Importer();

        var neither = await importer.PlanAsync(TachibkWriter.Write(new([Backup("Twin")], [], [new BackupSource("C", 9)])));
        Assert.Contains("Several titles", Assert.Single(neither.Unmatched).Reason);

        var fromB = await importer.PlanAsync(TachibkWriter.Write(new([Backup("Twin")], [], [new BackupSource("B", 9)])));
        Assert.Equal(1, fromB.InLibrary);
        Assert.Equal(onB.Id, (await importer.ApplyAsync(fromB)).Updated == 1 ? onB.Id : -1);
    }

    // ── With the bridge ─────────────────────────────────────────────

    private (MihonBridgeService Bridge, FakeSuwayomi Server, MangaSourceRegistry Registry) BridgeWith(string sourceId)
    {
        var server = new FakeSuwayomi();
        var config = new MemoryConfig { Values = { [MihonBridgeService.EnabledKey] = true } };
        var layout = new BridgeLayout(Path.Combine(_dir, "bridge"));
        Directory.CreateDirectory(layout.ServerDir("vT"));
        File.WriteAllText(Path.Combine(layout.ServerDir("vT"), ".installed"), "");
        var registry = new MangaSourceRegistry([]);
        var bridge = new MihonBridgeService(config, registry, NullLogger<MihonBridgeService>.Instance,
            new MihonBridgeServiceTests.FakeLauncher(), layout, "vT", null, null, server, TimeSpan.FromSeconds(5));
        registry.Add(new BridgedMangaSource(new BridgeSource(sourceId, "Example Reader", "en", "Example Reader", false, true, false),
            "Example Reader", bridge));
        return (bridge, server, registry);
    }

    [Fact]
    public async Task A_title_from_an_installed_extension_is_restored_into_the_server_then_added_by_its_url()
    {
        var (bridge, server, registry) = BridgeWith("0");
        using var _ = bridge;
        var restored = false;
        server.Override = (op, _) => op switch
        {
            // Unknown to the server until the backup has been restored into it.
            "FindManga" when !restored => """{"data":{"mangas":{"nodes":[]}}}""",
            "RestoreBackup" => (restored = true) ? "restore" : null,
            _ => null,
        };
        var backup = new MihonBackupData(
            [Backup("Newly Restored Title", source: 0, url: "Newly Restored Title",
                chapters: [TachibkReaderTests.Ch(1, read: true, url: "Newly Restored Title/Chapter 1"),
                           TachibkReaderTests.Ch(2, bookmark: true, url: "Newly Restored Title/Chapter 2")],
                categories: [0])],
            [new BackupCategory("Reading", 0)], [new BackupSource("Local source", 0)]);
        var file = TachibkWriter.Write(backup);

        var importer = Importer(registry, bridge);
        var plan = await importer.PlanAsync(file);
        Assert.Equal(1, plan.ToAdd);
        var report = await importer.ApplyAsync(plan);

        Assert.Equal(file, Assert.Single(server.Uploads)); // the backup went to the server as-is
        var flags = server.Last("RestoreBackup")["variables"]!["flags"]!;
        Assert.False(flags["includeServerSettings"]!.GetValue<bool>());
        Assert.False(flags["includeCategories"]!.GetValue<bool>());
        Assert.Equal(1, report.Added);
        var added = Assert.Single(await _manga.GetAllAsync());
        Assert.Equal("Example Reader", added.Source);
        Assert.Equal("2", added.SourceId); // the server's id for it (fixture)
        Assert.Equal("mihon-bridge:/api/v1/manga/2/thumbnail", added.CoverPath);
        var chapters = (await _manga.GetByIdAsync(added.Id))!.Chapters;
        // Chapters came from the server's stored copy and matched by URL, not by number.
        Assert.Equal(["3", "4"], chapters.Select(c => c.SourceId).Order());
        Assert.True(chapters.Single(c => c.SourceId == "3").IsRead);
        Assert.DoesNotContain(server.Requests, r => r["operationName"]!.GetValue<string>() == "FetchChapters"); // no site request
        Assert.Single(await _library.GetBookmarksAsync(added.Id));
    }

    [Fact]
    public async Task A_title_the_server_already_knows_and_the_library_has_is_matched_exactly()
    {
        var (bridge, server, registry) = BridgeWith("0");
        using var _ = bridge;
        var existing = await _manga.AddAsync(new Manga { Source = "Example Reader", SourceId = "2", Title = "Renamed Here" });
        var importer = Importer(registry, bridge);
        var plan = await importer.PlanAsync(TachibkWriter.Write(new(
            [Backup("Invented Title", source: 0, url: "Invented Title")], [], [])));
        Assert.Equal(1, plan.InLibrary); // by the source's URL, whatever the title says
        await importer.ApplyAsync(plan);
        Assert.Empty(server.Uploads); // nothing to restore
        Assert.Equal(2, (await _manga.GetByIdAsync(existing.Id))!.Chapters.Count);
    }

    [Fact]
    public async Task A_source_that_isnt_installed_is_reported_by_name()
    {
        var (bridge, _, registry) = BridgeWith("0");
        using var _b = bridge;
        var plan = await Importer(registry, bridge).PlanAsync(TachibkWriter.Write(new(
            [Backup("Elsewhere", source: 77)], [], [new BackupSource("Some Other Source", 77)])));
        var t = Assert.Single(plan.Unmatched);
        Assert.Equal("Not in your library, and Some Other Source isn't installed as a Mihon extension.", t.Reason);
    }
}
