using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.MihonBridge;
using Sentrychan.Core.Services;

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
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static byte[] Png()
    {
        // 1×1 PNG.
        return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");
    }

    [SuwayomiFact]
    public async Task The_bridge_drives_a_real_server_end_to_end()
    {
        var bundle = Environment.GetEnvironmentVariable(SuwayomiFactAttribute.Variable)!;
        var layout = new BridgeLayout(_root);
        Directory.CreateDirectory(Path.Combine(_root, "server"));
        Directory.CreateSymbolicLink(layout.ServerDir("vI"), Path.GetFullPath(bundle));
        var marker = Path.Combine(layout.ServerDir("vI"), ".installed");
        var markerExisted = File.Exists(marker);
        if (!markerExisted) File.WriteAllText(marker, "");

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
            if (!markerExisted) File.Delete(marker);
        }
    }
}
