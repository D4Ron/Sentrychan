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

namespace Sentrychan.App;

public static class Program
{
    private static string _logDir = string.Empty;

    // Single-instance plumbing. Kept alive for the process lifetime.
    private static System.Threading.Mutex? _instanceMutex;
    private const string MutexName = @"Global\Sentrychan_SingleInstance";
    private const string ShowEventName = @"Global\Sentrychan_ShowWindow";

    [System.STAThread]
    public static void Main(string[] args)
    {
        // MUST be the very first thing that runs. Velopack's installer/updater
        // invokes the app with hook arguments (--velopack-install, etc.) during
        // install/update/uninstall; this call handles them and exits before any of
        // our own startup runs. Skipped entirely on a normal launch.
        Velopack.VelopackApp.Build().Run();

        // ── Single instance ────────────────────────────────────────
        // Without this, launching Sentrychan again (or the tray leaving one running
        // while another starts) spins up a SECOND app — each with its own RSS/manga
        // monitors firing their own notifications. That's the real cause of "too many
        // notifications". Second launches signal the running copy to show, then exit.
        _instanceMutex = new System.Threading.Mutex(true, MutexName, out var isFirst);
        if (!isFirst)
        {
            try
            {
                using var ev = System.Threading.EventWaitHandle.OpenExisting(ShowEventName);
                ev.Set(); // ask the running instance to surface its window
            }
            catch { /* running instance may be mid-startup — just exit quietly */ }
            return;
        }
        StartShowWindowListener();

        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Sentrychan");
        Directory.CreateDirectory(appDataPath);
        var dbPath = Path.Combine(appDataPath, "sentrychan.db");

        _logDir = Path.Combine(appDataPath, "logs");
        Directory.CreateDirectory(_logDir);

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
                // Manga/novel sources: only the built-in Local source is compiled in. Every
                // online source is a source-pack plugin loaded at runtime (PluginSourceLoader),
                // so the shipped app carries no online sources of its own.
                services.AddSingleton<LocalMangaSourceService>();
                services.AddSingleton<IMangaSourceService>(sp => sp.GetRequiredService<LocalMangaSourceService>());
                services.AddSingleton<IMangaSourceRegistry, MangaSourceRegistry>();
                services.AddSingleton<IMangaDownloadService, MangaDownloadService>();
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
                services.AddSingleton<IAiringScheduleService, SubsPleaseScheduleService>();
                services.AddSingleton<INotificationService, WindowsNotificationService>();
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
                services.AddSingleton<INyaaSearchService, NyaaSearchService>();
                services.AddSingleton<IFillGapsService, FillGapsService>();
                services.AddSingleton<IDownloadPickerService, Sentrychan.UI.Services.DownloadPickerService>();

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
                    logging.AddConsole();

                    // Daily rolling file logs in %APPDATA%/Sentrychan/logs — survive
                    // the WinExe (no-console) build so user reports are debuggable.
                    // Uses the maintained Serilog.Sinks.File directly; the old
                    // Serilog.Extensions.Logging.File wrapper is abandoned and dragged
                    // in ancient System.IO.* shims that broke self-contained publish.
                    var serilog = new Serilog.LoggerConfiguration()
                        .MinimumLevel.Information()
                        .WriteTo.File(
                            Path.Combine(_logDir, "sentrychan-.log"),
                            rollingInterval: Serilog.RollingInterval.Day,
                            retainedFileCountLimit: 7,
                            shared: true)
                        .CreateLogger();
                    logging.AddSerilog(serilog, dispose: true);

                    logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
                });
            });

        var host = hostBuilder.Build();

        // Initialize download backend router
        using (var scope = host.Services.CreateScope())
        {
            var router = scope.ServiceProvider.GetRequiredService<IDownloadBackendRouter>();
            router.InitializeAsync().GetAwaiter().GetResult();
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

            // Seed the default sources so the app works out of the box (bundled build).
            if (!db.RssFeeds.Any())
            {
                db.RssFeeds.Add(new Sentrychan.Core.Models.RssFeed
                {
                    Url       = "https://nyaa.si/?page=rss",
                    FeedType  = Sentrychan.Core.Models.FeedType.Priority,
                    IsEnabled = true,
                    AddedAt   = DateTime.UtcNow
                });
                db.RssFeeds.Add(new Sentrychan.Core.Models.RssFeed
                {
                    Url              = "https://subsplease.org/rss/?t&h=1080",
                    FeedType         = Sentrychan.Core.Models.FeedType.Priority,
                    PreferredQuality = "1080p",
                    IsEnabled        = true,
                    AddedAt          = DateTime.UtcNow
                });
                db.SaveChanges();
                Console.WriteLine("[Program] Seeded default RSS feeds: nyaa.si + SubsPlease 1080p");
            }
        }

        // Hand service provider to Avalonia
        Sentrychan.UI.App.SetServiceProvider(host.Services);

        // Register a post-init callback so host.StartAsync runs AFTER Avalonia's
        // Win32 dispatcher is installed (see note further down).
        Sentrychan.UI.App.PostInitAction = () =>
        {
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
                    $"{evt.OriginalTitle} stuck at {evt.ProgressPercent:F0}% ({reason})");
            };

            // Jobs held under the MaxConcurrentDownloads cap survive a restart as
            // Pending rows — give them a chance to start now that slots are empty.
            _ = host.Services.GetRequiredService<Sentrychan.Core.Services.DownloadQueueManager>()
                    .PromotePendingAsync(CancellationToken.None);

            // Load source-pack plugins (seeding any bundled pack into the user folder first),
            // BEFORE the referer seeding below so plugin sources are registered by then.
            var mangaRegistry = host.Services.GetRequiredService<Sentrychan.Core.Interfaces.IMangaSourceRegistry>();
            try
            {
                var srcLog = host.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
                                 .CreateLogger("Sources");
                PluginSourceLoader.SeedAndLoad(host.Services, mangaRegistry, srcLog);
            }
            catch (Exception ex) { Console.WriteLine($"Source plugin load failed: {ex.Message}"); }

            // Teach AsyncImage which manga sources need a Referer for their image CDN.
            foreach (var src in mangaRegistry.Sources)
                if (!string.IsNullOrEmpty(src.ImageReferer))
                    Sentrychan.UI.Controls.AsyncImage.RegisterReferer(src.SourceName, src.ImageReferer!);

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
    /// Waits (on a background thread) for a second launch to signal the show-window
    /// event, then brings this instance's main window to the front instead of letting
    /// a duplicate start.
    /// </summary>
    private static void StartShowWindowListener()
    {
        var thread = new System.Threading.Thread(() =>
        {
            using var ev = new System.Threading.EventWaitHandle(
                false, System.Threading.EventResetMode.AutoReset, ShowEventName);
            while (true)
            {
                ev.WaitOne();
                try
                {
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
                    });
                }
                catch { /* app may be shutting down */ }
            }
        })
        { IsBackground = true, Name = "ShowWindowListener" };
        thread.Start();
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
