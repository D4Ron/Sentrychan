using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.MihonBridge;
using Sentrychan.Core.Services;

namespace Sentrychan.Tests.MihonBridge;

internal sealed class MemoryConfig : IConfigService
{
    public Dictionary<string, object?> Values { get; } = [];

    public Task<T> GetValueAsync<T>(string key, T defaultValue, CancellationToken ct = default) =>
        Task.FromResult(Values.TryGetValue(key, out var v) && v is T t ? t : defaultValue);

    public Task SetValueAsync<T>(string key, T value, CancellationToken ct = default)
    {
        Values[key] = value;
        return Task.CompletedTask;
    }
}

/// <summary>Builds a small stand-in for a server bundle: one top folder holding jre/bin/java and the jar.</summary>
internal static class FakeBundle
{
    public static byte[] TarGz(string top)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gz))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, top + "/"));
            Add(tar, top + "/jre/bin/java", "#!/bin/sh\n", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Add(tar, top + "/bin/Suwayomi-Server.jar", "jar", UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Add(tar, top + "/electron/chromium.bin", "big", UnixFileMode.UserRead);
        }
        return ms.ToArray();
    }

    private static void Add(TarWriter tar, string name, string text, UnixFileMode mode)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)),
            Mode = mode,
        };
        tar.WriteEntry(entry);
    }

    public static byte[] Zip(string top)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { "/jre/bin/java.exe", "/jre/bin/java", "/bin/Suwayomi-Server.jar" })
            {
                using var w = new StreamWriter(zip.CreateEntry(top + name).Open());
                w.Write("x");
            }
        }
        return ms.ToArray();
    }

    public static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

public sealed class BridgeInstallerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sentrychan-bridge-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static readonly Uri Url = new("https://example.test/server.tar.gz");

    [Fact]
    public async Task A_matching_bundle_is_unpacked_without_its_top_folder_or_launcher()
    {
        var bytes = FakeBundle.TarGz("Suwayomi-Server-vX-linux-x64");
        var fake = new FakeSuwayomi();
        fake.Files[Url.ToString()] = (bytes, HttpStatusCode.OK);
        var layout = new BridgeLayout(_root);
        var progress = new List<string>();

        await new BridgeInstaller(new HttpClient(fake), layout).InstallAsync("vX",
            new BridgeAsset("server.tar.gz", FakeBundle.Sha(bytes)), Url, new Progress<BridgeInstallProgress>(p => progress.Add(p.Stage)));

        Assert.True(layout.IsInstalled("vX"));
        Assert.True(File.Exists(layout.JarPath("vX")));
        Assert.False(Directory.Exists(Path.Combine(layout.ServerDir("vX"), "electron")));
        Assert.False(Directory.Exists(layout.ServerDir("vX") + ".partial"));
        Assert.Empty(Directory.GetFiles(layout.DownloadDir)); // the archive is gone
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(layout.JavaPath("vX")).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task A_zip_bundle_works_the_same()
    {
        var bytes = FakeBundle.Zip("Suwayomi-Server-vX-windows-x64");
        var fake = new FakeSuwayomi();
        var zipUrl = new Uri("https://example.test/server.zip");
        fake.Files[zipUrl.ToString()] = (bytes, HttpStatusCode.OK);
        var layout = new BridgeLayout(_root);
        await new BridgeInstaller(new HttpClient(fake), layout).InstallAsync("vX",
            new BridgeAsset("server.zip", FakeBundle.Sha(bytes)), zipUrl);
        Assert.True(layout.IsInstalled("vX"));
        Assert.True(File.Exists(layout.JarPath("vX")));
    }

    [Fact]
    public async Task A_bundle_with_the_wrong_hash_is_never_unpacked()
    {
        var bytes = FakeBundle.TarGz("top");
        var fake = new FakeSuwayomi();
        fake.Files[Url.ToString()] = (bytes, HttpStatusCode.OK);
        var layout = new BridgeLayout(_root);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => new BridgeInstaller(new HttpClient(fake), layout)
            .InstallAsync("vX", new BridgeAsset("server.tar.gz", new string('0', 64)), Url));
        Assert.Contains("checksum", ex.Message);
        Assert.False(layout.IsInstalled("vX"));
        Assert.False(Directory.Exists(layout.ServerDir("vX")));
        Assert.Empty(Directory.GetFiles(layout.DownloadDir));
    }

    [Fact]
    public void Every_platform_with_a_bundle_has_a_full_pinned_hash()
    {
        foreach (var p in new[] { "win-x64", "linux-x64", "osx-x64", "osx-arm64" })
        {
            var a = BridgeRelease.AssetFor(p)!;
            Assert.Matches("^[0-9a-f]{64}$", a.Sha256);
            Assert.Contains(BridgeRelease.Version, a.FileName);
        }
        Assert.Null(BridgeRelease.AssetFor("win-arm64")); // no such bundle: the bridge says so instead of guessing
        Assert.StartsWith("https://github.com/Suwayomi/Suwayomi-Server/releases/download/", BridgeRelease.UrlOf(BridgeRelease.AssetFor("linux-x64")!).ToString());
    }
}

