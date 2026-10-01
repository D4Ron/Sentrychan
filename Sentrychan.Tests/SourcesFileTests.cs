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

    private string SampleFile()
    {
        File.WriteAllText(P("Example.Sources.dll"), "pack");
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
        Assert.True(result.GroupsKept);
        Assert.False(result.GroupsSet);
        Assert.Equal(["Example.Sources.dll"], result.PacksInstalled);
        Assert.True(File.Exists(Path.Combine(P("installed-packs"), "Example.Sources.dll")));
        // The bridge is off: the repository waits for it.
        Assert.Equal(0, result.ReposAdded);
        Assert.Equal(1, result.ReposWaiting);
        Assert.Equal(["https://example.test/other/index.min.json"], await bridge.GetPendingRepositoriesAsync());
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal("MyGroup", (await db.AppConfigs.SingleAsync(c => c.Key == SourcesTransferService.GroupsKey)).Value);
            Assert.Single(db.RssFeeds);
        }
        Assert.Contains("restart", result.Summary());
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
        Assert.True(result.GroupsSet);
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
        Assert.False(again.GroupsSet);
        Assert.False(again.GroupsKept);
    }
}
