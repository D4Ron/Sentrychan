using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using Sentrychan.Core.Data;
using Sentrychan.Core.Models;
using Sentrychan.Core.Interfaces;
using System;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Unit = System.Reactive.Unit;
using System.IO;
using System.Collections.ObjectModel;
using System.Linq;
using Sentrychan.UI.Services;
using Sentrychan.UI.Interfaces;

namespace Sentrychan.UI.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly IDbContextFactory<AppDbContext>? _dbFactory;
    private readonly IAnimeApiService? _apiService;
    private readonly ISeriesService? _seriesService;
    private readonly IThemeService? _themeService;
    private readonly Window? _owner;
    private readonly IDownloadBackendRouter? _backendRouter;
    private readonly IDownloadFolderWatcher? _folderWatcher;
    private string _statusMessage = string.Empty;

    // ── RSS Feeds sub-section ──────────────────────────────────────
    public RssFeedsViewModel? RssFeedsVm { get; private set; }

    private string _fdmPath = string.Empty;
    public string FdmPath
    {
        get => _fdmPath;
        set => this.RaiseAndSetIfChanged(ref _fdmPath, value);
    }

    private string _downloadPath = string.Empty;
    public string DownloadPath
    {
        get => _downloadPath;
        set => this.RaiseAndSetIfChanged(ref _downloadPath, value);
    }

    private string _libraryPath = string.Empty;
    public string LibraryPath
    {
        get => _libraryPath;
        set => this.RaiseAndSetIfChanged(ref _libraryPath, value);
    }

    private string _checkInterval = "15";
    public string CheckInterval
    {
        get => _checkInterval;
        set => this.RaiseAndSetIfChanged(ref _checkInterval, value);
    }

    // Option lists for combo boxes. Binding ComboBox.ItemsSource to plain strings
    // (instead of <ComboBoxItem> children) ensures SelectedItem stores the string
    // value, not "Avalonia.Controls.ComboBoxItem".
    public string[] QualityOptions { get; } = { "1080p", "720p", "480p", "Any" };
    public string[] BackendOptions { get; } = { "MonoTorrent", "QBittorrent", "SystemDefault" };
    public string[] PlayerOptions  { get; } = { "Internal", "MPV", "VLC" };

    private string _qualityPreference = "1080p";
    public string QualityPreference
    {
        get => _qualityPreference;
        set => this.RaiseAndSetIfChanged(ref _qualityPreference, value);
    }
    
    private string _preferredReleaseGroups = "SubsPlease,Erai-raws,HorribleSubs";
    public string PreferredReleaseGroups
    {
        get => _preferredReleaseGroups;
        set => this.RaiseAndSetIfChanged(ref _preferredReleaseGroups, value);
    }

    private string _autoDownloadGroups = string.Empty;
    public string AutoDownloadGroups
    {
        get => _autoDownloadGroups;
        set => this.RaiseAndSetIfChanged(ref _autoDownloadGroups, value);
    }
    
    private string _vlcPath = "vlc";
    public string VlcPath
    {
        get => _vlcPath;
        set => this.RaiseAndSetIfChanged(ref _vlcPath, value);
    }

    private string _selectedPlayer = "Internal";
    public string SelectedPlayer
    {
        get => _selectedPlayer;
        set => this.RaiseAndSetIfChanged(ref _selectedPlayer, value);
    }

    private string _malUsername = string.Empty;
    private string _malImportResult = string.Empty;

    private string _qBitUrl = "http://localhost:8080";
    public string QBitUrl
    {
        get => _qBitUrl;
        set => this.RaiseAndSetIfChanged(ref _qBitUrl, value);
    }

    private string _qBitUsername = "admin";
    public string QBitUsername
    {
        get => _qBitUsername;
        set => this.RaiseAndSetIfChanged(ref _qBitUsername, value);
    }

    private string _qBitPassword = "adminadmin";
    public string QBitPassword
    {
        get => _qBitPassword;
        set => this.RaiseAndSetIfChanged(ref _qBitPassword, value);
    }

    private string _qBitTestResult = string.Empty;
    public string QBitTestResult
    {
        get => _qBitTestResult;
        set => this.RaiseAndSetIfChanged(ref _qBitTestResult, value);
    }

    private string _selectedDownloadBackend = "SystemDefault";
    public string SelectedDownloadBackend
    {
        get => _selectedDownloadBackend;
        set => this.RaiseAndSetIfChanged(ref _selectedDownloadBackend, value);
    }

    // ── Appearance ─────────────────────────────────────────────────
    public string[] LayoutOptions { get; } = ["Sidebar", "Top Bar"];

    private string _layoutMode = "Sidebar";
    public string LayoutMode
    {
        get => _layoutMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _layoutMode, value);
            // Apply live to the running main window.
            if (Avalonia.Application.Current?.ApplicationLifetime
                    is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d
                && d.MainWindow?.DataContext is MainWindowViewModel mvm && !string.IsNullOrEmpty(value))
                mvm.LayoutMode = value;
        }
    }

    private string _selectedTheme = "Yoru";
    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedTheme, value);
            // Live preview — apply the palette the moment the user picks it.
            if (!string.IsNullOrEmpty(value)) _themeService?.ApplyNamedTheme(value);
        }
    }

    private string _backgroundImagePath = string.Empty;
    public string BackgroundImagePath
    {
        get => _backgroundImagePath;
        set => this.RaiseAndSetIfChanged(ref _backgroundImagePath, value);
    }

    public string MalUsername
    {
        get => _malUsername;
        set => this.RaiseAndSetIfChanged(ref _malUsername, value);
    }

    public string MalImportResult
    {
        get => _malImportResult;
        set => this.RaiseAndSetIfChanged(ref _malImportResult, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    private bool _isStatusError;
    /// <summary>Colours the footer status line red. Set by <see cref="Fail"/>.</summary>
    public bool IsStatusError
    {
        get => _isStatusError;
        set => this.RaiseAndSetIfChanged(ref _isStatusError, value);
    }

    /// <summary>Report a validation failure that aborted the save.</summary>
    private void Fail(string message)
    {
        IsStatusError = true;
        StatusMessage = message;
    }

    /// <summary>Report success, clearing any previous error state.</summary>
    private void Succeed(string message)
    {
        IsStatusError = false;
        StatusMessage = message;
    }

    // ── About ──────────────────────────────────────────────────────
    public string AppVersion =>
        "v" + (System.Reflection.Assembly.GetEntryAssembly()?
            .GetName().Version?.ToString(3) ?? "1.0.0");

    public string LogFolderPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sentrychan", "logs");

    public ReactiveCommand<Unit, Unit> OpenLogsCommand { get; } =
        ReactiveCommand.Create(() =>
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sentrychan", "logs");
            if (System.IO.Directory.Exists(dir))
                System.Diagnostics.Process.Start("explorer.exe", dir);
        });

    public ReactiveCommand<Unit, Unit> ShowTutorialCommand { get; } =
        ReactiveCommand.CreateFromTask(async () =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                    is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d
                && d.MainWindow?.DataContext is MainWindowViewModel mvm)
                await mvm.ShowTutorialAsync();
        });

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    // ── Update check ───────────────────────────────────────────────
    private string _updateStatus = string.Empty;
    public string UpdateStatus { get => _updateStatus; set => this.RaiseAndSetIfChanged(ref _updateStatus, value); }

    private bool _isCheckingUpdate;
    public bool IsCheckingUpdate { get => _isCheckingUpdate; set => this.RaiseAndSetIfChanged(ref _isCheckingUpdate, value); }

    private bool _updateAvailable;
    public bool UpdateAvailable { get => _updateAvailable; set => this.RaiseAndSetIfChanged(ref _updateAvailable, value); }

    public ReactiveCommand<Unit, Unit> CheckForUpdatesCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyUpdateCommand { get; }

    private static IUpdateService? Updater =>
        App.Services?.GetService(typeof(IUpdateService)) as IUpdateService;

    private async Task CheckForUpdatesAsync()
    {
        var updater = Updater;
        if (updater == null) { UpdateStatus = "Update service unavailable."; return; }
        if (!updater.IsSupported)
        {
            UpdateStatus = "Updates apply to the installed version only (you're running a dev/portable build).";
            return;
        }

        IsCheckingUpdate = true;
        UpdateStatus = "Checking for updates…";
        UpdateAvailable = false;
        try
        {
            var version = await updater.CheckForUpdateAsync();
            if (string.IsNullOrEmpty(version))
                UpdateStatus = "You're on the latest version.";
            else
            {
                UpdateAvailable = true;
                UpdateStatus = $"Version {version} is available.";
            }
        }
        catch (Exception ex) { UpdateStatus = $"Check failed: {ex.Message}"; }
        finally { IsCheckingUpdate = false; }
    }

    private async Task ApplyUpdateAsync()
    {
        var updater = Updater; if (updater == null) return;
        UpdateStatus = "Downloading update…";
        await updater.DownloadAndRestartAsync(); // restarts on success
        UpdateStatus = "Update failed to apply. See logs.";
    }

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> BrowseFdmCommand { get; }
    public ReactiveCommand<Unit, Unit> BrowseDownloadCommand { get; }
    public ReactiveCommand<Unit, Unit> BrowseLibraryCommand { get; }
    public ReactiveCommand<Unit, Unit> BrowseBackgroundImageCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportSourcePackCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenSourcesFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearBackgroundImageCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportMalWatchingCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportLibraryCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportLibraryCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetLibraryCommand { get; }
    public ReactiveCommand<Unit, Unit> TestQBitConnectionCommand { get; }

    // Design-time
    public SettingsViewModel()
    {
        _dbFactory = null;
        _owner = null;
        SaveCommand = ReactiveCommand.CreateFromTask(async ct => await SaveAsync(ct));
        BrowseFdmCommand = ReactiveCommand.CreateFromTask(async ct => await BrowseFdmAsync(ct));
        BrowseDownloadCommand = ReactiveCommand.CreateFromTask(async ct => await BrowseDownloadAsync(ct));
        BrowseLibraryCommand = ReactiveCommand.CreateFromTask(async ct => await BrowseLibraryAsync(ct));
        ImportSourcePackCommand = ReactiveCommand.CreateFromTask(ImportSourcePackAsync);
        OpenSourcesFolderCommand = ReactiveCommand.Create(OpenSourcesFolder);
        BrowseBackgroundImageCommand = ReactiveCommand.CreateFromTask(async ct => await BrowseBackgroundImageAsync(ct));
        ClearBackgroundImageCommand = ReactiveCommand.Create(() => { BackgroundImagePath = string.Empty; });
        ImportMalWatchingCommand = ReactiveCommand.CreateFromTask(async ct => await ImportMalWatchingAsync(ct));
        ExportLibraryCommand = ReactiveCommand.CreateFromTask(ExportLibraryAsync);
        ImportLibraryCommand = ReactiveCommand.CreateFromTask(ImportLibraryAsync);
        ResetLibraryCommand = ReactiveCommand.CreateFromTask(async ct => await ResetLibraryAsync(ct));
        TestQBitConnectionCommand = ReactiveCommand.CreateFromTask(TestQBitConnectionAsync);
        CheckForUpdatesCommand = ReactiveCommand.CreateFromTask(CheckForUpdatesAsync);
        ApplyUpdateCommand = ReactiveCommand.CreateFromTask(ApplyUpdateAsync);
    }

    // Runtime
    public SettingsViewModel(
        IDbContextFactory<AppDbContext> dbFactory,
        IAnimeApiService apiService,
        ISeriesService seriesService,
        IThemeService themeService,
        Window owner,
        IDownloadBackendRouter backendRouter,
        IRssMonitorService rssMonitor,
        IDownloadFolderWatcher folderWatcher) : this()
    {
        _dbFactory = dbFactory;
        _apiService = apiService;
        _seriesService = seriesService;
        _themeService = themeService;
        _owner = owner;
        _backendRouter = backendRouter;
        _folderWatcher = folderWatcher;
        BrowseLibraryCommand = ReactiveCommand.CreateFromTask(async ct => await BrowseLibraryAsync(ct));
        ImportSourcePackCommand = ReactiveCommand.CreateFromTask(ImportSourcePackAsync);
        OpenSourcesFolderCommand = ReactiveCommand.Create(OpenSourcesFolder);
        ResetLibraryCommand = ReactiveCommand.CreateFromTask(async ct => await ResetLibraryAsync(ct));
        TestQBitConnectionCommand = ReactiveCommand.CreateFromTask(TestQBitConnectionAsync);
        CheckForUpdatesCommand = ReactiveCommand.CreateFromTask(CheckForUpdatesAsync);
        ApplyUpdateCommand = ReactiveCommand.CreateFromTask(ApplyUpdateAsync);

        RssFeedsVm = new RssFeedsViewModel(dbFactory, themeService, rssMonitor);
    }

    // Call after construction to populate fields from DB
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_dbFactory == null) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        FdmPath = await GetConfig(db, "FdmPath", @"C:\Program Files\Free Download Manager\fdm.exe", ct);
        DownloadPath = await GetConfig(db, "DownloadPath", "", ct);
        LibraryPath = await GetConfig(db, "LibraryPath", "", ct);
        CheckInterval = await GetConfig(db, "CheckIntervalMinutes", "15", ct);
        QualityPreference = await GetConfig(db, "QualityPreference", "1080p", ct);

        // "0" is stored for unlimited; show it as blank so the watermark reads through.
        var down = await GetConfig(db, Sentrychan.Core.Services.Backends.MonoTorrentBackend.MaxDownloadKey, "0", ct);
        var up   = await GetConfig(db, Sentrychan.Core.Services.Backends.MonoTorrentBackend.MaxUploadKey, "0", ct);
        MaxDownloadRate = down == "0" ? string.Empty : down;
        MaxUploadRate   = up   == "0" ? string.Empty : up;

        var conc = await GetConfig(db, Sentrychan.Core.Services.DownloadQueueManager.MaxConcurrentKey, "0", ct);
        MaxConcurrentDownloads = conc == "0" ? string.Empty : conc;

        AutoRemoveCompleted = await GetConfig(db,
            Sentrychan.Core.Services.AiringStatusRefreshService.AutoRemoveKey, "false", ct) == "true";

        LocalMangaPath = await GetConfig(db,
            Sentrychan.Core.Services.LocalMangaSourceService.RootConfigKey, "", ct);

        NotificationLevel = await GetConfig(db, Sentrychan.Core.Services.NotificationSettings.LevelKey, "Important", ct);
        WindowsNotifications = await GetConfig(db, Sentrychan.Core.Services.NotificationSettings.WindowsKey, "true", ct) == "true";
        WatchFolderMode = await GetConfig(db, "DownloadOrganizeMode", "Own", ct) == "Watch";
        PreferredReleaseGroups = await GetConfig(db, "PreferredReleaseGroups", "SubsPlease,Erai-raws,HorribleSubs", ct);
        AutoDownloadGroups = await GetConfig(db, "AutoDownloadGroups", "", ct);
        VlcPath = await GetConfig(db, "VlcPath", "vlc", ct);
        SelectedPlayer = await GetConfig(db, "SelectedPlayer", "Internal", ct);
        MalUsername = await GetConfig(db, "MalUsername", "", ct);

        QBitUrl      = await GetConfig(db, "QBitUrl",      "http://localhost:8080", ct);
        QBitUsername = await GetConfig(db, "QBitUsername",  "admin",        ct);
        // Decrypt for editing; legacy plaintext values pass through untouched and get
        // re-written encrypted on the next save.
        QBitPassword = Sentrychan.Core.Services.SecretProtector.Unprotect(
            await GetConfig(db, "QBitPassword", "adminadmin", ct));
        SelectedDownloadBackend = await GetConfig(db, "SelectedDownloadBackend", "SystemDefault", ct);

        // Appearance
        _selectedTheme = await GetConfig(db, "SelectedTheme", "Yoru", ct);
        _layoutMode = await GetConfig(db, "LayoutMode", "Sidebar", ct);
        this.RaisePropertyChanged(nameof(LayoutMode));
        this.RaisePropertyChanged(nameof(SelectedTheme));
        BackgroundImagePath = await GetConfig(db, "BackgroundImagePath", "", ct);
    }

    // ── Speed limits (integrated downloader) ───────────────────────
    // KB/s. Empty or 0 means unlimited, which is the default.
    private string _maxDownloadRate = string.Empty;
    public string MaxDownloadRate
    {
        get => _maxDownloadRate;
        set => this.RaiseAndSetIfChanged(ref _maxDownloadRate, value);
    }

    private string _maxUploadRate = string.Empty;
    public string MaxUploadRate
    {
        get => _maxUploadRate;
        set => this.RaiseAndSetIfChanged(ref _maxUploadRate, value);
    }

    // Max simultaneous downloads. Empty or 0 = unlimited.
    private string _maxConcurrentDownloads = string.Empty;
    public string MaxConcurrentDownloads
    {
        get => _maxConcurrentDownloads;
        set => this.RaiseAndSetIfChanged(ref _maxConcurrentDownloads, value);
    }

    // Opt-in: drop a series from the library (DB only, files kept) once it has
    // finished airing AND every episode is downloaded.
    private bool _autoRemoveCompleted;
    public bool AutoRemoveCompleted
    {
        get => _autoRemoveCompleted;
        set => this.RaiseAndSetIfChanged(ref _autoRemoveCompleted, value);
    }

    // Folder the "Local" manga source reads (manga you already have on disk).
    private string _localMangaPath = string.Empty;
    public string LocalMangaPath
    {
        get => _localMangaPath;
        set => this.RaiseAndSetIfChanged(ref _localMangaPath, value);
    }

    // ── Notifications ──────────────────────────────────────────────
    public string[] NotificationLevels { get; } = ["Off", "Important", "All"];

    private string _notificationLevel = "Important";
    public string NotificationLevel
    {
        get => _notificationLevel;
        set => this.RaiseAndSetIfChanged(ref _notificationLevel, value);
    }

    private bool _windowsNotifications = true;
    public bool WindowsNotifications
    {
        get => _windowsNotifications;
        set => this.RaiseAndSetIfChanged(ref _windowsNotifications, value);
    }

    // Download-folder organizing. Off (default) = only files Sentrychan downloaded or
    // that match a tracked series are organized; unrelated files are left alone.
    // On = treat the whole download folder as an anime dropzone (watch-folder mode).
    private bool _watchFolderMode;
    public bool WatchFolderMode
    {
        get => _watchFolderMode;
        set => this.RaiseAndSetIfChanged(ref _watchFolderMode, value);
    }

    private static bool IsValidRate(string value) =>
        string.IsNullOrWhiteSpace(value) || (int.TryParse(value, out var n) && n >= 0);

    private async Task SaveAsync(CancellationToken ct)
    {
        if (_dbFactory == null) return;

        if (!int.TryParse(CheckInterval, out var interval) || interval < 1)
        {
            Fail("Check interval must be a number greater than 0 — see the General tab");
            return;
        }

        if (!IsValidRate(MaxDownloadRate) || !IsValidRate(MaxUploadRate))
        {
            Fail("Speed limits must be a whole number of KB/s, or blank for unlimited — see the Downloads tab");
            return;
        }

        if (!IsValidRate(MaxConcurrentDownloads))
        {
            Fail("Max simultaneous downloads must be a whole number, or blank for unlimited — see the Downloads tab");
            return;
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            await SetConfig(db, "FdmPath", FdmPath, ct);
            await SetConfig(db, "DownloadPath", DownloadPath, ct);
            await SetConfig(db, "LibraryPath", LibraryPath, ct);
            await SetConfig(db, "CheckIntervalMinutes", CheckInterval, ct);
            await SetConfig(db, "QualityPreference", QualityPreference, ct);
            await SetConfig(db, "PreferredReleaseGroups", PreferredReleaseGroups, ct);
            await SetConfig(db, "AutoDownloadGroups", AutoDownloadGroups, ct);
            await SetConfig(db, "VlcPath", VlcPath, ct);
            await SetConfig(db, "SelectedPlayer", SelectedPlayer, ct);
            await SetConfig(db, "MalUsername", MalUsername, ct);

            await SetConfig(db, "QBitUrl",                QBitUrl,                ct);
            await SetConfig(db, "QBitUsername",           QBitUsername,           ct);
            await SetConfig(db, "QBitPassword",
                Sentrychan.Core.Services.SecretProtector.Protect(QBitPassword), ct);
            await SetConfig(db, "SelectedDownloadBackend", SelectedDownloadBackend, ct);

            // Blank → "0" (unlimited)
            await SetConfig(db, Sentrychan.Core.Services.Backends.MonoTorrentBackend.MaxDownloadKey,
                string.IsNullOrWhiteSpace(MaxDownloadRate) ? "0" : MaxDownloadRate.Trim(), ct);
            await SetConfig(db, Sentrychan.Core.Services.Backends.MonoTorrentBackend.MaxUploadKey,
                string.IsNullOrWhiteSpace(MaxUploadRate) ? "0" : MaxUploadRate.Trim(), ct);
            await SetConfig(db, Sentrychan.Core.Services.DownloadQueueManager.MaxConcurrentKey,
                string.IsNullOrWhiteSpace(MaxConcurrentDownloads) ? "0" : MaxConcurrentDownloads.Trim(), ct);
            await SetConfig(db, Sentrychan.Core.Services.AiringStatusRefreshService.AutoRemoveKey,
                AutoRemoveCompleted ? "true" : "false", ct);
            await SetConfig(db, Sentrychan.Core.Services.LocalMangaSourceService.RootConfigKey,
                LocalMangaPath?.Trim() ?? string.Empty, ct);
            await SetConfig(db, Sentrychan.Core.Services.NotificationSettings.LevelKey, NotificationLevel, ct);
            await SetConfig(db, Sentrychan.Core.Services.NotificationSettings.WindowsKey,
                WindowsNotifications ? "true" : "false", ct);
            await SetConfig(db, "DownloadOrganizeMode", WatchFolderMode ? "Watch" : "Own", ct);

            // Appearance
            await SetConfig(db, "SelectedTheme",       SelectedTheme,       ct);
            await SetConfig(db, "LayoutMode",          LayoutMode,          ct);
            await SetConfig(db, "BackgroundImagePath", BackgroundImagePath, ct);

            await db.SaveChangesAsync(ct);
            
            // Apply backend change immediately without restart
            if (_backendRouter != null && !string.IsNullOrEmpty(SelectedDownloadBackend))
            {
                await _backendRouter.SetActiveAsync(SelectedDownloadBackend, ct);

                // Re-configure qBit if it's now the active backend
                if (string.Equals(SelectedDownloadBackend, "QBittorrent", StringComparison.OrdinalIgnoreCase))
                {
                    var qbit = _backendRouter.All
                        .OfType<Sentrychan.Core.Services.Backends.QBittorrentBackend>()
                        .FirstOrDefault();
                    qbit?.Configure(QBitUrl, QBitUsername, QBitPassword);
                }
            }

            // Apply notification preferences immediately.
            Sentrychan.Core.Services.NotificationSettings.Apply(NotificationLevel, WindowsNotifications);

            // Push the new speed limits onto the live MonoTorrent engine so they
            // take effect immediately instead of only on the next app start.
            var mono = _backendRouter?.All
                .OfType<Sentrychan.Core.Services.Backends.MonoTorrentBackend>()
                .FirstOrDefault();
            if (mono != null) await mono.ApplyRateLimitsAsync(ct);

            // If the concurrency limit was raised (or removed), held jobs can start now.
            var queue = App.Services?.GetService(typeof(Sentrychan.Core.Services.DownloadQueueManager))
                as Sentrychan.Core.Services.DownloadQueueManager;
            if (queue != null) _ = queue.PromotePendingAsync(CancellationToken.None);

            // Restart folder watcher in case download path changed
            if (_folderWatcher != null)
            {
                await _folderWatcher.StopAsync();
                await _folderWatcher.StartAsync();
            }

            Succeed("Settings saved");
        }
        catch (Exception ex)
        {
            Fail($"Failed to save: {ex.Message}");
        }
    }

    private async Task BrowseFdmAsync(CancellationToken ct)
    {
        if (_owner == null) return;

        var files = await _owner.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Select FDM Executable",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Executable") { Patterns = ["*.exe"] }
                ]
            });

        if (files.Count > 0)
            FdmPath = files[0].Path.LocalPath;
    }

    private async Task BrowseDownloadAsync(CancellationToken ct)
    {
        if (_owner == null) return;

        var folders = await _owner.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Select Download Folder",
                AllowMultiple = false
            });

        if (folders.Count > 0)
            DownloadPath = folders[0].Path.LocalPath;
    }

    private async Task BrowseLibraryAsync(CancellationToken ct)
    {
        if (_owner == null) return;
        var folders = await _owner.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Select Anime Library Folder",
                AllowMultiple = false
            });
        if (folders.Count > 0)
            LibraryPath = folders[0].Path.LocalPath;
    }

    private async Task BrowseBackgroundImageAsync(CancellationToken ct)
    {
        if (_owner == null) return;
        var files = await _owner.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Select Background Image",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Images") { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp"] }
                ]
            });
        if (files.Count > 0)
            BackgroundImagePath = files[0].Path.LocalPath;
    }

    // ── Source packs (Mihon-style installable sources) ──────────────
    private static string SourcesDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sentrychan", "sources");

    private async Task ImportSourcePackAsync()
    {
        if (_owner == null) return;
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import source pack",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Source pack (*.dll)") { Patterns = ["*.dll"] }]
        });
        if (files.Count == 0) return;
        try
        {
            System.IO.Directory.CreateDirectory(SourcesDir);
            var n = 0;
            foreach (var f in files)
            {
                var src = f.Path.LocalPath;
                System.IO.File.Copy(src, System.IO.Path.Combine(SourcesDir, System.IO.Path.GetFileName(src)), overwrite: true);
                n++;
            }
            Succeed($"Imported {n} source pack(s). Restart Sentrychan to load them.");
        }
        catch (Exception ex) { Fail($"Import failed: {ex.Message}"); }
    }

    private void OpenSourcesFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(SourcesDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = SourcesDir, UseShellExecute = true });
        }
        catch (Exception ex) { Fail($"Couldn't open folder: {ex.Message}"); }
    }

    private async Task TestQBitConnectionAsync(CancellationToken ct)
    {
        QBitTestResult = "Testing...";
        try
        {
            using var http = new System.Net.Http.HttpClient
                { Timeout = TimeSpan.FromSeconds(5) };
            var baseUrl = QBitUrl.TrimEnd('/');

            // Try to reach the version endpoint (doesn't require auth)
            var response = await http.GetAsync($"{baseUrl}/api/v2/app/version", ct);
            if (!response.IsSuccessStatusCode)
            {
                QBitTestResult = $"Unreachable ({(int)response.StatusCode})";
                return;
            }

            // Try to authenticate
            var form = new System.Net.Http.FormUrlEncodedContent([
                new System.Collections.Generic.KeyValuePair<string, string>("username", QBitUsername),
                new System.Collections.Generic.KeyValuePair<string, string>("password", QBitPassword)
            ]);
            var loginResp = await http.PostAsync($"{baseUrl}/api/v2/auth/login", form, ct);
            var body      = await loginResp.Content.ReadAsStringAsync(ct);
            QBitTestResult = body.Trim() == "Ok." ? "Connected ✓" : "Auth failed — check credentials";
        }
        catch (Exception ex)
        {
            QBitTestResult = $"Error: {ex.Message}";
        }
    }

    private async Task ImportMalWatchingAsync(CancellationToken ct)
    {
        if (_apiService == null || _seriesService == null || string.IsNullOrEmpty(MalUsername)) return;
        
        MalImportResult = "Importing...";
        try
        {
            var results = await _apiService.GetUserWatchingAsync(MalUsername, ct);
            int imported = 0;
            foreach (var anime in results)
            {
                var series = new Series
                {
                    MalId = anime.MalId,
                    Title = anime.Title,
                    OriginalTitle = anime.TitleJapanese ?? anime.Title,
                    LastEpisodeNumber = 0,
                    AddedAt = DateTime.UtcNow,
                    AiringStatus = Sentrychan.Core.Services.AiringStatusNormalizer.Normalize(anime.Status),
                    TotalEpisodes = anime.Episodes
                };
                
                if (await _seriesService.AddAsync(series, anime.LargeImageUrl, ct) != null)
                    imported++;
            }
            MalImportResult = $"Imported {imported} series";
        }
        catch (Exception ex)
        {
            MalImportResult = $"Error: {ex.Message}";
        }
    }

    private async Task ExportLibraryAsync()
    {
        if (_owner == null || _dbFactory == null) return;
        try
        {
            var options = new FilePickerSaveOptions { Title = "Export Library", SuggestedFileName = "sentrychan_library.json" };
            var file = await _owner.StorageProvider.SaveFilePickerAsync(options);
            if (file == null) return;

            await using var db = await _dbFactory.CreateDbContextAsync();
            var series = await db.Series.AsNoTracking().ToListAsync();
            var feeds = await db.RssFeeds.AsNoTracking().ToListAsync();

            var export = new { Series = series, Feeds = feeds, ExportedAt = DateTime.UtcNow };
            var json = System.Text.Json.JsonSerializer.Serialize(export, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            
            await using var stream = await file.OpenWriteAsync();
            using var writer = new StreamWriter(stream);
            await writer.WriteAsync(json);
            
            MalImportResult = "Library exported successfully!";
        }
        catch (Exception ex)
        {
            MalImportResult = $"Export failed: {ex.Message}";
        }
    }

    private async Task ImportLibraryAsync()
    {
        if (_owner == null || _dbFactory == null || _seriesService == null) return;
        try
        {
            var options = new FilePickerOpenOptions { Title = "Import Library", AllowMultiple = false, FileTypeFilter = new[] { FilePickerFileTypes.Json } };
            var files = await _owner.StorageProvider.OpenFilePickerAsync(options);
            if (files.Count == 0) return;

            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();
            
            var import = System.Text.Json.JsonSerializer.Deserialize<LibraryExport>(json);
            if (import == null) throw new Exception("Invalid library file.");

            await using var db = await _dbFactory.CreateDbContextAsync();
            int importedSeries = 0;
            int importedFeeds = 0;

            foreach (var s in import.Series)
            {
                if (!await db.Series.AnyAsync(x => x.MalId == s.MalId))
                {
                    s.Id = 0; // Reset for auto-increment
                    db.Series.Add(s);
                    importedSeries++;
                }
            }

            foreach (var f in import.Feeds)
            {
                if (!await db.RssFeeds.AnyAsync(x => x.Url == f.Url))
                {
                    f.Id = 0;
                    db.RssFeeds.Add(f);
                    importedFeeds++;
                }
            }

            await db.SaveChangesAsync();
            MalImportResult = $"Imported {importedSeries} series and {importedFeeds} feeds.";
        }
        catch (Exception ex)
        {
            MalImportResult = $"Import failed: {ex.Message}";
        }
    }

    private async Task ResetLibraryAsync(CancellationToken ct)
    {
        if (_dbFactory == null) return;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // Removing order ensures constraints are met
            db.WatchHistory.RemoveRange(db.WatchHistory);
            db.TitleAliases.RemoveRange(db.TitleAliases);
            db.DownloadJobs.RemoveRange(db.DownloadJobs);
            db.AppConfigs.RemoveRange(db.AppConfigs);
            db.ApiCaches.RemoveRange(db.ApiCaches);

            db.Series.RemoveRange(db.Series);

            await db.SaveChangesAsync(ct);
            Succeed("Library reset successfully. Restart app to see changes.");
        }
        catch (Exception ex)
        {
            Fail($"Reset failed: {ex.Message}");
        }
    }

    private class LibraryExport
    {
        public System.Collections.Generic.List<Series> Series { get; set; } = [];
        public System.Collections.Generic.List<RssFeed> Feeds { get; set; } = [];
    }

    private static async Task<string> GetConfig(
        AppDbContext db, string key, string defaultValue, CancellationToken ct)
    {
        var entry = await db.AppConfigs
            .FirstOrDefaultAsync(c => c.Key == key, ct);
        return entry?.Value ?? defaultValue;
    }

    private static async Task SetConfig(
        AppDbContext db, string key, string value, CancellationToken ct)
    {
        var entry = await db.AppConfigs
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry != null)
            entry.Value = value;
        else
            db.AppConfigs.Add(new AppConfig { Key = key, Value = value });
    }
}