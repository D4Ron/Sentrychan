using ReactiveUI;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using Avalonia;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Models.Api;

namespace Sentrychan.UI.ViewModels;

public enum SeriesSourceMode { Library, Custom }

public class CreateWatchPartyViewModel : ViewModelBase
{
    private readonly IWatchPartyHostService? _hostService;
    private readonly ITunnelService? _tunnelService;
    private readonly IDbContextFactory<Sentrychan.Core.Data.AppDbContext>? _dbFactory;
    private readonly IVideoFileLocator? _fileLocator;

    // Output
    private bool _isSessionCreated;
    public bool IsSessionCreated
    {
        get => _isSessionCreated;
        set => this.RaiseAndSetIfChanged(ref _isSessionCreated, value);
    }

    public string? RoomCode => _hostService?.CurrentSessionId;

    private string _inviteString = string.Empty;
    public string InviteString
    {
        get => _inviteString;
        set => this.RaiseAndSetIfChanged(ref _inviteString, value);
    }

    private Bitmap? _qrCodeBitmap;
    public Bitmap? QrCodeBitmap
    {
        get => _qrCodeBitmap;
        set => this.RaiseAndSetIfChanged(ref _qrCodeBitmap, value);
    }

    private bool _isTunnelActive;
    public bool IsTunnelActive
    {
        get => _isTunnelActive;
        set => this.RaiseAndSetIfChanged(ref _isTunnelActive, value);
    }

    private string? _publicAddress;
    public string? PublicAddress
    {
        get => _publicAddress;
        set => this.RaiseAndSetIfChanged(ref _publicAddress, value);
    }

    private bool _canActivateTunnel;
    public bool CanActivateTunnel
    {
        get => _canActivateTunnel;
        set => this.RaiseAndSetIfChanged(ref _canActivateTunnel, value);
    }

    private string _tunnelTooltip = "Activate NAT Tunnel (FRP)";
    public string TunnelTooltip
    {
        get => _tunnelTooltip;
        set => this.RaiseAndSetIfChanged(ref _tunnelTooltip, value);
    }

    private string? _tailscaleIp;
    public string? TailscaleIp
    {
        get => _tailscaleIp;
        set => this.RaiseAndSetIfChanged(ref _tailscaleIp, value);
    }

    private string _tailscaleInviteString = string.Empty;
    public string TailscaleInviteString
    {
        get => _tailscaleInviteString;
        set => this.RaiseAndSetIfChanged(ref _tailscaleInviteString, value);
    }

    private bool _isTunnelConfigured;
    public bool IsTunnelConfigured
    {
        get => _isTunnelConfigured;
        set => this.RaiseAndSetIfChanged(ref _isTunnelConfigured, value);
    }

    private string _tunnelStatusMessage = "Not configured";
    public string TunnelStatusMessage
    {
        get => _tunnelStatusMessage;
        set => this.RaiseAndSetIfChanged(ref _tunnelStatusMessage, value);
    }

    private string _tunnelButtonLabel = "Activate Tunnel";
    public string TunnelButtonLabel
    {
        get => _tunnelButtonLabel;
        set => this.RaiseAndSetIfChanged(ref _tunnelButtonLabel, value);
    }

    private string _inviteStringLabel = "LAN invite";
    public string InviteStringLabel
    {
        get => _inviteStringLabel;
        set => this.RaiseAndSetIfChanged(ref _inviteStringLabel, value);
    }

    public ObservableCollection<Series> AvailableSeries { get; } = new();

    private SeriesSourceMode _seriesSourceMode = SeriesSourceMode.Library;
    public SeriesSourceMode SeriesSourceMode
    {
        get => _seriesSourceMode;
        set => this.RaiseAndSetIfChanged(ref _seriesSourceMode, value);
    }

    public bool IsLibraryMode => SeriesSourceMode == SeriesSourceMode.Library;
    public bool IsCustomMode => SeriesSourceMode == SeriesSourceMode.Custom;