public sealed class MihonBridgeServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sentrychan-bridge-").FullName;
    private readonly MemoryConfig _config = new();
    private readonly FakeSuwayomi _server = new();
    private readonly FakeLauncher _launcher = new();
    private readonly MangaSourceRegistry _registry = new([]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    internal sealed class FakeLauncher : IBridgeProcessLauncher
    {
        public List<BridgeLaunch> Launches { get; } = [];
        public List<int> KilledOrphans { get; } = [];
        public FakeProcess? Last { get; private set; }
        public bool ExitImmediately { get; set; }

        public IBridgeProcess Start(BridgeLaunch launch)
        {
            Launches.Add(launch);
            return Last = new FakeProcess(4000 + Launches.Count) { HasExited = ExitImmediately };
        }

        public void KillOrphan(int processId) => KilledOrphans.Add(processId);
    }

    internal sealed class FakeProcess(int id) : IBridgeProcess
    {
        public int Id => id;
        public bool HasExited { get; set; }
        public string RecentOutput => "Starting\nError: something the server said";
        public bool Killed { get; private set; }
        public void Kill() { Killed = true; HasExited = true; }
        public void Dispose() => Kill();
    }

    private MihonBridgeService Service(bool installed = true)
    {
        var layout = new BridgeLayout(_root);
        if (installed)
        {
            Directory.CreateDirectory(layout.ServerDir("vT"));
            File.WriteAllText(Path.Combine(layout.ServerDir("vT"), ".installed"), "");
        }
        return new MihonBridgeService(_config, _registry, NullLogger<MihonBridgeService>.Instance, _launcher, layout,
            "vT", new BridgeAsset("x.tar.gz", new string('0', 64)), new Uri("https://example.test/x.tar.gz"), _server,
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Nothing_starts_while_the_bridge_is_off()
    {
        using var bridge = Service();
        await bridge.InitializeAsync();
        Assert.Equal(BridgeState.Off, bridge.State);
        var ex = await Assert.ThrowsAsync<BridgeException>(() => bridge.ClientAsync());
        Assert.Equal(BridgeFailure.NotRunning, ex.Failure);
        Assert.Contains("turned off", ex.Message);
        Assert.Empty(_launcher.Launches);
        Assert.Null(await bridge.ResolveImageUrlAsync("mihon-bridge:/api/v1/manga/1/thumbnail"));
    }

    [Fact]
    public async Task Starts_on_demand_on_loopback_and_waits_until_it_answers()
    {
        _config.Values[MihonBridgeService.EnabledKey] = true;
        var calls = 0;
        _server.Listening = () => ++calls > 3; // the first probes find nobody listening
        using var bridge = Service();

        var client = await bridge.ClientAsync();
        Assert.Equal(BridgeState.Running, bridge.State);
        var launch = Assert.Single(_launcher.Launches);
        Assert.Equal(client.BaseUri.Port, launch.Port);
        Assert.Equal("127.0.0.1", client.BaseUri.Host);
        Assert.Equal(launch.Port, _config.Values[MihonBridgeService.PortKey]); // reused next time if free
        Assert.False(launch.WebView);
        Assert.Equal(Path.Combine(_root, "data"), launch.DataDir);
        Assert.Equal("4001", File.ReadAllText(bridge.Layout.PidFile));

        // Running: no second process.
        Assert.Same(client, await bridge.ClientAsync());
        Assert.Single(_launcher.Launches);

        Assert.Equal($"http://127.0.0.1:{launch.Port}/api/v1/manga/1/thumbnail",
            await bridge.ResolveImageUrlAsync("mihon-bridge:/api/v1/manga/1/thumbnail"));

        bridge.Stop();
        Assert.True(_launcher.Last!.Killed);
        Assert.False(File.Exists(bridge.Layout.PidFile));
        Assert.Equal(BridgeState.Stopped, bridge.State);
    }

    [Fact]
    public async Task A_server_that_dies_while_starting_is_reported_with_its_last_words()
    {
        _config.Values[MihonBridgeService.EnabledKey] = true;
        _launcher.ExitImmediately = true;
        using var bridge = Service();
        var ex = await Assert.ThrowsAsync<BridgeException>(() => bridge.ClientAsync());
        Assert.Equal(BridgeState.Failed, bridge.State);
        Assert.Contains("stopped while starting", ex.Message);
        Assert.Contains("something the server said", bridge.LastError);
    }

    [Fact]
    public async Task A_server_left_over_from_a_crash_is_stopped_first()
    {
        _config.Values[MihonBridgeService.EnabledKey] = true;
        using var bridge = Service();
        Directory.CreateDirectory(_root);
        File.WriteAllText(bridge.Layout.PidFile, "31337");
        await bridge.ClientAsync();
        Assert.Equal([31337], _launcher.KilledOrphans);
    }

    [Fact]
    public async Task Not_installed_says_so_instead_of_starting()
    {
        _config.Values[MihonBridgeService.EnabledKey] = true;
        using var bridge = Service(installed: false);
        await bridge.InitializeAsync();
        Assert.Equal(BridgeState.NotInstalled, bridge.State);
        var ex = await Assert.ThrowsAsync<BridgeException>(() => bridge.ClientAsync());
        Assert.Contains("isn't installed", ex.Message);
    }

    [Fact]
    public async Task Installed_sources_join_the_registry_with_stable_names_and_leave_when_turned_off()
    {
        _config.Values[MihonBridgeService.EnabledKey] = true;
        _registry.Add(new V1Source(browse: true)); // "Old Pack Source" — a pack source
        using var bridge = Service();

        var sources = await bridge.RefreshSourcesAsync();
        // Mihon's own local source is left out; Sentrychan has one.
        Assert.Equal(["Example Reader", "Example Reader (ES)", "Grown-ups Only"], sources.Select(s => s.SourceName));
        Assert.Equal(4, _registry.Sources.Count);
        Assert.True(sources[2].IsAdultSource);

        // A restart puts the same sources back from the cache, without starting the server.
        using var again = new MihonBridgeService(_config, _registry, NullLogger<MihonBridgeService>.Instance, _launcher,
            bridge.Layout, "vT", null, null, _server);
        var launches = _launcher.Launches.Count;
        foreach (var s in _registry.Sources.OfType<BridgedMangaSource>().ToList()) _registry.Remove(s);
        await again.InitializeAsync();
        Assert.Equal(sources.Select(s => s.SourceName), again.Sources.Select(s => s.SourceName));
        Assert.Equal(launches, _launcher.Launches.Count);

        await again.DisableAsync();
        Assert.Equal(["Old Pack Source"], _registry.Sources.Select(s => s.SourceName));
    }

    [Fact]
    public async Task A_name_clash_with_a_pack_source_gets_a_suffix_that_sticks()
    {
        _config.Values[MihonBridgeService.EnabledKey] = true;
        var pack = new V1Source(browse: true);
        _server.Responses["Sources"] = """
            {"data":{"sources":{"nodes":[{"id":"5","name":"Old Pack Source","lang":"en","displayName":"Old Pack Source","isNsfw":false,"supportsLatest":false,"isConfigurable":false}]}}}
            """;
        _registry.Add(pack);
        using var bridge = Service();
        Assert.Equal("Old Pack Source (Mihon)", Assert.Single(await bridge.RefreshSourcesAsync()).SourceName);

        // The pack goes away; the bridged source keeps the name library entries already store.
        _registry.Remove(pack);
        Assert.Equal("Old Pack Source (Mihon)", Assert.Single(await bridge.RefreshSourcesAsync()).SourceName);
    }

    [Fact]
    public void The_server_is_told_to_stay_local_and_quiet()
    {
        var args = JavaBridgeProcessLauncher.Arguments(new BridgeLaunch("java", "/b/bin/Suwayomi-Server.jar", "/data", 4567));
        Assert.Contains("-Dsuwayomi.tachidesk.config.server.ip=127.0.0.1", args);
        Assert.Contains("-Dsuwayomi.tachidesk.config.server.port=4567", args);
        Assert.Contains("-Dsuwayomi.tachidesk.config.server.rootDir=/data", args);
        Assert.Contains("-Dsuwayomi.tachidesk.config.server.webUIEnabled=false", args);
        Assert.Contains("-Dsuwayomi.tachidesk.config.server.initialOpenInBrowserEnabled=false", args);
        Assert.Contains("-Dsuwayomi.tachidesk.config.server.systemTrayEnabled=false", args);
        Assert.Contains("-Dsuwayomi.tachidesk.config.server.kcefEnabled=false", args);
        Assert.Equal(["-jar", "/b/bin/Suwayomi-Server.jar"], args.TakeLast(2));
    }

    [Fact]
    public void A_free_port_is_found_and_a_taken_one_is_avoided()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try
        {
            var taken = ((IPEndPoint)l.LocalEndpoint).Port;
            Assert.False(BridgePorts.IsFree(taken));
            Assert.NotEqual(taken, BridgePorts.Pick(taken));
        }
        finally { l.Stop(); }
    }
}
