using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sentrychan.Core.Data;
using Sentrychan.Core.MihonBridge;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services;
using Sentrychan.Core.Sources;
using Sentrychan.Tests.MihonBridge;

namespace Sentrychan.Tests;

public sealed class SourcesFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sentrychan-sources-").FullName;
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public void A_file_round_trips_its_manifest_and_packs()
    {
        File.WriteAllText(P("Example.Sources.dll"), "pack bytes");
        var manifest = new SourcesManifest
        {
            Name = "me",
            Feeds = [new("https://example.test/rss?q=show", "Secondary", "1080p")],
            PreferredReleaseGroups = "GroupA, GroupB",
            MihonRepositories = ["https://example.test/index.min.json"],
        };
        SourcesFile.Write(P("my.scsources"), manifest, [P("Example.Sources.dll")]);

        var read = SourcesFile.Read(P("my.scsources"));
        Assert.Equal("https://example.test/rss?q=show", Assert.Single(read.Manifest.Feeds).Url);
        Assert.Equal("Secondary", read.Manifest.Feeds[0].Type);
        Assert.Equal("1080p", read.Manifest.Feeds[0].PreferredQuality);
        Assert.Equal("GroupA, GroupB", read.Manifest.PreferredReleaseGroups);
        Assert.Equal(["https://example.test/index.min.json"], read.Manifest.MihonRepositories);
        Assert.Equal(["Example.Sources.dll"], read.PackNames);

        var installed = SourcesFile.ExtractPacks(P("my.scsources"), P("packs-out"));
        Assert.Equal(["Example.Sources.dll"], installed);
        Assert.Equal("pack bytes", File.ReadAllText(Path.Combine(P("packs-out"), "Example.Sources.dll")));
    }

    [Fact]
    public void A_hand_written_json_manifest_is_read_too()
    {
        File.WriteAllText(P("hand.json"), """{ "feeds": [ { "url": "https://example.test/feed.xml" } ], "extra": 1 }""");
        var read = SourcesFile.Read(P("hand.json"));
        Assert.Equal("Priority", Assert.Single(read.Manifest.Feeds).Type);
        Assert.Empty(read.PackNames);
    }

    [Fact]
    public void Pack_entries_can_only_land_in_the_packs_folder()
    {
        using (var zip = ZipFile.Open(P("odd.scsources"), ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("sources.json").Open())) w.Write("{}");
            using (var w = new StreamWriter(zip.CreateEntry("packs/../escape.dll").Open())) w.Write("x");
            using (var w = new StreamWriter(zip.CreateEntry("packs/sub/nested.dll").Open())) w.Write("x");
            using (var w = new StreamWriter(zip.CreateEntry("packs/readme.txt").Open())) w.Write("x");
            using (var w = new StreamWriter(zip.CreateEntry("packs/Good.dll").Open())) w.Write("x");
        }
        Assert.Equal(["Good.dll"], SourcesFile.ExtractPacks(P("odd.scsources"), P("out")));
        Assert.False(File.Exists(P("escape.dll")));
    }

    [Fact]
    public void Something_that_isnt_a_sources_file_is_refused_plainly()
    {
        using (var zip = ZipFile.Open(P("other.zip"), ZipArchiveMode.Create)) zip.CreateEntry("hello.txt");
        Assert.Contains("isn't a sources file", Assert.Throws<InvalidDataException>(() => SourcesFile.Read(P("other.zip"))).Message);
        File.WriteAllText(P("bad.json"), "not json");
        Assert.Throws<InvalidDataException>(() => SourcesFile.Read(P("bad.json")));
    }

    // ── Feed check ──────────────────────────────────────────────────

    [Fact]
    public void A_web_page_is_told_apart_from_a_feed()
    {
        var check = RssFeedProbe.Inspect("<!DOCTYPE html><html><body><a href='/rss'>RSS</a></body></html>");
        Assert.False(check.IsUsable);
        Assert.Contains("web page", check.Message);
    }

    [Fact]
    public void A_feed_of_torrents_is_usable()
    {
        var check = RssFeedProbe.Inspect("""
            <rss version="2.0"><channel><title>Releases</title>
              <item><title>Show - 01</title><link>https://example.test/download/1.torrent</link></item>
              <item><title>Show - 02</title><enclosure url="https://example.test/d/2" type="application/x-bittorrent"/></item>
              <item><title>Show - 03</title><description>magnet:?xt=urn:btih:abc</description></item>
            </channel></rss>
            """);
        Assert.True(check.IsUsable);
        Assert.Equal(3, check.Items);
        Assert.Equal(3, check.Downloadable);
    }

    [Fact]
    public void A_feed_without_anything_to_download_is_refused()
    {
        var check = RssFeedProbe.Inspect("""
            <rss version="2.0"><channel><title>News</title>
              <item><title>An article</title><link>https://example.test/news/1</link></item>
            </channel></rss>
            """);
        Assert.False(check.IsUsable);
        Assert.Contains("no", check.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_empty_feed_is_fine_and_an_atom_feed_counts()
    {
        Assert.True(RssFeedProbe.Inspect("<rss><channel><title>Narrow search</title></channel></rss>").IsUsable);
        var atom = RssFeedProbe.Inspect("""
            <feed xmlns="http://www.w3.org/2005/Atom"><title>A</title>
              <entry><title>Show - 01</title><link href="magnet:?xt=urn:btih:abc"/></entry>
            </feed>
            """);
        Assert.True(atom.IsUsable);
        Assert.Equal(1, atom.Downloadable);
    }

    // ── Import ──────────────────────────────────────────────────────

    private sealed class Factory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
    }

    private (SourcesTransferService Service, Factory Db, MihonBridgeService Bridge, FakeSuwayomi Server) Transfer(bool bridgeOn)
    {
        var factory = new Factory(P("t.db"));
        using (var db = factory.CreateDbContext()) db.Database.Migrate();
        var config = new MemoryConfig();
        var layout = new BridgeLayout(P("bridge"));
        if (bridgeOn)
        {
            config.Values[MihonBridgeService.EnabledKey] = true;
            Directory.CreateDirectory(layout.ServerDir("vT"));
            File.WriteAllText(Path.Combine(layout.ServerDir("vT"), ".installed"), "");
        }
        var server = new FakeSuwayomi();
        var bridge = new MihonBridgeService(config, new MangaSourceRegistry([]), NullLogger<MihonBridgeService>.Instance,
            new MihonBridgeServiceTests.FakeLauncher(), layout, "vT", null, null, server, TimeSpan.FromSeconds(5));
        return (new SourcesTransferService(factory, bridge, NullLogger<SourcesTransferService>.Instance, P("installed-packs")),
            factory, bridge, server);
    }

    /// <summary>A stand-in source pack: any .NET library built against Sentrychan.Core passes the check — this one is.</summary>
    private string RealPack(string name = "Example.Sources.dll")
    {
        File.Copy(typeof(SourcesFileTests).Assembly.Location, P(name), overwrite: true);
        return P(name);
    }

    private string SampleFile()
    {
        RealPack();
        SourcesFile.Write(P("in.scsources"), new SourcesManifest
        {
            Feeds =
            [
                new("https://example.test/rss/a"),
                new("https://example.test/rss/adult"),
                new("not a url"),
            ],
            PreferredReleaseGroups = "GroupA",
            MihonRepositories = ["https://example.test/other/index.min.json"],
        }, [P("Example.Sources.dll")]);
        return P("in.scsources");
    }

    [Fact]
    public async Task Importing_adds_what_is_new_and_never_replaces_the_users_own()
    {
        var (service, factory, bridge, _) = Transfer(bridgeOn: false);
        using var _b = bridge;
        await using (var db = factory.CreateDbContext())
        {
            db.RssFeeds.Add(new RssFeed { Url = "https://example.test/rss/a" });
            db.AppConfigs.Add(new AppConfig { Key = SourcesTransferService.GroupsKey, Value = "MyGroup" });
            await db.SaveChangesAsync();
        }

        var result = await service.ImportAsync(SampleFile(), url => url.Contains("adult") ? "it needs secret mode" : null);

        Assert.Equal(0, result.FeedsAdded);
        Assert.Equal(1, result.FeedsAlreadyThere);
        Assert.Equal(2, result.FeedsRefused.Count);
        // Groups merge: the file's go after the user's own, which keep their order.
        Assert.Equal(["GroupA"], result.GroupsAdded);
        Assert.Equal(["Example.Sources.dll"], result.PacksInstalled);
        Assert.True(File.Exists(Path.Combine(P("installed-packs"), "Example.Sources.dll")));
        // The bridge is off: the repository waits for it.
        Assert.Equal(0, result.ReposAdded);
        Assert.Equal(1, result.ReposWaiting);
        Assert.Equal(["https://example.test/other/index.min.json"], await bridge.GetPendingRepositoriesAsync());
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal("MyGroup,GroupA", (await db.AppConfigs.SingleAsync(c => c.Key == SourcesTransferService.GroupsKey)).Value);
            Assert.Single(db.RssFeeds);
        }
        Assert.Contains("installed and ready", result.Summary());
    }

    [Fact]
    public async Task Into_an_empty_app_everything_arrives_and_the_bridge_gets_its_repository()
    {
        var (service, factory, bridge, server) = Transfer(bridgeOn: true);
        using var _b = bridge;
        server.Override = (op, _) => op == "AddRepo"
            ? """{"data":{"addExtensionStore":{"extensionStore":{"name":"Other","indexUrl":"https://example.test/other/index.min.json"}}}}"""
            : null;

        var result = await service.ImportAsync(SampleFile());

        Assert.Equal(2, result.FeedsAdded);
        Assert.Equal(["GroupA"], result.GroupsAdded);
        Assert.Equal(1, result.ReposAdded);
        Assert.Equal(0, result.ReposWaiting);
        Assert.Equal("https://example.test/other/index.min.json",
            server.Last("AddRepo")["variables"]!["url"]!.GetValue<string>());

        // And straight back out again.
        var exported = await service.ExportAsync(P("out.scsources"), includePacks: true);
        Assert.Equal(2, exported.Manifest.Feeds.Count);
        Assert.Equal("GroupA", exported.Manifest.PreferredReleaseGroups);
        Assert.Contains("https://example.test/index.min.json", exported.Manifest.MihonRepositories); // the running server's list
        Assert.Equal(["Example.Sources.dll"], exported.PackNames);

        // A second import of the same file changes nothing.
        var again = await service.ImportAsync(SampleFile());
        Assert.Equal(0, again.FeedsAdded);
        Assert.Empty(again.GroupsAdded);
    }

    // ── Whatever shape the sources arrive in ────────────────────────

    /// <summary>What a Mac leaves after a double-click on the file: a folder, plus "._" shadow files.</summary>
    private string UnpackedFolder()
    {
        var root = Directory.CreateDirectory(P("My sources")).FullName;
        File.WriteAllText(Path.Combine(root, "sources.json"),
            """{ "feeds": [ { "url": "https://example.test/rss/a" } ], "preferredReleaseGroups": "GroupA,GroupB" }""");
        Directory.CreateDirectory(Path.Combine(root, "packs"));
        File.Copy(RealPack(), Path.Combine(root, "packs", "Example.Sources.dll"));
        File.WriteAllText(Path.Combine(root, "packs", "._Example.Sources.dll"), "AppleDouble");
        Directory.CreateDirectory(Path.Combine(root, "__MACOSX"));
        File.WriteAllText(Path.Combine(root, "__MACOSX", "._sources.json"), "AppleDouble");
        return root;
    }

    [Fact]
    public void The_unpacked_folder_reads_like_the_file()
    {
        var input = SourcesInput.Read(UnpackedFolder());
        Assert.True(input.FoundManifest);
        Assert.Equal("https://example.test/rss/a", Assert.Single(input.Manifest.Feeds).Url);
        Assert.Equal("Example.Sources.dll", Assert.Single(input.Packs).Name);
        Assert.Empty(input.Skipped);
    }

    [Fact]
    public void A_renamed_or_re_zipped_file_and_a_bare_pack_are_read()
    {
        // macOS "Compress" puts everything under the folder's name, and adds __MACOSX.
        var folder = UnpackedFolder();
        ZipFile.CreateFromDirectory(folder, P("again.zip"), CompressionLevel.Fastest, includeBaseDirectory: true);
        var zipped = SourcesInput.Read(P("again.zip"));
        Assert.True(zipped.FoundManifest);
        Assert.Equal("Example.Sources.dll", Assert.Single(zipped.Packs).Name);

        var bare = SourcesInput.Read(RealPack("Other.Sources.dll"));
        Assert.False(bare.FoundManifest);
        Assert.Equal("Other.Sources.dll", Assert.Single(bare.Packs).Name);
    }

    [Fact]
    public void A_library_that_isnt_a_pack_is_refused_with_a_reason()
    {
        File.WriteAllText(P("Fake.dll"), "not a library at all");
        File.Copy(typeof(object).Assembly.Location, P("System.Private.CoreLib.dll"));
        var input = SourcesInput.Read([P("Fake.dll"), P("System.Private.CoreLib.dll")]);
        Assert.Empty(input.Packs);
        Assert.Contains(input.Skipped, s => s.StartsWith("Fake.dll") && s.Contains("not a .NET library"));
        Assert.Contains(input.Skipped, s => s.StartsWith("System.Private.CoreLib.dll") && s.Contains("not a Sentrychan source pack"));
    }

    [Fact]
    public async Task Importing_the_unpacked_folder_installs_the_pack_and_merges_groups()
    {
        var (service, factory, bridge, _) = Transfer(bridgeOn: false);
        using var _b = bridge;
        var result = await service.ImportAsync([UnpackedFolder()]);
        Assert.Equal(1, result.FeedsAdded);
        Assert.Equal(["GroupA", "GroupB"], result.GroupsAdded);
        Assert.Equal(["Example.Sources.dll"], result.PacksInstalled);
        Assert.Equal(["Example.Sources.dll"], Directory.GetFiles(P("installed-packs")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Sources_put_into_the_sources_folder_by_hand_are_taken_in_at_startup()
    {
        var (service, factory, bridge, _) = Transfer(bridgeOn: false);
        using var _b = bridge;
        // What a tester did: copied the unpacked folder's contents into the sources folder.
        var packs = Directory.CreateDirectory(P("installed-packs")).FullName;
        var unpacked = UnpackedFolder();
        File.Copy(Path.Combine(unpacked, "sources.json"), Path.Combine(packs, "sources.json"));
        Directory.Move(Path.Combine(unpacked, "packs"), Path.Combine(packs, "packs"));

        var result = await service.TidySourcesFolderAsync();

        Assert.NotNull(result);
        Assert.Equal(1, result!.FeedsAdded);
        Assert.True(File.Exists(Path.Combine(packs, "Example.Sources.dll")));
        Assert.False(File.Exists(Path.Combine(packs, "sources.json")));
        Assert.False(Directory.Exists(Path.Combine(packs, "packs")));
        Assert.Single(Directory.GetDirectories(Path.Combine(packs, SourcesTransferService.ImportedFolder)));
        // Nothing left to do the next time.
        Assert.Null(await service.TidySourcesFolderAsync());
    }

    // ── The library folder ──────────────────────────────────────────

    [Fact]
    public void A_deleted_library_folder_is_made_again_but_not_on_a_missing_drive()
    {
        var library = P("Anime");
        Assert.True(Sentrychan.Core.Library.LibraryFolder.Ensure(library, out _, out var created));
        Assert.True(created && Directory.Exists(library));
        Assert.True(Sentrychan.Core.Library.LibraryFolder.Ensure(library, out _, out created));
        Assert.False(created);

        Assert.False(Sentrychan.Core.Library.LibraryFolder.Ensure(P(Path.Combine("gone", "Anime")), out var problem, out _));
        Assert.Contains("drive connected", problem);
        Assert.False(Directory.Exists(P("gone")));
    }
}
