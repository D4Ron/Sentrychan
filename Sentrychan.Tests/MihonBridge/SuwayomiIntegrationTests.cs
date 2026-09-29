using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.MangaLibrary;
using Sentrychan.Core.MihonBackup;
using Sentrychan.Core.MihonBridge;
using Sentrychan.Core.Services;
using Sentrychan.Tests.MihonBackup;

namespace Sentrychan.Tests.MihonBridge;

/// <summary>
/// Runs only when <c>SENTRYCHAN_SUWAYOMI_DIR</c> points at an unpacked server bundle of the pinned
/// release (the folder holding <c>jre/</c> and <c>bin/</c>). It's a few hundred megabytes, so it
/// isn't part of the normal run.
/// </summary>
public sealed class SuwayomiFactAttribute : FactAttribute
{
    public const string Variable = "SENTRYCHAN_SUWAYOMI_DIR";

    public SuwayomiFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Set {Variable} to an unpacked Suwayomi-Server {BridgeRelease.Version} bundle to run.";
    }
}

/// <summary>
/// The whole bridge against a real server: start, health check, every call a bridged source
/// makes, images, stop. It uses the server's built-in local source over invented titles, so no
/// extension, repository or site is involved.
/// </summary>
public sealed class SuwayomiIntegrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sentrychan-suwayomi-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class Factory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
    }

    /// <summary>The unpacked bundle, linked in as installed server "vI".</summary>
    private BridgeLayout LinkBundle(out Action cleanup)
    {
        var bundle = Environment.GetEnvironmentVariable(SuwayomiFactAttribute.Variable)!;
        var layout = new BridgeLayout(_root);
        Directory.CreateDirectory(Path.Combine(_root, "server"));
        Directory.CreateSymbolicLink(layout.ServerDir("vI"), Path.GetFullPath(bundle));
        var marker = Path.Combine(layout.ServerDir("vI"), ".installed");
        var markerExisted = File.Exists(marker);
        if (!markerExisted) File.WriteAllText(marker, "");
        cleanup = () => { if (!markerExisted) File.Delete(marker); };
        return layout;
    }

    [SuwayomiFact]
    public async Task A_mihon_backup_is_restored_into_the_server_and_added_to_the_library()
    {
        var layout = LinkBundle(out var cleanup);
        var factory = new Factory(Path.Combine(_root, "t.db"));
        await using (var db = factory.CreateDbContext()) db.Database.Migrate();
        var config = new MemoryConfig();
        config.Values[MihonBridgeService.EnabledKey] = true;
        var registry = new MangaSourceRegistry([]);
        using var bridge = new MihonBridgeService(config, registry, NullLogger<MihonBridgeService>.Instance,
            new JavaBridgeProcessLauncher(), layout, "vI", null, null, null, TimeSpan.FromMinutes(3));
        // The server's local source stands in for an installed extension: no site involved.
        registry.Add(new BridgedMangaSource(new BridgeSource("0", "Local source", "en", "Local source", false, true, false),
            "Bridged local", bridge));
        try
        {
            var manga = new MangaService(factory, NullLogger<MangaService>.Instance);
            var library = new MangaLibraryService(factory, NullLogger<MangaLibraryService>.Instance);
            var importer = new MihonBackupImporter(factory, manga, library, registry, bridge, NullLogger<MihonBackupImporter>.Instance);
            var backup = TachibkWriter.Write(new MihonBackupData(
                [new BackupManga(0, "Restored Invented Title", "Restored Invented Title", null, null, [], 0, null, 0,
                    [new BackupChapter("Restored Invented Title/Ch 1", "Ch 1", null, true, false, 0, 0, 1, 1),
                     new BackupChapter("Restored Invented Title/Ch 2", "Ch 2", null, false, true, 0, 0, 2, 0)],
                    [0], true, [new BackupHistory("Restored Invented Title/Ch 1", 1_700_000_000_000)])],
                [new BackupCategory("Reading", 0)], [new BackupSource("Local source", 0)]));

            var plan = await importer.PlanAsync(backup);
            Assert.Equal(1, plan.ToAdd);
            var report = await importer.ApplyAsync(plan);
            Assert.Equal((1, 1, 1, 1), (report.Added, report.ChaptersRead, report.Bookmarks, report.History));

            var added = (await manga.GetByIdAsync((await manga.GetAllAsync()).Single().Id))!;
            Assert.Equal("Bridged local", added.Source);
            Assert.Equal(2, added.Chapters.Count);
            Assert.True(added.Chapters.Single(c => c.ChapterSort == 1).IsRead);
            Assert.Equal("Reading", (await library.GetCategoriesAsync()).Single().Name);

            // A second import finds it by URL and changes nothing.
            var again = await importer.PlanAsync(backup);
            Assert.Equal(1, again.InLibrary);
        }
        finally
        {
            bridge.Stop();
            cleanup();
        }
    }

    private static byte[] Png()
    {
        // 1×1 PNG.
        return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");
    }

    [SuwayomiFact]
    public async Task The_bridge_drives_a_real_server_end_to_end()
    {
        var layout = LinkBundle(out var cleanup);
        foreach (var title in new[] { "Invented Title", "Another Invented Title" })
            for (var c = 1; c <= 2; c++)
            {
                var dir = Path.Combine(layout.DataDir, "local", title, $"Chapter {c}");
                Directory.CreateDirectory(dir);
                for (var p = 1; p <= 3; p++) File.WriteAllBytes(Path.Combine(dir, $"{p:000}.png"), Png());
            }

        var config = new MemoryConfig();
        config.Values[MihonBridgeService.EnabledKey] = true;
        using var bridge = new MihonBridgeService(config, new MangaSourceRegistry([]), NullLogger<MihonBridgeService>.Instance,
            new JavaBridgeProcessLauncher(), layout, "vI", null, null, null, TimeSpan.FromMinutes(3));
        try
        {
            var client = await bridge.ClientAsync();
            Assert.Equal(BridgeRelease.Version, (await client.AboutAsync()).Version);
            Assert.Equal(BridgeState.Running, bridge.State);
            Assert.Empty(await client.GetReposAsync());      // ships none
            Assert.Empty(await client.GetExtensionsAsync());
            Assert.Empty(await bridge.RefreshSourcesAsync()); // only the local source, which is left out

            IMangaSourceService local = new BridgedMangaSource(
                new BridgeSource("0", "Local source", "en", "Local source", false, true, false), "Bridged local", bridge);
            var popular = await local.GetPopularAsync(1);
            Assert.Equal(2, popular.Items.Count);

            var filters = (await local.GetFilterListAsync()).Clone();
            var sort = Assert.IsType<SortFilter>(Assert.Single(filters));
            sort.State = new SortSelection(0, Ascending: false);
            var sorted = await local.SearchAsync("", 1, filters);
            Assert.Equal(["Invented Title", "Another Invented Title"], sorted.Items.Select(i => i.Title));

            var id = sorted.Items[0].SourceId;
            Assert.Equal("Invented Title", (await local.GetDetailsAsync(id))!.Title);
            var chapters = await local.GetChaptersAsync(id);
            Assert.Equal(["1", "2"], chapters.Select(c => c.ChapterNumber));
            var pages = await local.GetPageUrlsAsync(chapters[0].SourceId);
            Assert.Equal(3, pages.Count);

            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            Assert.Equal(Png(), await http.GetByteArrayAsync(pages[0]));
            var cover = await bridge.ResolveImageUrlAsync(sorted.Items[0].CoverUrl);
            var coverResponse = await http.GetAsync(cover);
            Assert.True(coverResponse.IsSuccessStatusCode);

            var port = bridge.Address!.Port;
            bridge.Stop();
            Assert.Equal(BridgeState.Stopped, bridge.State);
            await Task.Delay(500);
            Assert.True(BridgePorts.IsFree(port));
        }
        finally
        {
            bridge.Stop();
            cleanup();
        }
    }
}
