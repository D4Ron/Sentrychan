using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.Core.MihonBridge;

public enum BridgeState { Off, NotInstalled, Installing, Stopped, Starting, Running, Failed }

/// <summary>
/// The Mihon extension bridge: an opt-in helper server (Suwayomi-Server) that runs Mihon
/// extensions, managed as a child process. Nothing here does anything until the user turns
/// the bridge on — it ships no repositories, suggests none, and downloads nothing by itself.
///
/// <para>Lifecycle: installed once (pinned, hash-checked download), started on demand the first
/// time a bridged source or cover needs it, bound to 127.0.0.1 on a free port, health-checked
/// through its API, and stopped with the app.</para>
/// </summary>
public sealed class MihonBridgeService : IMihonBridge, IHostedService, IDisposable
{
    public const string EnabledKey = "MihonBridge.Enabled";
    public const string WebChecksKey = "MihonBridge.WebChecks";
    public const string PortKey = "MihonBridge.Port";
    public const string SourcesKey = "MihonBridge.Sources";

    /// <summary>Mihon's built-in local source; Sentrychan has its own.</summary>
    private const string LocalSourceId = "0";

    private readonly IConfigService _config;
    private readonly IMangaSourceRegistry _registry;
    private readonly IBridgeProcessLauncher _launcher;
    private readonly ILogger<MihonBridgeService> _log;
    private readonly HttpClient _download;
    private readonly HttpClient _api;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly string _version;
    private readonly BridgeAsset? _asset;
    private readonly Uri? _assetUrl;

    private IBridgeProcess? _process;
    private SuwayomiClient? _client;

    public MihonBridgeService(IConfigService config, IMangaSourceRegistry registry, ILogger<MihonBridgeService> log)
        : this(config, registry, log, new JavaBridgeProcessLauncher(), new BridgeLayout(AppPaths.MihonBridge),
            BridgeRelease.Version, BridgeRelease.AssetFor(BridgeRelease.CurrentPlatform()), null, null)
    { }

    /// <summary>For tests: a fake launcher, a temp layout, a local release and HTTP handlers.</summary>
    public MihonBridgeService(IConfigService config, IMangaSourceRegistry registry, ILogger<MihonBridgeService> log,
        IBridgeProcessLauncher launcher, BridgeLayout layout, string version, BridgeAsset? asset, Uri? assetUrl,
        HttpMessageHandler? handler, TimeSpan? startTimeout = null)
    {
        _config = config;
        _registry = registry;
        _log = log;
        _launcher = launcher;
        Layout = layout;
        _version = version;
        _asset = asset;
        _assetUrl = assetUrl ?? (asset == null ? null : BridgeRelease.UrlOf(asset));
        _download = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _download.Timeout = TimeSpan.FromMinutes(30);
        // Loopback only, and never through a proxy the user configured for the internet.
        _api = handler == null
            ? new HttpClient(new SocketsHttpHandler { UseProxy = false })
            : new HttpClient(handler, disposeHandler: false);
        _api.Timeout = TimeSpan.FromMinutes(3); // an extension listing a slow site can take a while
        StartTimeout = startTimeout ?? TimeSpan.FromMinutes(2);
    }

    public BridgeLayout Layout { get; }

    /// <summary>First start migrates the server's database; give it room.</summary>
    public TimeSpan StartTimeout { get; }

    /// <summary>False when the pinned release has no build for this OS/CPU.</summary>
    public bool IsSupported => _asset != null;

    public bool IsInstalled => Layout.IsInstalled(_version);

    private BridgeState _state = BridgeState.Off;
    public BridgeState State => _state;

    /// <summary>Why the last start failed, in words the user can read.</summary>
    public string? LastError { get; private set; }

    /// <summary>The server's address while it runs.</summary>
    public Uri? Address => _client?.BaseUri;

    public event Action? StateChanged;

    /// <summary>Raised after the bridged sources in the registry changed.</summary>
    public event Action<IReadOnlyList<BridgedMangaSource>>? SourcesChanged;

    private void SetState(BridgeState state, string? error = null)
    {
        _state = state;
        LastError = error;
        StateChanged?.Invoke();
    }

    public Task<bool> IsEnabledAsync(CancellationToken ct = default) => _config.GetValueAsync(EnabledKey, false, ct);

    // ── Turning it on and off ───────────────────────────────────────

