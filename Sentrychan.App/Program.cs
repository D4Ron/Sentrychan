using Avalonia;
using Avalonia.ReactiveUI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Extensions.Http;
using Refit;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Services;
using Sentrychan.Core.Services.Api;
using Sentrychan.Core.Services.AniDb;
using LibVLCSharp.Shared;
using Serilog;                    // WriteTo.File + AddSerilog extension methods
using Sentrychan.UI;
using Sentrychan.UI.ViewModels;
using Sentrychan.UI.Interfaces;
using Sentrychan.UI.Services;
using Sentrychan.Core;

namespace Sentrychan.App;

public static class Program
{
    private static string _logDir = string.Empty;

    // Single-instance plumbing. Kept alive for the process lifetime. Per flavour: a preview
    // launch must not just surface a running stable window and exit. Stable keeps the names
    // older builds used, so an updated and a not-yet-updated stable still see each other.
    private static System.Threading.Mutex? _instanceMutex;
    private static readonly string MutexName = InstanceGuard.OwnInstanceMutex;
    private static IDisposable? _showWindowListener;

    // Passed to the new process by RestartApp, which starts it before this one has exited.
    private const string RestartedArg = "--restarted";

    [System.STAThread]
    public static void Main(string[] args)
    {
        // MUST be the very first thing that runs. Velopack's installer/updater
        // invokes the app with hook arguments (--velopack-install, etc.) during
        // install/update/uninstall; this call handles them and exits before any of
        // our own startup runs. Skipped entirely on a normal launch.
        // OnFirstRun: the installer just put the app here and started it — the app then asks what
        // an installer's last page would (InstallFinishedDialog), including whether to open it at all.
        Velopack.VelopackApp.Build()
            .OnFirstRun(_ => Sentrychan.UI.App.JustInstalled = true)
            .Run();

        // ── Single instance ────────────────────────────────────────
        // Without this, launching Sentrychan again (or the tray leaving one running
        // while another starts) spins up a SECOND app — each with its own RSS/manga
        // monitors firing their own notifications. That's the real cause of "too many
        // notifications". Second launches signal the running copy to show, then exit.
        _instanceMutex = new System.Threading.Mutex(true, MutexName, out var isFirst);
        if (!isFirst && args.Contains(RestartedArg))
        {
            // Started by RestartApp while the old instance is still shutting down: wait for it to
            // let go instead of asking it to show its window. Once we hold the mutex it has exited,
            // so its database handles are closed too.
            try { isFirst = _instanceMutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (System.Threading.AbandonedMutexException) { isFirst = true; }
        }
        if (!isFirst)
        {
            // Ask the running instance to surface its window. It may be mid-startup and not
            // listening yet — then just exit quietly.
            ShowWindowSignal.Send(BuildInfo.IsPreview);
            return;
        }
        StartShowWindowListener();

        // Stable and preview may run side by side, but only one of them works the library.
        InstanceGuard.CheckOtherInstance();

        AppPaths.EnsureDataDir();
        var dbPath = AppPaths.Database;

        _logDir = AppPaths.Logs;
        Directory.CreateDirectory(_logDir);

        // A library copied from stable on the preview's first run is swapped in here, before
        // anything opens the database (see StableLibraryCopy). If it fails the app starts on
        // what it had; the staged copy stays for the next attempt.
        try
        {
            if (StableLibraryCopy.ApplyStaged(AppPaths.DataDir))
                Console.WriteLine("[Program] Library copied from Sentrychan is now in place");
        }
        catch (Exception ex) { LogCrash("Applying the library copied from Sentrychan", ex); }

        // ── Global crash handling ──────────────────────────────────
        // Without a console (WinExe), an unhandled exception would vanish.
        // Log it to disk and show the user where the log is.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        var hostBuilder = Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) =>
            {
                // ── Database ──────────────────────────────────────────────
                services.AddDbContextFactory<AppDbContext>(options =>
                    options.UseSqlite($"Data Source={dbPath}"));

                // ── Caching ───────────────────────────────────────────────
                services.AddMemoryCache();

                // ── Polly ─────────────────────────────────────────────────
                var retryPolicy = HttpPolicyExtensions
                    .HandleTransientHttpError()
                    .WaitAndRetryAsync(3,
                        attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt))
                                 + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500)));

                var circuitBreaker = HttpPolicyExtensions
                    .HandleTransientHttpError()
                    .CircuitBreakerAsync(5, TimeSpan.FromSeconds(30));

                // ── Refit — Jikan ─────────────────────────────────────────
                services.AddRefitClient<IJikanApi>(new RefitSettings
                {
                    ContentSerializer = new SystemTextJsonContentSerializer()
                })
                    .ConfigureHttpClient(c =>
                    {
                        c.BaseAddress = new Uri("https://api.jikan.moe/v4");
                        c.Timeout = TimeSpan.FromSeconds(15);
                    })
                    .AddPolicyHandler(retryPolicy)
                    .AddPolicyHandler(circuitBreaker);

                // ── Named HttpClient for images ───────────────────────────
                services.AddHttpClient("ImageClient", c =>
                {
                    c.Timeout = TimeSpan.FromSeconds(10);
                    c.DefaultRequestHeaders.Add("User-Agent", "Sentrychan/2.0");
                }).AddPolicyHandler(retryPolicy);

                // ── Core Services ─────────────────────────────────────────
                services.AddSingleton<IConfigService, ConfigService>();
                services.AddSingleton<IAnimeQuizService, AnimeQuizService>();
                services.AddSingleton<IAnimeApiService, AnimeApiService>();
                services.AddSingleton<ISeriesService, SeriesService>();
                services.AddSingleton<IMangaService, MangaService>();
                services.AddSingleton<Sentrychan.Core.MangaLibrary.MangaLibraryService>();
                // Manga/novel sources: only the built-in Local source is compiled in. Every
                // online source is a source-pack plugin loaded at runtime (PluginSourceLoader),
                // so the shipped app carries no online sources of its own.
                services.AddSingleton<LocalMangaSourceService>();
                services.AddSingleton<IMangaSourceService>(sp => sp.GetRequiredService<LocalMangaSourceService>());
                services.AddSingleton<IMangaSourceRegistry, MangaSourceRegistry>();
                services.AddSingleton<IMangaDownloadService, MangaDownloadService>();
                // Opt-in Mihon extension bridge. Idle (no process, no network) until the user
                // turns it on; stopped with the host.
                services.AddSingleton<Sentrychan.Core.MihonBridge.MihonBridgeService>();
                services.AddHostedService(sp => sp.GetRequiredService<Sentrychan.Core.MihonBridge.MihonBridgeService>());
                services.AddTransient<Sentrychan.Core.MihonBackup.MihonBackupImporter>();
                services.AddSingleton<MangaUpdateService>();
                services.AddSingleton<Sentrychan.UI.Interfaces.IUpdateService, VelopackUpdateService>();
                services.AddSingleton<ITitleAliasService, TitleAliasService>();
                services.AddSingleton<DownloadQueueManager>();
                services.AddSingleton<IEpisodeNormalizer, EpisodeNormalizer>();
                services.AddSingleton<IWatchPartyHostService, WatchPartyHostService>();
                services.AddSingleton<IWatchPartyClientService, WatchPartyClientService>();
                services.AddSingleton<IPlayerBridgeService>(sp => new PlayerBridgeService(
                    sp.GetRequiredService<ILogger<PlayerBridgeService>>(),
                    sp.GetRequiredService<IDbContextFactory<AppDbContext>>(),
                    sp.GetRequiredService<ILoggerFactory>()));
                services.AddSingleton<ISyncEngine, SyncEngine>();
                services.AddSingleton<IReactionOverlayService, ReactionOverlayService>();
                services.AddSingleton<IVoiceChatService, VoiceChatService>();
                services.AddSingleton<ITunnelService, FrpTunnelService>();
                services.AddSingleton<ILanDiscoveryService, LanDiscoveryService>();
                services.AddSingleton<AniDbUdpClient>();
                services.AddSingleton<IAniDbApiService, AniDbApiService>();
                services.AddSingleton<IThemeService, ThemeService>();
                services.AddSingleton<ISecretModeService>(sp => (ISecretModeService)sp.GetRequiredService<IThemeService>());
                services.AddSingleton<IVideoFileLocator, VideoFileLocator>();
                services.AddSingleton<ITitleResolverService, TitleResolverService>();
                services.AddSingleton<ILibraryScanService, LibraryScanService>();
                services.AddSingleton<Sentrychan.Core.Services.AiringStatusRefreshService>();
                // MAL-backed schedule by default; a loaded source pack may register an override.
                services.AddSingleton<JikanAiringScheduleService>();
                services.AddSingleton<AiringScheduleRouter>();
                services.AddSingleton<IAiringScheduleService>(sp => sp.GetRequiredService<AiringScheduleRouter>());
                services.AddSingleton<IAiringScheduleRegistry>(sp => sp.GetRequiredService<AiringScheduleRouter>());
                services.AddSingleton<INotificationService>(_ => DesktopNotifications.ForCurrentOs());
                services.AddSingleton<QuoteService>();
                services.AddSingleton<IAccountService, SupabaseAccountService>();
                services.AddSingleton<IHyperbeamService, HyperbeamService>();
                services.AddSingleton<AniDbCoverService>();
                services.AddHttpClient("AniDb", client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(15);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Sentrychan/1.0");
                });

                // ── Download Backends ─────────────────────────────────────
                // Shared claim-list so the folder watcher doesn't fight the torrent
                // engine for files it is still writing.
                services.AddSingleton<IActiveTorrentFiles, ActiveTorrentFiles>();
                services.AddSingleton<Sentrychan.Core.Services.Backends.FdmBackend>();
                services.AddSingleton<Sentrychan.Core.Services.Backends.QBittorrentBackend>();
                services.AddSingleton<Sentrychan.Core.Services.Backends.MonoTorrentBackend>();
                services.AddSingleton<IDownloadBackendRouter, DownloadBackendRouter>();
                services.AddSingleton<IFileMovementPipeline, FileMovementPipeline>();
                // Holds no provider of its own: release-index providers come only from source packs.
                services.AddSingleton<IReleaseProviders, ReleaseProviders>();
                services.AddSingleton<IFillGapsService, FillGapsService>();
                services.AddSingleton<EpisodeRepairService>();
                services.AddSingleton<Sentrychan.Core.Library.LibraryTidyService>();
                services.AddSingleton<Sentrychan.Core.Sources.SourcesTransferService>();
                services.AddSingleton<Sentrychan.Core.Sources.SourcesChecker>();
                services.AddSingleton(sp => new SeasonFamilyService(
                    async (malId, ct) => (await sp.GetRequiredService<Sentrychan.Core.Services.Api.IJikanApi>().GetAnimeRelationsAsync(malId, ct)).Data,
                    sp.GetRequiredService<ITitleResolverService>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SeasonFamilyService>>()));
                services.AddSingleton<PluginSourceLoader>();
                services.AddSingleton<Sentrychan.Core.Sources.ISourcePackHost>(sp => sp.GetRequiredService<PluginSourceLoader>());
                services.AddSingleton<Sentrychan.Core.Vault.VaultService>();
                services.AddSingleton<IDownloadPickerService,Sentrychan.UI.Services.DownloadPickerService>();

                // ── MediatR ───────────────────────────────────────────────
                services.AddMediatR(cfg =>
                    cfg.RegisterServicesFromAssembly(
                        typeof(Sentrychan.Core.Events.NewEpisodeFoundEvent).Assembly));

                // UI notification handlers — MainWindowViewModel handles events from background services
                services.AddSingleton<MediatR.INotificationHandler<Sentrychan.Core.Events.NewEpisodeFoundEvent>>(
                    sp => sp.GetRequiredService<MainWindowViewModel>());
                services.AddSingleton<MediatR.INotificationHandler<Sentrychan.Core.Events.MonitorStatusEvent>>(
                    sp => sp.GetRequiredService<MainWindowViewModel>());
                services.AddSingleton<MediatR.INotificationHandler<Sentrychan.Core.Events.NewFileArrivedEvent>>(
                    sp => sp.GetRequiredService<MainWindowViewModel>());
                services.AddSingleton<MediatR.INotificationHandler<Sentrychan.Core.Events.UndoableEpisodeUpdateEvent>>(
                    sp => sp.GetRequiredService<MainWindowViewModel>());
                services.AddSingleton<MediatR.INotificationHandler<Sentrychan.Core.Events.UnmatchedFileEvent>>(
                    sp => sp.GetRequiredService<MainWindowViewModel>());
                services.AddSingleton<MediatR.INotificationHandler<Sentrychan.Core.Events.MultiSourceEpisodeEvent>>(
                    sp => sp.GetRequiredService<MainWindowViewModel>());
                services.AddSingleton<MediatR.INotificationHandler<Sentrychan.Core.Events.DownloadConfirmationEvent>>(
                    sp => sp.GetRequiredService<MainWindowViewModel>());

                // ── Background Services ───────────────────────────────────
                services.AddHostedService<RssMonitorService>();
                services.AddSingleton<IRssMonitorService>(sp =>
                    (IRssMonitorService)sp.GetServices<IHostedService>()
                        .First(s => s is RssMonitorService));

                services.AddHostedService<DownloadFolderWatcher>();
                services.AddSingleton<IDownloadFolderWatcher>(sp =>
                    (IDownloadFolderWatcher)sp.GetServices<IHostedService>()
                        .First(s => s is DownloadFolderWatcher));

                // ── ViewModels ────────────────────────────────────────────
                services.AddSingleton<MainWindowViewModel>(sp => new MainWindowViewModel(
                    sp.GetRequiredService<ISeriesService>(),
                    sp.GetRequiredService<IRssMonitorService>(),
                    sp.GetRequiredService<IAnimeApiService>(),
                    sp.GetRequiredService<IAniDbApiService>(),
                    sp.GetRequiredService<IDbContextFactory<AppDbContext>>(),
                    sp.GetRequiredService<DownloadQueueManager>(),
                    sp.GetRequiredService<IWatchPartyHostService>(),
                    sp.GetRequiredService<IWatchPartyClientService>(),
                    sp.GetRequiredService<IPlayerBridgeService>(),
                    sp.GetRequiredService<ISyncEngine>(),
                    sp.GetRequiredService<IReactionOverlayService>(),
                    sp.GetRequiredService<IVoiceChatService>(),
                    sp.GetRequiredService<ITunnelService>(),
                    sp.GetRequiredService<ILanDiscoveryService>(),
                    sp.GetRequiredService<AniDbUdpClient>(),
                    sp.GetRequiredService<IThemeService>(),
                    sp.GetRequiredService<QuoteService>(),
                    sp.GetRequiredService<IVideoFileLocator>(),
                    sp.GetRequiredService<ITitleAliasService>()));

                // ── Logging ───────────────────────────────────────────────
                services.AddLogging(logging =>
                {
                    // The app has no console window, so the console logger only burned a
                    // thread formatting lines nobody saw. Kept for a debugger's output pane.
                    if (System.Diagnostics.Debugger.IsAttached) logging.AddConsole();

                    // Daily rolling file logs in <AppPaths.DataDir>/logs — survive
                    // the WinExe (no-console) build so user reports are debuggable.
                    // Uses the maintained Serilog.Sinks.File directly; the old
                    // Serilog.Extensions.Logging.File wrapper is abandoned and dragged
                    // in ancient System.IO.* shims that broke self-contained publish.
                    // Test builds log in detail: a tester's log is the only view of their machine.
                    var serilog = new Serilog.LoggerConfiguration()
                        .MinimumLevel.Is(BuildInfo.IsTestBuild ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Information)
                        // AddSerilog passes every category through to Serilog, so these go here, not
                        // in logging filters: EF Core logged each SQL command (~0.9 MB a day).
                        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
                        .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
                        .WriteTo.File(
                            Path.Combine(_logDir, "sentrychan-.log"),
                            rollingInterval: Serilog.RollingInterval.Day,
                            retainedFileCountLimit: 7,
                            shared: true)
                        .CreateLogger();
                    logging.AddSerilog(serilog, dispose: true);

                    logging.SetMinimumLevel(BuildInfo.IsTestBuild
                        ? Microsoft.Extensions.Logging.LogLevel.Debug
                        : Microsoft.Extensions.Logging.LogLevel.Information);
                });
            });

        var host = hostBuilder.Build();

        var startLog = host.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("Startup");
        Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(startLog,
            "[Startup] {App} {Version} ({Flavor}{Test}) on {OS} {Arch}, .NET {Runtime}, data {Data}",
            BuildInfo.AppName, typeof(Program).Assembly.GetName().Version, BuildInfo.Flavor, BuildInfo.IsTestBuild ? ", test build" : "",
            System.Runtime.InteropServices.RuntimeInformation.OSDescription, System.Runtime.InteropServices.RuntimeInformation.OSArchitecture,
            Environment.Version, AppPaths.DataDir);
        if (BuildInfo.IsTestBuild)
        {
            Console.SetOut(new ConsoleToLog(startLog));
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Out));
        }

        // Apply migrations
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.Migrate();

            // Cleanup expired API cache entries
            var expired = db.ApiCaches.Where(c => c.ExpiresAt < DateTime.UtcNow);
            if (expired.Any())
            {
                db.ApiCaches.RemoveRange(expired);
                db.SaveChanges();
            }

            // Heal config values corrupted by the old ComboBox binding bug, which
            // stored the literal string "Avalonia.Controls.ComboBoxItem".
            var corrupted = db.AppConfigs
                .Where(c => c.Value == "Avalonia.Controls.ComboBoxItem")
                .ToList();
            if (corrupted.Count > 0)
            {
                foreach (var c in corrupted)
                {
                    c.Value = c.Key switch
                    {
                        "SelectedDownloadBackend" => "MonoTorrent",
                        "QualityPreference"       => "1080p",
                        "SelectedPlayer"          => "Internal",
                        _                          => string.Empty
                    };
                }
                db.SaveChanges();
                Console.WriteLine($"[Program] Healed {corrupted.Count} corrupted config value(s)");
            }

            // The add-feed form had the same bug, so a feed's quality could be that literal
            // too. The real choice can't be recovered; fall back to the global preference.
            var badFeeds = db.RssFeeds.Where(f => f.PreferredQuality != null && f.PreferredQuality.StartsWith("Avalonia.")).ToList();
            if (badFeeds.Count > 0)
            {
                foreach (var f in badFeeds) f.PreferredQuality = null;
                db.SaveChanges();
            }

            // Up to 1.0.5 a feed was switched off for good after 10 failed checks in a row, so a short
            // outage left it off (FeedBackoff now slows retries instead). Turn those back on, once.
            const string feedsRestoredKey = "AutoDisabledFeedsRestored";
            if (!db.AppConfigs.Any(c => c.Key == feedsRestoredKey))
            {
                foreach (var f in db.RssFeeds.Where(f => !f.IsEnabled && f.ConsecutiveFailures >= 10 && f.LastError != null))
                {
                    f.IsEnabled = true;
                    Console.WriteLine($"[Program] Turned back on a feed the old failure rule switched off: {f.Url}");
                }
                db.AppConfigs.Add(new Sentrychan.Core.Models.AppConfig { Key = feedsRestoredKey, Value = "true" });
                db.SaveChanges();
            }

            // "Auto-download only from" was a second list beside the preferred groups, saying nearly
            // the same thing. Its groups join the preferred list (after the ones already there, so the
            // order — the priority — is kept) and the release-group mode does its job from now on.
            var retired = db.AppConfigs.FirstOrDefault(c => c.Key == ReleaseGroupPolicy.RetiredAutoDownloadKey);
            if (retired != null)
            {
                var preferredRow = db.AppConfigs.FirstOrDefault(c => c.Key == ReleaseGroupPolicy.GroupsKey);
                var merged = ReleaseGroupPolicy.SplitGroups(preferredRow?.Value);
                merged.AddRange(ReleaseGroupPolicy.SplitGroups(retired.Value)
                    .Where(g => !merged.Contains(g, StringComparer.OrdinalIgnoreCase)));
                if (merged.Count > 0)
                {
                    if (preferredRow == null) db.AppConfigs.Add(new Sentrychan.Core.Models.AppConfig { Key = ReleaseGroupPolicy.GroupsKey, Value = string.Join(",", merged) });
                    else preferredRow.Value = string.Join(",", merged);
                }
                db.AppConfigs.Remove(retired);
                db.SaveChanges();
                Console.WriteLine($"[Program] Merged the auto-download groups into the preferred groups: {string.Join(", ", merged)}");
            }

            // No feeds are seeded here. The app ships knowing no content site; default feeds,
            // if any, come from a loaded source pack (see SeedProviderDefaults).
        }

        // Initialize download backend router — after the migrations: it reads its settings, and on
        // a first run there's no settings table before them.
        using (var scope = host.Services.CreateScope())
        {
            var router = scope.ServiceProvider.GetRequiredService<IDownloadBackendRouter>();
            router.InitializeAsync().GetAwaiter().GetResult();
        }

        // Hand service provider to Avalonia
        Sentrychan.UI.App.SetServiceProvider(host.Services);
        // The library layout of split shows reads their season chains.
        Sentrychan.Core.Library.SeasonLayout.Resolver = host.Services.GetRequiredService<ITitleResolverService>();
        Sentrychan.UI.App.Restart = RestartApp;

        // Logs and Windows notifications ask this before naming anything.
        var secretModeService = host.Services.GetRequiredService<ISecretModeService>();
        Sentrychan.Core.Vault.Privacy.SecretModeActive = () => secretModeService.IsSecretModeActive;

        // Register a post-init callback so host.StartAsync runs AFTER Avalonia's
        // Win32 dispatcher is installed (see note further down).
        Sentrychan.UI.App.PostInitAction = () =>
        {
            // Load source packs BEFORE the host starts. Hosted services — the RSS monitor in
            // particular — run their first check the moment the host starts, so any release
            // provider and the default feeds it brings must already be registered by then.
            var mangaRegistry = host.Services.GetRequiredService<Sentrychan.Core.Interfaces.IMangaSourceRegistry>();
            var packLoader = host.Services.GetRequiredService<PluginSourceLoader>();
            var transfer = host.Services.GetRequiredService<Sentrychan.Core.Sources.SourcesTransferService>();
            var releaseProviders = host.Services.GetRequiredService<IReleaseProviders>();
            try
            {
                // Sources put into the sources folder by hand, in whatever shape, are taken in first.
                transfer.TidySourcesFolderAsync().GetAwaiter().GetResult();
                packLoader.SeedAndLoad();
                transfer.ApplyPackDefaultsAsync(releaseProviders).GetAwaiter().GetResult();
            }
            catch (Exception ex) { LogCrash("Loading source packs", ex); }

            // Packs imported while the app runs load at once (a replaced one waits for a restart).
            Sentrychan.UI.App.LoadNewPacks = async () =>
            {
                var status = packLoader.LoadNewPacks();
                await transfer.ApplyPackDefaultsAsync(releaseProviders);
                RegisterImageSettings(mangaRegistry);
                return status;
            };

            // Bridged Mihon sources the user installed last time, from the saved list — the
            // server itself only starts when one of them is used.
            var mihonBridge = host.Services.GetRequiredService<Sentrychan.Core.MihonBridge.MihonBridgeService>();
            try { mihonBridge.InitializeAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { Console.WriteLine($"Mihon bridge init failed: {ex.Message}"); }
            Sentrychan.UI.Controls.AsyncImage.UrlResolver = url => mihonBridge.ResolveImageUrlAsync(url);

            RegisterImageSettings(mangaRegistry);
            // Extensions installed later join while the app runs.
            mihonBridge.SourcesChanged += sources =>
            {
                foreach (var src in sources.Where(s => s.IsAdultSource))
                    Sentrychan.UI.Controls.AsyncImage.RegisterPrivateSource(src.SourceName);
            };

            // Vault images (downloaded adult chapters) are decrypted in memory on display.
            var vaultService = host.Services.GetRequiredService<Sentrychan.Core.Vault.VaultService>();
            Sentrychan.UI.Controls.AsyncImage.VaultReader = id => vaultService.ReadAllAsync(id);

            host.StartAsync().GetAwaiter().GetResult();

            // Restore saved auth session (silent, non-blocking)
            _ = host.Services.GetRequiredService<IAccountService>()
                    .TryRestoreSessionAsync(CancellationToken.None);

            // Warm up the title resolver (downloads/indexes the offline anime DB).
            // Non-blocking — consumers fall back to legacy matching until IsReady.
            _ = host.Services.GetRequiredService<ITitleResolverService>()
                    .InitializeAsync(CancellationToken.None);

            // Wire backend completion events to the file movement pipeline
            var qbit        = host.Services.GetRequiredService<Sentrychan.Core.Services.Backends.QBittorrentBackend>();
            var monoTorrent = host.Services.GetRequiredService<Sentrychan.Core.Services.Backends.MonoTorrentBackend>();
            var pipeline    = host.Services.GetRequiredService<IFileMovementPipeline>();

            qbit.DownloadCompleted += async (evt) =>
            {
                await pipeline.OnBackendCompletedAsync(evt.Handle, evt.FilePath, evt.OriginalTitle, CancellationToken.None);
            };
            monoTorrent.DownloadCompleted += async (evt) =>
            {
                await pipeline.OnBackendCompletedAsync(evt.Handle, evt.FilePath, evt.OriginalTitle, CancellationToken.None);
            };

            // A stalled torrent never reaches Seeding, so it would never raise a
            // completion event — previously it just sat unfinished with nothing shown.
            // Surface it instead, and say why (0 peers = connectivity, not the release).
            monoTorrent.DownloadStalled += (evt) =>
            {
                var notifier = host.Services.GetRequiredService<INotificationService>();
                var reason = evt.PeersAvailable == 0
                    ? "no peers reachable"
                    : $"{evt.PeersAvailable} peers, no progress";
                notifier.Notify(
                    "Sentrychan · download stalled",
                    $"{Sentrychan.Core.Vault.Privacy.Name(evt.OriginalTitle, evt.Handle)} stuck at {evt.ProgressPercent:F0}% ({reason})");
            };

            // Downloads that were still running when the app closed pick up where they
            // left off — only now that completion is wired, so none can finish unheard.
            // Then jobs held under the MaxConcurrentDownloads cap (Pending rows) get the
            // slots that are left.
            _ = Task.Run(async () =>
            {
                // The vault holds the list of private downloads; it must be open before any
                // resumed torrent can finish, or a private one would be filed in the library.
                try { await host.Services.GetRequiredService<Sentrychan.Core.Vault.VaultService>().EnsureReadyAsync(); }
                catch (Exception ex) { Console.WriteLine($"Vault open failed: {ex.Message}"); }
                // The other flavour is running and resuming its own copy of these jobs; two
                // engines writing the same files would corrupt them.
                if (InstanceGuard.PausedForOtherInstance) return;
                try { await monoTorrent.RestoreAsync(CancellationToken.None); }
                catch (Exception ex) { Console.WriteLine($"Torrent restore failed: {ex.Message}"); }
                await host.Services.GetRequiredService<Sentrychan.Core.Services.DownloadQueueManager>()
                          .PromotePendingAsync(CancellationToken.None);
            });

            // "Sentrychan.App.exe --play <file>": open a video straight in the built-in player
            // (what an "Open with" entry would pass).
            var playIndex = Array.IndexOf(args, "--play");
            if (playIndex >= 0 && playIndex + 1 < args.Length && File.Exists(args[playIndex + 1]))
            {
                var toPlay = args[playIndex + 1];
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    _ = Sentrychan.UI.Services.PlayerLauncher.PlayFileAsync(toPlay));
            }

            // Load notification preferences (level + Windows-vs-in-app).
            try
            {
                using var cfgDb = host.Services
                    .GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<AppDbContext>>()
                    .CreateDbContext();
                var level = cfgDb.AppConfigs.FirstOrDefault(c => c.Key == NotificationSettings.LevelKey)?.Value;
                var win   = cfgDb.AppConfigs.FirstOrDefault(c => c.Key == NotificationSettings.WindowsKey)?.Value != "false";
                NotificationSettings.Apply(level, win);
            }
            catch { /* defaults (Important + Windows) apply */ }
        };

        // Graceful shutdown hooks
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        };
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        };

        // NOTE: host.StartAsync() is intentionally deferred until AFTER Avalonia's
        // Win32 dispatcher has been installed. Starting hosted services here would
        // trigger MediatR.Publish from RssMonitorService which resolves MainWindowViewModel,
        // whose ReactiveCommands touch Dispatcher.UIThread and lock in the managed
        // dispatcher — making Avalonia.MainLoop throw PlatformNotSupportedException.
        // The host is started from App.OnFrameworkInitializationCompleted after the
        // MainWindow is created. See Sentrychan.UI.App.

        // Launch Avalonia UI — must run on the main STA thread
        try
        {
            AppBuilder.Configure<Sentrychan.UI.App>()
                .UsePlatformDetect()
                // Avalonia's default Windows compositor (WinUI) wakes every display frame for as
                // long as a window exists, hidden or not: ~2-3% of a core around the clock for an
                // app that lives in the tray. The redirection surface only draws when something
                // changes (~0.2-0.6% measured). It can't do Mica/acrylic, which nothing here shows.
                .With(new Win32PlatformOptions { CompositionMode = [Win32CompositionMode.RedirectionSurface] })
                .WithInterFont()
                .UseReactiveUI()
                .LogToTrace()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            LogCrash("Avalonia lifetime", ex);
            throw;
        }

        host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Teaches AsyncImage which manga sources need a Referer for their image CDN, and which are
    /// adult sources whose images must never reach the disk cache. Safe to repeat.
    /// </summary>
    private static void RegisterImageSettings(Sentrychan.Core.Interfaces.IMangaSourceRegistry registry)
    {
        foreach (var src in registry.Sources)
        {
            if (!string.IsNullOrEmpty(src.ImageReferer))
                Sentrychan.UI.Controls.AsyncImage.RegisterReferer(src.SourceName, src.ImageReferer!);
            if (src.IsAdultSource)
                Sentrychan.UI.Controls.AsyncImage.RegisterPrivateSource(src.SourceName);
        }
    }

    /// <summary>
    /// Waits for a second launch to ask for the window, then brings this instance's main window
    /// to the front instead of letting a duplicate start.
    /// </summary>
    private static void StartShowWindowListener()
    {
        _showWindowListener = ShowWindowSignal.Listen(BuildInfo.IsPreview, () =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (Avalonia.Application.Current?.ApplicationLifetime
                        is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d
                    && d.MainWindow is { } w)
                {
                    w.Show();
                    w.WindowState = Avalonia.Controls.WindowState.Normal;
                    w.Activate();
                }
            }));
    }

    /// <summary>
    /// Starts a new instance and shuts this one down. The new one waits for this one's
    /// single-instance mutex (see <see cref="RestartedArg"/>), so the two never run together.
    /// </summary>
    private static void RestartApp()
    {
        // Relaunch the way this process was launched: the installed exe, or `dotnet <dll>` in development.
        var exe = Environment.ProcessPath!;
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(typeof(Program).Assembly.Location);
        psi.ArgumentList.Add(RestartedArg);
        System.Diagnostics.Process.Start(psi);

        if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d)
            d.Shutdown();
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var path = Path.Combine(_logDir, "crash.log");
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{ex}\n\n";
            File.AppendAllText(path, entry);
        }
        catch { /* nothing more we can do */ }
    }
}