    // Custom Mode Fields
    private string _customSeriesTitle = string.Empty;
    public string CustomSeriesTitle
    {
        get => _customSeriesTitle;
        set => this.RaiseAndSetIfChanged(ref _customSeriesTitle, value);
    }

    private string _customSeriesSearch = string.Empty;
    public string CustomSeriesSearch
    {
        get => _customSeriesSearch;
        set => this.RaiseAndSetIfChanged(ref _customSeriesSearch, value);
    }

    public ObservableCollection<AnimeResult> SearchResults { get; } = new();

    private AnimeResult? _selectedSearchResult;
    public AnimeResult? SelectedSearchResult
    {
        get => _selectedSearchResult;
        set => this.RaiseAndSetIfChanged(ref _selectedSearchResult, value);
    }

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        set => this.RaiseAndSetIfChanged(ref _isSearching, value);
    }

    private int? _customMalId;
    public int? CustomMalId
    {
        get => _customMalId;
        set => this.RaiseAndSetIfChanged(ref _customMalId, value);
    }

    public string EffectiveSeriesTitle => IsLibraryMode
        ? (SelectedSeries?.Title ?? string.Empty)
        : CustomSeriesTitle;

    public int? EffectiveSeriesId => IsLibraryMode
        ? SelectedSeries?.Id
        : null;

    private Series? _selectedSeries;
    public Series? SelectedSeries
    {
        get => _selectedSeries;
        set => this.RaiseAndSetIfChanged(ref _selectedSeries, value);
    }

    private int _selectedEpisodeNumber = 1;
    public int SelectedEpisodeNumber
    {
        get => _selectedEpisodeNumber;
        set => this.RaiseAndSetIfChanged(ref _selectedEpisodeNumber, value);
    }

    private string _videoSource = string.Empty;
    public string VideoSource
    {
        get => _videoSource;
        set => this.RaiseAndSetIfChanged(ref _videoSource, value);
    }

    private bool _isLocatingFile;
    public bool IsLocatingFile
    {
        get => _isLocatingFile;
        set => this.RaiseAndSetIfChanged(ref _isLocatingFile, value);
    }

    private string _fileStatusMessage = "Select a series to continue";
    public string FileStatusMessage
    {
        get => _fileStatusMessage;
        set => this.RaiseAndSetIfChanged(ref _fileStatusMessage, value);
    }

    private bool _fileFoundAutomatically;
    public bool FileFoundAutomatically
    {
        get => _fileFoundAutomatically;
        set => this.RaiseAndSetIfChanged(ref _fileFoundAutomatically, value);
    }

    private string _displayName = string.Empty;
    public string DisplayName
    {
        get => _displayName;
        set => this.RaiseAndSetIfChanged(ref _displayName, value);
    }

    private string _password = string.Empty;
    public string Password
    {
        get => _password;
        set => this.RaiseAndSetIfChanged(ref _password, value);
    }

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    private bool _hasError;
    public bool HasError
    {
        get => _hasError;
        set => this.RaiseAndSetIfChanged(ref _hasError, value);
    }

    public Func<string, Task>? CopyToClipboard { get; set; }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    public string CancelButtonText => IsSessionCreated ? "Close" : "Cancel";

    public ReactiveCommand<Unit, Unit> CreateCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleTunnelCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenTailscaleCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyTailscaleInviteCommand { get; }
    public ReactiveCommand<Unit, Unit> BrowseCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyLinkCommand { get; }
    public ReactiveCommand<Unit, Unit> ShareDiscordCommand { get; }
    public ReactiveCommand<Unit, Unit> ProceedToLobbyCommand { get; }
    public ReactiveCommand<Unit, Unit> SearchJikanCommand { get; }
    public ReactiveCommand<AnimeResult, Unit> SelectSearchResultCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearSearchResultCommand { get; }
    public ReactiveCommand<Unit, Unit> SwitchToLibraryModeCommand { get; }
    public ReactiveCommand<Unit, Unit> SwitchToCustomModeCommand { get; }

    private readonly IAnimeApiService? _animeApiService;
    private readonly ILogger<CreateWatchPartyViewModel>? _logger;

    public CreateWatchPartyViewModel()
    {
        var canCreate = this.WhenAnyValue(
            x => x.SeriesSourceMode,
            x => x.SelectedSeries,
            x => x.CustomSeriesTitle,
            x => x.VideoSource,
            x => x.DisplayName,
            x => x.IsBusy,
            (mode, series, customTitle, video, name, busy) => 
            {
                bool seriesOk = mode == SeriesSourceMode.Library
                    ? series != null
                    : !string.IsNullOrWhiteSpace(customTitle);
                return seriesOk && 
                       !string.IsNullOrWhiteSpace(video) && 
                       !string.IsNullOrWhiteSpace(name) && 
                       !busy;
            });

        CreateCommand = ReactiveCommand.CreateFromTask(async ct => await CreatePartyAsync(ct), canCreate);
        CancelCommand = ReactiveCommand.Create(() => { });
        ToggleTunnelCommand = ReactiveCommand.CreateFromTask(async ct => await ToggleTunnelAsync(ct));
        
        OpenTailscaleCommand = ReactiveCommand.Create(() => 
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://tailscale.com/download") { UseShellExecute = true }); }
            catch { /* Log error */ }
        });
        
        CopyTailscaleInviteCommand = ReactiveCommand.CreateFromTask(async ct => 
        {
            if (CopyToClipboard != null && !string.IsNullOrEmpty(TailscaleInviteString))
                await CopyToClipboard(TailscaleInviteString);
        });

        BrowseCommand = ReactiveCommand.CreateFromTask(async ct => await BrowseFileAsync());
        CopyLinkCommand = ReactiveCommand.CreateFromTask(async ct => await CopyLinkAsync());
        ShareDiscordCommand = ReactiveCommand.CreateFromTask(async ct => await ShareDiscordAsync());
        ProceedToLobbyCommand = ReactiveCommand.Create(() => { });

        SearchJikanCommand = ReactiveCommand.CreateFromTask(async ct => await SearchJikanAsync(ct));
        SelectSearchResultCommand = ReactiveCommand.Create<AnimeResult>(r => SelectSearchResult(r));
        ClearSearchResultCommand = ReactiveCommand.Create(() => ClearSearchResult());
        
        SwitchToLibraryModeCommand = ReactiveCommand.Create(() =>
        {
            SeriesSourceMode = SeriesSourceMode.Library;
            CustomSeriesTitle = string.Empty;
            CustomSeriesSearch = string.Empty;
            SearchResults.Clear();
            SelectedSearchResult = null;
            CustomMalId = null;
        });

        SwitchToCustomModeCommand = ReactiveCommand.Create(() =>
        {
            SeriesSourceMode = SeriesSourceMode.Custom;
            SelectedSeries = null;
        });

        this.WhenAnyValue(x => x.SeriesSourceMode)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(IsLibraryMode));
                this.RaisePropertyChanged(nameof(IsCustomMode));
            });

        SetupFileDetection();
    }

    public CreateWatchPartyViewModel(
        IWatchPartyHostService hostService, 
        ITunnelService tunnelService,
        IDbContextFactory<Sentrychan.Core.Data.AppDbContext> dbFactory,
        IVideoFileLocator fileLocator,
        IAnimeApiService animeApiService,
        ILogger<CreateWatchPartyViewModel> logger) : this()
    {
        _hostService = hostService;
        _tunnelService = tunnelService;
        _dbFactory = dbFactory;
        _fileLocator = fileLocator;
        _animeApiService = animeApiService;
        _logger = logger;
        
        CheckTunnelPrerequisites();
        _ = LoadSeriesAsync(CancellationToken.None);
    }

    private async Task LoadSeriesAsync(CancellationToken ct)
    {
        if (_dbFactory == null) return;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var series = await db.Series
                .OrderBy(s => s.Title)
                .ToListAsync(ct);
            
            AvailableSeries.Clear();
            foreach (var s in series) AvailableSeries.Add(s);
            
            SelectedSeries = AvailableSeries.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load series: {ex.Message}";
            HasError = true;
        }
    }

    private void SetupFileDetection()
    {
        this.WhenAnyValue(
            x => x.SeriesSourceMode, 
            x => x.SelectedSeries, 
            x => x.CustomSeriesTitle,
            x => x.SelectedEpisodeNumber)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ => await AutoDetectFileAsync(CancellationToken.None));
    }

    private async Task AutoDetectFileAsync(CancellationToken ct)
    {
        string title = EffectiveSeriesTitle;
        if (string.IsNullOrWhiteSpace(title) || _fileLocator == null) return;

        IsLocatingFile = true;
        try
        {
            // Try primary title
            var path = await _fileLocator.FindVideoFileAsync(title, SelectedEpisodeNumber, ct);

            // In custom mode with a MAL result, try alternate titles as fallbacks
            if (path == null && IsCustomMode && SelectedSearchResult != null)
            {
                var titlesToTry = new List<string>();
                if (!string.IsNullOrEmpty(SelectedSearchResult.Title)) titlesToTry.Add(SelectedSearchResult.Title);
                if (!string.IsNullOrEmpty(SelectedSearchResult.TitleEnglish)) titlesToTry.Add(SelectedSearchResult.TitleEnglish);
                if (!string.IsNullOrEmpty(SelectedSearchResult.TitleJapanese)) titlesToTry.Add(SelectedSearchResult.TitleJapanese);
                foreach (var t in SelectedSearchResult.Titles)
                {
                    if (!string.IsNullOrEmpty(t.Title)) titlesToTry.Add(t.Title);
                }

                foreach (var altTitle in titlesToTry.Distinct())
                {
                    if (path != null) break; // Found it, no need to search further
                    path = await _fileLocator.FindVideoFileAsync(altTitle, SelectedEpisodeNumber, ct);
                }
            }

            if (path != null)
            {
                VideoSource = path;
                FileStatusMessage = $"Found: {Path.GetFileName(path)}";
                FileFoundAutomatically = true;
            }
            else
            {
                VideoSource = string.Empty;
                FileStatusMessage = "File not found — browse to locate";
                FileFoundAutomatically = false;
            }
        }
        finally
        {
            IsLocatingFile = false;
        }
    }

    private async Task CreatePartyAsync(CancellationToken ct)
    {
        if (_hostService == null || string.IsNullOrWhiteSpace(EffectiveSeriesTitle)) return;

        IsBusy = true;
        HasError = false;
        StatusMessage = "Starting host server...";

        var success = await _hostService.StartPartyAsync(
            DisplayName, 
            Password, 
            EffectiveSeriesId, 
            EffectiveSeriesTitle,
            SelectedEpisodeNumber, 
            ct);

        if (success)
        {
            StatusMessage = "Watch Party created!";
            IsSessionCreated = true;
            
            TailscaleIp = Sentrychan.Core.Services.TailscaleDetector.GetTailscaleIp();
            if (TailscaleIp != null)
            {
                _logger?.LogInformation("Tailscale detected: {Ip}", TailscaleIp);
            }
            
            UpdateInviteString();
            this.RaisePropertyChanged(nameof(RoomCode));
        }
        else
        {
            HasError = true;
            StatusMessage = "Failed to start host server.";
        }

        IsBusy = false;
    }

    private void UpdateInviteString()
    {
        if (string.IsNullOrEmpty(RoomCode)) return;

        string address;
        if (IsTunnelActive && !string.IsNullOrEmpty(PublicAddress))
        {
            address = PublicAddress;
            InviteStringLabel = "Tunnel invite (internet)";
        }
        else if (!string.IsNullOrEmpty(TailscaleIp))
        {
            address = GetLocalIpAddress();
            InviteStringLabel = "LAN invite";
        }
        else
        {
            address = GetLocalIpAddress();
            InviteStringLabel = "LAN invite";
        }

        InviteString = $"sentrychan://join/{address}/{RoomCode}";
        QrCodeBitmap = GenerateQrCode(InviteString);
        
        if (!string.IsNullOrEmpty(TailscaleIp))
        {
            TailscaleInviteString = $"sentrychan://join/{TailscaleIp}:7742/{RoomCode}";
        }
    }

    private Bitmap GenerateQrCode(string text)
    {
        using var qrGenerator = new QRCodeGenerator();
        using var qrData = qrGenerator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        using var qrCode = new PngByteQRCode(qrData);
        var pngBytes = qrCode.GetGraphic(6);
        using var ms = new MemoryStream(pngBytes);
        return new Bitmap(ms);
    }

    private string GetLocalIpAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            var ip = (socket.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
            return $"{ip}:7742";
        }
        catch { return "127.0.0.1:7742"; }
    }

    private async Task CopyLinkAsync()
    {
        if (string.IsNullOrEmpty(InviteString)) return;

        if (CopyToClipboard != null)
        {
            await CopyToClipboard(InviteString);
            StatusMessage = "Copied invite link to clipboard!";
            HasError = false;
        }
    }

    private async Task ShareDiscordAsync()
    {
        await CopyLinkAsync();
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "discord://",
                UseShellExecute = true
            });
        }
        catch
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://discord.com/channels/@me",
                UseShellExecute = true
            });
        }
    }

    private async Task BrowseFileAsync()
    {
        // Simple mock since we don't have the dialog service here directly
        await Task.CompletedTask;
    }

    private async Task SearchJikanAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(CustomSeriesSearch) || _animeApiService == null) return;

        IsSearching = true;
        try
        {
            var results = await _animeApiService.SearchAnimeAsync(CustomSeriesSearch, ct: ct);
            SearchResults.Clear();
            foreach (var r in results) SearchResults.Add(r);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Jikan search failed in WatchParty dialog");
        }
        finally { IsSearching = false; }
    }

    private void SelectSearchResult(AnimeResult result)
    {
        SelectedSearchResult = result;
        CustomSeriesTitle = result.Title;
        CustomMalId = result.MalId;
        SearchResults.Clear();
    }

    private void ClearSearchResult()
    {
        SelectedSearchResult = null;
        CustomMalId = null;
    }

    private async Task ToggleTunnelAsync(CancellationToken ct)
    {
        if (_tunnelService == null) return;

        if (IsTunnelActive)
        {
            await _tunnelService.StopTunnelAsync();
            IsTunnelActive = false;
            PublicAddress = null;
            TunnelButtonLabel = "Activate Tunnel";
            TunnelStatusMessage = "Tunnel deactivated.";
            UpdateInviteString();
        }
        else
        {
            TunnelStatusMessage = "Activating...";
            IsBusy = true;
            try 
            {
                var result = await _tunnelService.StartTunnelAsync(7742, ct);
                if (result.Success)
                {
                    PublicAddress = result.PublicAddress;
                    IsTunnelActive = true;
                    TunnelButtonLabel = "Deactivate Tunnel";
                    TunnelStatusMessage = $"Active: {result.PublicAddress}";
                    UpdateInviteString();
                }
                else
                {
                    TunnelStatusMessage = "Failed to activate tunnel.";
                }
            }
            finally
            {
                IsBusy = false;
            }
        }
    }

    private async void CheckTunnelPrerequisites()
    {
        if (_dbFactory == null) return;
        try 
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var config = await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == "FrpServerAddress");
            IsTunnelConfigured = !string.IsNullOrEmpty(config?.Value);
            CanActivateTunnel = IsTunnelConfigured;
            if (CanActivateTunnel)
                TunnelStatusMessage = "Ready to activate";
            else
                TunnelStatusMessage = "Not configured";
                TunnelTooltip = "Configure FRP server address in Settings to enable tunneling.";
        }
        catch { }
    }
}
