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
using Sentrychan.Core;

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
    
    // No built-in preference: a loaded source pack may seed one on a fresh install.
    private string _preferredReleaseGroups = string.Empty;
    public string PreferredReleaseGroups
    {
        get => _preferredReleaseGroups;
        set => this.RaiseAndSetIfChanged(ref _preferredReleaseGroups, value);
    }

    // How strictly automatic downloads keep to the preferred groups (ReleaseGroupPolicy).
    public const string GroupModeAny = "Any group";
    public const string GroupModePrefer = "Prefer my groups";
    public const string GroupModeOnly = "Only my groups";
    public string[] GroupModeOptions { get; } = [GroupModePrefer, GroupModeOnly, GroupModeAny];

    private string _groupMode = GroupModePrefer;
    public string GroupMode
    {
        get => _groupMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _groupMode, value);
            this.RaisePropertyChanged(nameof(GroupModeHint));
        }
    }

    public string GroupModeHint => GroupMode switch
    {
        GroupModeOnly => "New episodes download only from the groups above. Releases from other groups are ignored.",
        GroupModeAny => "Any group. When several release the same episode, the one highest in the list wins; other groups' releases ask first on shows set to auto-download.",
        _ => $"Waits up to {Sentrychan.Core.Services.ReleaseGroupPolicy.PreferWait.TotalHours:0} hours for one of the groups above, then takes another group's release.",
    } + " Only automatic downloads follow this — what you pick yourself in Search or Latest always downloads. Each show can have its own rule (its page → Release groups).";

    public static string GroupModeLabel(Sentrychan.Core.Services.GroupMode mode) => mode switch
    {
        Sentrychan.Core.Services.GroupMode.Any => GroupModeAny,
        Sentrychan.Core.Services.GroupMode.Only => GroupModeOnly,
        _ => GroupModePrefer,
    };

    public static Sentrychan.Core.Services.GroupMode GroupModeFromLabel(string? label) => label switch
    {
        GroupModeAny => Sentrychan.Core.Services.GroupMode.Any,
        GroupModeOnly => Sentrychan.Core.Services.GroupMode.Only,
        _ => Sentrychan.Core.Services.GroupMode.Prefer,
    };
    
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

    private string _showInfoStatus = string.Empty;
    public string ShowInfoStatus
    {
        get => _showInfoStatus;
        set => this.RaiseAndSetIfChanged(ref _showInfoStatus, value);
    }

    private ReactiveCommand<Unit, Unit>? _refreshShowInfoCommand;
    /// <summary>Re-checks every library show's airing status and episode count now, instead of at the next twice-daily pass.</summary>
    public ReactiveCommand<Unit, Unit> RefreshShowInfoCommand => _refreshShowInfoCommand ??= ReactiveCommand.CreateFromTask(async () =>
    {
        if (App.Services?.GetService(typeof(Sentrychan.Core.Services.AiringStatusRefreshService))
                is not Sentrychan.Core.Services.AiringStatusRefreshService refresher)
            return;
        ShowInfoStatus = "Checking…";
        try
        {
            var report = await refresher.RefreshAsync(CancellationToken.None);
            var changes = report.Refreshed + report.Corrected;
            ShowInfoStatus = changes == 0
                ? $"All {report.Checked} shows are up to date."
                : $"{report.Checked} shows checked: {report.Refreshed} airing statuses and {report.Corrected} episode counts updated.";
            if (changes > 0 && Avalonia.Application.Current?.ApplicationLifetime
                    is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow.DataContext: MainWindowViewModel main })
                await main.LoadSeriesAsync();
        }
        catch (Exception ex) { ShowInfoStatus = "Couldn't check: " + ex.Message; }
    });

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
        // A success note needn't linger in the save bar; errors stay until fixed.
        _ = Task.Delay(4000).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (StatusMessage == message && !IsStatusError) StatusMessage = string.Empty;
        }));
    }

    // ── About ──────────────────────────────────────────────────────
    // The package's version ("1.0.5"), without the commit the SDK appends ("+1a2b3c…"). The
    // assembly version is pinned at 1.0.0, so it said "v1.0.0" on every release.
    public string AppVersion =>
        "v" + ((System.Reflection.Assembly.GetEntryAssembly()?
                   .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                   .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion?.Split('+')[0]
               ?? System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0");

    public string LogFolderPath => AppPaths.Logs;

    public ReactiveCommand<Unit, Unit> OpenLogsCommand { get; } =
        ReactiveCommand.Create(() =>
        {
            Services.ShellLauncher.OpenFolder(AppPaths.Logs);
        });

    public ReactiveCommand<string, Unit> OpenLinkCommand { get; } =
        ReactiveCommand.Create<string>(Services.AppLinks.Open);

    public ReactiveCommand<Unit, Unit> ShowTutorialCommand { get; } =
        ReactiveCommand.CreateFromTask(async () =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                    is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d
                && d.MainWindow?.DataContext is MainWindowViewModel mvm)
                await mvm.ShowTutorialAsync();
        });

    public ReactiveCommand<Unit, Unit> ProblemReportCommand { get; } =
        ReactiveCommand.CreateFromTask(async () =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                    is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d
                && d.MainWindow?.DataContext is MainWindowViewModel mvm)
                await mvm.ShowProblemReportAsync();
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
        if (App.Services?.GetService(typeof(Sentrychan.Core.MihonBridge.MihonBridgeService)) is Sentrychan.Core.MihonBridge.MihonBridgeService bridge
            && App.Services.GetService(typeof(IConfigService)) is IConfigService config)
        {
            MihonExtensions = new MihonExtensionsViewModel(bridge, config,
                App.Services.GetService(typeof(ISecretModeService)) as ISecretModeService)
            {
                ShowPreferences = async prefs =>
                {
                    var dialog = new Views.Dialogs.SourcePreferencesDialog { DataContext = prefs };
                    await dialog.ShowDialog(owner);
                },
            };
        }
        InitPageBehaviour();
    }

    /// <summary>Settings → Sources → Mihon extensions. Acts immediately; not part of Save.</summary>
    public MihonExtensionsViewModel? MihonExtensions { get; }

    // ── Page behaviour ─────────────────────────────────────────────
    // Settings is a page, not a modal dialog, so nothing forces a Save/Cancel decision:
    // edits wait in the save bar until saved or discarded, and survive navigating away.

    // Status and progress text change constantly and aren't settings.
    private static readonly System.Collections.Generic.HashSet<string> NotSettings =
    [
        nameof(StatusMessage), nameof(IsStatusError), nameof(IsDirty), nameof(QBitTestResult),
        nameof(MalImportResult), nameof(UpdateStatus), nameof(IsCheckingUpdate), nameof(UpdateAvailable),
        nameof(ConfirmingReset), nameof(ShowSaveBar), nameof(NamingExample), nameof(IsCustomNaming),
        nameof(SelectedTab), nameof(SourcesFileMessage), nameof(ShowAdvanced), nameof(OpenAtLogin),
        nameof(ShowInfoStatus),
    ];

    public const string ShowAdvancedKey = "ShowAdvancedSettings";

    private bool _showAdvanced;
    /// <summary>
    /// Technical options (download engines, intervals, paths to other programs, the danger zone)
    /// stay out of sight until asked for. Remembered at once — it's how the page looks, not a setting.
    /// </summary>
    public bool ShowAdvanced
    {
        get => _showAdvanced;
        set
        {
            if (_showAdvanced == value) return;
            this.RaiseAndSetIfChanged(ref _showAdvanced, value);
            if (!_loading && _dbFactory != null) _ = SaveFlagAsync(ShowAdvancedKey, value);
        }
    }

    /// <summary>
    /// "Start with Windows" / "Open at login" acts at once — it's the system's own list (the Run
    /// key, a LaunchAgent), not a stored setting.
    /// </summary>
    public bool CanOpenAtLogin => Services.LoginItem.IsSupported;
    public string OpenAtLoginLabel => Services.LoginItem.Label;
    public bool OpenAtLogin
    {
        get => Services.LoginItem.IsEnabled;
        set
        {
            try { Services.LoginItem.Set(value); }
            catch (Exception ex) { Fail("Couldn't change Open at login: " + ex.Message); }
            this.RaisePropertyChanged();
        }
    }

    public bool IsTestBuild => Sentrychan.Core.BuildInfo.IsTestBuild;

    // Words and examples that fit the system the app runs on — a Mac user was shown "C:\Downloads"
    // and "Windows notifications".
    public string NotificationsLabel => OperatingSystem.IsWindows() ? "Windows notifications" : "System notifications";
    public string NotificationsHint => OperatingSystem.IsWindows()
        ? "On: important events pop up as Windows notifications. Off: they stay as quiet messages inside the app."
        : "On: important events show up in the system's notifications. Off: they stay as quiet messages inside the app.";
    public string DownloadFolderExample => OperatingSystem.IsWindows() ? @"C:\Users\you\Downloads" : "~/Downloads";
    public string LibraryFolderExample => OperatingSystem.IsWindows() ? @"D:\Anime" : "~/Movies/Anime";
    public string MangaFolderExample => OperatingSystem.IsWindows() ? @"D:\Manga" : "~/Documents/Manga";

    private async Task SaveFlagAsync(string key, bool value)
    {
        try
        {
            await using var db = await _dbFactory!.CreateDbContextAsync();
            await SetConfig(db, key, value ? "true" : "false", default);
            await db.SaveChangesAsync();
        }
        catch { /* only a view preference */ }
    }

    // Tab positions in SettingsView, for opening the page at one.
    public const int DownloadsTab = 2;
    public const int SourcesTab = 5;
    public const int AboutTab = 8;

    private int _selectedTab;
    public int SelectedTab { get => _selectedTab; set => this.RaiseAndSetIfChanged(ref _selectedTab, value); }

    /// <summary>Opens the "Add your sources" guide; wired by the main window.</summary>
    public Func<Task>? OpenSourcesGuide { get; set; }
    public ReactiveCommand<Unit, Unit> OpenSourcesGuideCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ImportSourcesFileCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ImportSourcesFolderCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ExportSourcesCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CheckSourcesCommand { get; private set; } = null!;

    /// <summary>Imports picked paths and shows the result with the sources check; wired by the main window.</summary>
    public Func<System.Collections.Generic.IReadOnlyList<string>, Task>? ImportSources { get; set; }
    /// <summary>Opens the sources check on its own; wired by the main window.</summary>
    public Func<Task>? CheckSources { get; set; }

    private string? _sourcesFileMessage;
    /// <summary>What the last sources-file export did. Acts at once, not on Save.</summary>
    public string? SourcesFileMessage { get => _sourcesFileMessage; private set => this.RaiseAndSetIfChanged(ref _sourcesFileMessage, value); }

    private async Task ImportSourcesAsync(bool folder)
    {
        if (_owner == null || ImportSources == null) return;
        try
        {
            if (await Services.SourcesFileActions.PickAsync(_owner, folder) is not { } paths) return;
            SourcesFileMessage = null;
            await ImportSources(paths);
            RssFeedsVm?.LoadFeedsCommand.Execute().Subscribe();
            if (MihonExtensions != null) await MihonExtensions.LoadAsync();
        }
        catch (Exception ex) { SourcesFileMessage = "That couldn't be imported: " + ex.Message; }
    }

    private async Task ExportSourcesAsync()
    {
        if (_owner == null) return;
        try
        {
            if (await Services.SourcesFileActions.ExportAsync(_owner) is { } message) SourcesFileMessage = message;
        }
        catch (Exception ex) { SourcesFileMessage = "Export failed: " + ex.Message; }
    }

    private bool _loading;

    private bool _isDirty;
    public bool IsDirty
    {
        get => _isDirty;
        set
        {
            this.RaiseAndSetIfChanged(ref _isDirty, value);
            this.RaisePropertyChanged(nameof(ShowSaveBar));
        }
    }

    public bool ShowSaveBar => IsDirty || !string.IsNullOrEmpty(StatusMessage);

    /// <summary>Raised after a successful save, so the main window can pick up what changed.</summary>
    public event Action? Saved;

    public ReactiveCommand<Unit, Unit> DiscardCommand { get; private set; } = null!;

    /// <summary>Opens the (separate) account window; wired by the main window.</summary>
    public Func<Task>? OpenAccount { get; set; }
    public ReactiveCommand<Unit, Unit> OpenAccountCommand { get; private set; } = null!;

    private bool _confirmingReset;
    /// <summary>Reset wipes the library; the first click only arms it.</summary>
    public bool ConfirmingReset { get => _confirmingReset; set => this.RaiseAndSetIfChanged(ref _confirmingReset, value); }
    public ReactiveCommand<Unit, Unit> CancelResetCommand { get; private set; } = null!;

    private void InitPageBehaviour()
    {
        Changed.Subscribe(e =>
        {
            if (_loading || e.PropertyName == null || NotSettings.Contains(e.PropertyName)) return;
            IsDirty = true;
        });
        this.WhenAnyValue(x => x.StatusMessage).Subscribe(_ => this.RaisePropertyChanged(nameof(ShowSaveBar)));

        DiscardCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            await LoadAsync();
            // Theme and layout preview live while picked; put the saved ones back.
            _themeService?.ApplyNamedTheme(SelectedTheme);
            LayoutMode = _layoutMode;
            IsDirty = false;
            Succeed("Changes discarded");
        });
        OpenAccountCommand = ReactiveCommand.CreateFromTask(async () => { if (OpenAccount != null) await OpenAccount(); });
        OpenSourcesGuideCommand = ReactiveCommand.CreateFromTask(async () => { if (OpenSourcesGuide != null) await OpenSourcesGuide(); });
        ImportSourcesFileCommand = ReactiveCommand.CreateFromTask(() => ImportSourcesAsync(folder: false));
        ImportSourcesFolderCommand = ReactiveCommand.CreateFromTask(() => ImportSourcesAsync(folder: true));
        ExportSourcesCommand = ReactiveCommand.CreateFromTask(ExportSourcesAsync);
        CheckSourcesCommand = ReactiveCommand.CreateFromTask(async () => { if (CheckSources != null) await CheckSources(); });
        CancelResetCommand = ReactiveCommand.Create(() => { ConfirmingReset = false; });
    }

    // Call after construction to populate fields from DB
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_dbFactory == null) return;
        _ = MihonExtensions?.LoadAsync();
        _loading = true;
        try { await LoadCoreAsync(ct); }
        finally
        {
            _loading = false;
            IsDirty = false;
        }
    }

    private async Task LoadCoreAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory!.CreateDbContextAsync(ct);

        ShowAdvanced = await GetConfig(db, ShowAdvancedKey, "false", ct) == "true";
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

        var naming = Sentrychan.Core.Library.NamingTemplate.FromConfig(
            await GetConfig(db, Sentrychan.Core.Library.NamingTemplate.PresetKey, "", ct),
            await GetConfig(db, Sentrychan.Core.Library.NamingTemplate.TemplateKey, "", ct));
        NamingPreset = LabelFromPreset(naming.Preset);
        var storedTemplate = await GetConfig(db, Sentrychan.Core.Library.NamingTemplate.TemplateKey, "", ct);
        CustomNamingTemplate = string.IsNullOrWhiteSpace(storedTemplate)
            ? Sentrychan.Core.Library.NamingTemplate.JellyfinTemplate : storedTemplate;

        NotificationLevel = await GetConfig(db, Sentrychan.Core.Services.NotificationSettings.LevelKey, "Important", ct);
        WindowsNotifications = await GetConfig(db, Sentrychan.Core.Services.NotificationSettings.WindowsKey, "true", ct) == "true";
        WatchFolderMode = await GetConfig(db, "DownloadOrganizeMode", "Own", ct) == "Watch";
        PreferredReleaseGroups = await GetConfig(db, "PreferredReleaseGroups", "", ct);
        GroupMode = GroupModeLabel(Sentrychan.Core.Services.ReleaseGroupPolicy.ParseMode(
            await GetConfig(db, Sentrychan.Core.Services.ReleaseGroupPolicy.ModeKey, "", ct)));
        VlcPath = await GetConfig(db, "VlcPath", "vlc", ct);
        SelectedPlayer = await GetConfig(db, "SelectedPlayer", "Internal", ct);
        MalUsername = await GetConfig(db, "MalUsername", "", ct);

        QBitUrl      = await GetConfig(db, "QBitUrl",      "http://localhost:8080", ct);
        QBitUsername = await GetConfig(db, "QBitUsername",  "admin",        ct);
        // Decrypt for editing; legacy plaintext values pass through untouched and get
        // re-written encrypted on the next save.
        try
        {
            QBitPassword = Sentrychan.Core.Services.SecretProtector.Unprotect(
                await GetConfig(db, "QBitPassword", "adminadmin", ct));
        }
        catch (Sentrychan.Core.Secrets.SecretStoreUnavailableException ex)
        {
            QBitPassword = string.Empty;
            StatusMessage = ex.Message;
        }
        SelectedDownloadBackend = await GetConfig(db, "SelectedDownloadBackend", "SystemDefault", ct);

        // Appearance
        _selectedTheme = await GetConfig(db, "SelectedTheme", "Yoru", ct);
        _layoutMode = await GetConfig(db, "LayoutMode", "Sidebar", ct);
        this.RaisePropertyChanged(nameof(LayoutMode));
        this.RaisePropertyChanged(nameof(SelectedTheme));
        BackgroundImagePath = await GetConfig(db, "BackgroundImagePath", "", ct);
        MangaUiStyle = await GetConfig(db, MainWindowViewModel.MangaUiStyleKey, "Classic", ct) == "Mihon" ? "Mihon" : "Classic";
    }

    // Manga screens: the classic pages, or the Mihon-style ones (opt-in while they settle in).
    private string _mangaUiStyle = "Classic";
    public string MangaUiStyle
    {
        get => _mangaUiStyle;
        set => this.RaiseAndSetIfChanged(ref _mangaUiStyle, value);
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

    // ── Library naming ─────────────────────────────────────────────
    // How finished downloads are named, and what Tidy library renames existing files to.
    public string[] NamingPresets { get; } = ["Jellyfin/Plex", "Minimal", "Custom"];

    private static Sentrychan.Core.Library.NamingPreset PresetFromLabel(string? label) => label switch
    {
        "Minimal" => Sentrychan.Core.Library.NamingPreset.Minimal,
        "Custom"  => Sentrychan.Core.Library.NamingPreset.Custom,
        _         => Sentrychan.Core.Library.NamingPreset.JellyfinPlex,
    };

    private static string LabelFromPreset(Sentrychan.Core.Library.NamingPreset preset) => preset switch
    {
        Sentrychan.Core.Library.NamingPreset.Minimal => "Minimal",
        Sentrychan.Core.Library.NamingPreset.Custom  => "Custom",
        _                                            => "Jellyfin/Plex",
    };

    private string _namingPreset = "Jellyfin/Plex";
    public string NamingPreset
    {
        get => _namingPreset;
        set
        {
            this.RaiseAndSetIfChanged(ref _namingPreset, value);
            this.RaisePropertyChanged(nameof(IsCustomNaming));
            RaiseNamingExample();
        }
    }

    private string _customNamingTemplate = Sentrychan.Core.Library.NamingTemplate.JellyfinTemplate;
    public string CustomNamingTemplate
    {
        get => _customNamingTemplate;
        set
        {
            this.RaiseAndSetIfChanged(ref _customNamingTemplate, value);
            RaiseNamingExample();
        }
    }

    public bool IsCustomNaming => NamingPreset == "Custom";

    /// <summary>What the chosen naming produces for a sample episode — updates as the template is typed.</summary>
    public string NamingExample
    {
        get
        {
            var preset = PresetFromLabel(NamingPreset);
            if (preset == Sentrychan.Core.Library.NamingPreset.Custom
                && Sentrychan.Core.Library.NamingTemplate.Validate(CustomNamingTemplate) is { } error)
                return error;
            var t = Sentrychan.Core.Library.NamingTemplate.For(preset, CustomNamingTemplate);
            var episode = t.Render(new Sentrychan.Core.Library.EpisodeNaming("Sousou no Frieren", 2023, 1, 5, ".mkv", "Group", "1080p"));
            var special = t.Render(new Sentrychan.Core.Library.EpisodeNaming("Sousou no Frieren", 2023, 0, 1, ".mkv", "Group", "1080p"));
            return $"{episode.Replace('\\', '/')}\n{special.Replace('\\', '/')}   (a special)";
        }
    }

    private void RaiseNamingExample() => this.RaisePropertyChanged(nameof(NamingExample));

    public ReactiveCommand<Unit, Unit> OpenTidyLibraryCommand { get; } =
        ReactiveCommand.CreateFromTask(async () =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                    is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner }
                || App.Services?.GetService(typeof(Sentrychan.Core.Library.LibraryTidyService))
                    is not Sentrychan.Core.Library.LibraryTidyService tidy)
                return;
            var vm = new TidyLibraryViewModel(tidy);
            var dialog = new Views.Dialogs.TidyLibraryDialog { DataContext = vm };
            _ = vm.RefreshAsync();
            await dialog.ShowDialog(owner);
        });

    public ReactiveCommand<Unit, Unit> ImportMihonBackupCommand { get; } =
        ReactiveCommand.CreateFromTask(async () =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                    is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner }
                || App.Services?.GetService(typeof(Sentrychan.Core.MihonBackup.MihonBackupImporter))
                    is not Sentrychan.Core.MihonBackup.MihonBackupImporter importer)
                return;
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Mihon backup",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Mihon backup") { Patterns = ["*.tachibk", "*.proto.gz"] }, FilePickerFileTypes.All],
            });
            if (files.Count == 0) return;
            byte[] bytes;
            await using (var stream = await files[0].OpenReadAsync())
            using (var ms = new MemoryStream())
            {
                await stream.CopyToAsync(ms);
                bytes = ms.ToArray();
            }
            var vm = new MihonImportViewModel(importer, bytes, files[0].Name);
            var dialog = new Views.Dialogs.MihonImportDialog { DataContext = vm };
            _ = vm.PlanAsync();
            await dialog.ShowDialog(owner);
        });

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

        if (IsCustomNaming && Sentrychan.Core.Library.NamingTemplate.Validate(CustomNamingTemplate) is { } namingError)
        {
            Fail($"{namingError} — see the Library tab");
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
            await SetConfig(db, Sentrychan.Core.Services.ReleaseGroupPolicy.ModeKey, GroupModeFromLabel(GroupMode).ToString(), ct);
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
            await SetConfig(db, Sentrychan.Core.Library.NamingTemplate.PresetKey, PresetFromLabel(NamingPreset).ToString(), ct);
            await SetConfig(db, Sentrychan.Core.Library.NamingTemplate.TemplateKey, CustomNamingTemplate?.Trim() ?? string.Empty, ct);

            // Appearance
            await SetConfig(db, "SelectedTheme",       SelectedTheme,       ct);
            await SetConfig(db, "LayoutMode",          LayoutMode,          ct);
            await SetConfig(db, "BackgroundImagePath", BackgroundImagePath, ct);
            await SetConfig(db, MainWindowViewModel.MangaUiStyleKey, MangaUiStyle, ct);

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

            IsDirty = false;
            Succeed("Settings saved");
            Saved?.Invoke();
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
    private static string SourcesDir => AppPaths.Sources;

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
                    TotalEpisodes = anime.Episodes,
                    Year = anime.Year ?? Sentrychan.Core.Library.LibraryMetadata.YearOf(anime.Aired?.From),
                    MediaType = Sentrychan.Core.Library.LibraryMetadata.NormalizeType(anime.Type),
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
        // Irreversible: the first click only arms the button.
        if (!ConfirmingReset) { ConfirmingReset = true; return; }
        ConfirmingReset = false;
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