    /// <summary>Reads the saved state at startup and puts the last known sources in the registry.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(ct)) { SetState(BridgeState.Off); return; }
        SetState(IsInstalled ? BridgeState.Stopped : BridgeState.NotInstalled);
        SyncRegistry(await LoadCachedSourcesAsync(ct));
    }

    /// <summary>
    /// Turns the bridge on: downloads and installs the server if needed. The caller has already
    /// explained the download to the user.
    /// </summary>
    public async Task EnableAsync(IProgress<BridgeInstallProgress>? progress = null, CancellationToken ct = default)
    {
        await _config.SetValueAsync(EnabledKey, true, ct);
        await InstallAsync(progress, ct);
    }

    /// <summary>Turns it off: stops the server and removes its sources from the app. Installed files stay.</summary>
    public async Task DisableAsync(CancellationToken ct = default)
    {
        await _config.SetValueAsync(EnabledKey, false, ct);
        Stop();
        SyncRegistry([]);
        SetState(BridgeState.Off);
    }

    public async Task InstallAsync(IProgress<BridgeInstallProgress>? progress = null, CancellationToken ct = default)
    {
        if (IsInstalled) { if (_state is BridgeState.Off or BridgeState.NotInstalled) SetState(BridgeState.Stopped); return; }
        if (_asset == null || _assetUrl == null)
            throw new PlatformNotSupportedException("The Mihon extensions server has no build for this system.");
        SetState(BridgeState.Installing);
        try
        {
            await new BridgeInstaller(_download, Layout).InstallAsync(_version, _asset, _assetUrl, progress, ct);
            new BridgeInstaller(_download, Layout).RemoveOtherVersions(_version);
            SetState(BridgeState.Stopped);
        }
        catch (Exception ex)
        {
            SetState(BridgeState.NotInstalled, ex is OperationCanceledException ? null : ex.Message);
            throw;
        }
    }

    // ── Running ─────────────────────────────────────────────────────

    public async Task<SuwayomiClient> ClientAsync(CancellationToken ct = default)
    {
        if (_client != null && _process is { HasExited: false }) return _client;

        await _startLock.WaitAsync(ct);
        try
        {
            if (_client != null && _process is { HasExited: false }) return _client;
            if (!await IsEnabledAsync(ct))
                throw BridgeException.NotRunning("Mihon extensions are turned off (Settings → Sources).");
            if (!IsInstalled)
                throw BridgeException.NotRunning("The Mihon extensions server isn't installed yet (Settings → Sources).");
            return await StartAsync(ct);
        }
        finally { _startLock.Release(); }
    }

    private async Task<SuwayomiClient> StartAsync(CancellationToken ct)
    {
        Stop();
        KillOrphan();
        SetState(BridgeState.Starting);

        var port = BridgePorts.Pick(await _config.GetValueAsync(PortKey, 0, ct));
        await _config.SetValueAsync(PortKey, port, ct);
        Directory.CreateDirectory(Layout.DataDir);
        var launch = new BridgeLaunch(Layout.JavaPath(_version), Layout.JarPath(_version), Layout.DataDir, port,
            Layout.PreloadLibrary(_version), await _config.GetValueAsync(WebChecksKey, false, ct));

        IBridgeProcess process;
        try { process = _launcher.Start(launch); }
        catch (Exception ex)
        {
            SetState(BridgeState.Failed, "The Mihon extensions server couldn't start: " + ex.Message);
            throw BridgeException.NotRunning(LastError!, ex);
        }
        _process = process;
        try { File.WriteAllText(Layout.PidFile, process.Id.ToString(CultureInfo.InvariantCulture)); } catch { /* best effort */ }

        var client = new SuwayomiClient(_api, new Uri($"http://127.0.0.1:{port}/"));
        var deadline = DateTime.UtcNow + StartTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                var tail = LastLines(process.RecentOutput, 3);
                Stop();
                SetState(BridgeState.Failed, "The Mihon extensions server stopped while starting." + (tail.Length > 0 ? " " + tail : ""));
                throw BridgeException.NotRunning(LastError!);
            }
            try
            {
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probe.CancelAfter(TimeSpan.FromSeconds(5));
                var about = await client.AboutAsync(probe.Token);
                _log.LogInformation("Mihon bridge: {Name} {Version} on port {Port}", about.Name, about.Version, port);
                break;
            }
            catch (Exception ex) when (ex is BridgeException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                if (DateTime.UtcNow > deadline)
                {
                    Stop();
                    SetState(BridgeState.Failed, "The Mihon extensions server didn't answer in time.");
                    throw BridgeException.NotRunning(LastError!);
                }
                await Task.Delay(500, ct);
            }
        }

        _client = client;
        SetState(BridgeState.Running);
        return client;
    }

    private static string LastLines(string text, int n) =>
        string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(n));

    /// <summary>Stops the server. It starts again the next time something needs it.</summary>
    public void Stop()
    {
        var p = _process;
        _process = null;
        _client = null;
        if (p == null) return;
        try { p.Dispose(); } catch { /* already gone */ }
        try { File.Delete(Layout.PidFile); } catch { /* best effort */ }
        if (_state is BridgeState.Running or BridgeState.Starting) SetState(BridgeState.Stopped);
    }

    /// <summary>A server a crashed session left running would hold the data directory; stop it.</summary>
    private void KillOrphan()
    {
        try
        {
            if (!File.Exists(Layout.PidFile)) return;
            if (int.TryParse(File.ReadAllText(Layout.PidFile).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                _launcher.KillOrphan(pid);
            File.Delete(Layout.PidFile);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Mihon bridge: orphan check failed"); }
    }

    /// <summary>
    /// A cover URL (<see cref="BridgedMangaSource.ImageScheme"/>) against the running server,
    /// starting it if needed. Null when the bridge is off or can't start.
    /// </summary>
    public async Task<string?> ResolveImageUrlAsync(string url, CancellationToken ct = default)
    {
        if (!url.StartsWith(BridgedMangaSource.ImageScheme, StringComparison.Ordinal)) return url;
        try
        {
            var client = await ClientAsync(ct);
            return client.Absolute(url[BridgedMangaSource.ImageScheme.Length..]);
        }
        catch (BridgeException) { return null; }
    }

    // ── Sources ─────────────────────────────────────────────────────

    /// <summary>Asks the server which sources its installed extensions provide and updates the app's list.</summary>
    public async Task<IReadOnlyList<BridgedMangaSource>> RefreshSourcesAsync(CancellationToken ct = default)
    {
        var client = await ClientAsync(ct);
        var sources = (await client.GetSourcesAsync(ct)).Where(s => s.Id != LocalSourceId).ToList();
        var named = NameSources(sources, await LoadCachedSourcesAsync(ct));
        await _config.SetValueAsync(SourcesKey, JsonSerializer.Serialize(named), ct);
        return SyncRegistry(named);
    }

    public IReadOnlyList<BridgedMangaSource> Sources =>
        _registry.Sources.OfType<BridgedMangaSource>().ToList();

    /// <summary>A bridged source with the name a library entry stores. Sticks once chosen.</summary>
    public sealed record NamedSource(BridgeSource Source, string Name);

    /// <summary>
    /// Picks each source's name in the app. Library entries store the source by name, so a name
    /// never changes once given; a clash with another source (a source pack, or two extensions
    /// with the same display name) gets a suffix.
    /// </summary>
    private List<NamedSource> NameSources(List<BridgeSource> sources, IReadOnlyList<NamedSource> previous)
    {
        var byId = previous.ToDictionary(p => p.Source.Id, p => p.Name);
        var taken = new HashSet<string>(_registry.Sources.Where(s => s is not BridgedMangaSource).Select(s => s.SourceName),
            StringComparer.OrdinalIgnoreCase);
        var result = new List<NamedSource>();
        foreach (var s in sources)
        {
            if (!byId.TryGetValue(s.Id, out var name) || taken.Contains(name))
            {
                name = s.DisplayName;
                if (taken.Contains(name)) name += " (Mihon)";
                for (var i = 2; taken.Contains(name); i++) name = $"{s.DisplayName} (Mihon {i})";
            }
            taken.Add(name);
            result.Add(new(s, name));
        }
        return result;
    }

    private async Task<IReadOnlyList<NamedSource>> LoadCachedSourcesAsync(CancellationToken ct)
    {
        var json = await _config.GetValueAsync(SourcesKey, string.Empty, ct);
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<NamedSource>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private List<BridgedMangaSource> SyncRegistry(IReadOnlyList<NamedSource> wanted)
    {
        var current = _registry.Sources.OfType<BridgedMangaSource>().ToList();
        static bool Same(NamedSource w, BridgedMangaSource c) => w.Source == c.Source && w.Name == c.SourceName;
        foreach (var s in current.Where(c => !wanted.Any(w => Same(w, c))))
            _registry.Remove(s);
        foreach (var w in wanted.Where(w => !current.Any(c => Same(w, c))))
            _registry.Add(new BridgedMangaSource(w.Source, w.Name, this));
        var now = Sources;
        SourcesChanged?.Invoke(now);
        return now.ToList();
    }

    // ── Host lifetime ───────────────────────────────────────────────

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        Stop();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Stop();
        _download.Dispose();
        _api.Dispose();
        _startLock.Dispose();
    }
}
