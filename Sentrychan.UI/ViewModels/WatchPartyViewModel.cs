using ReactiveUI;
using Sentrychan.Core.Interfaces;
using Sentrychan.UI.Interfaces;
using Sentrychan.Core.Models;
using Sentrychan.Core.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace Sentrychan.UI.ViewModels;

public class WatchPartyViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IWatchPartyClientService _client;
    private readonly IWatchPartyHostService _host;
    private readonly IPlayerBridgeService _player;
    private readonly ISyncEngine _sync;
    private readonly IReactionOverlayService _reactionService;
    private readonly IVoiceChatService _voiceService;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILanDiscoveryService _lanService;
    private readonly ILogger<WatchPartyViewModel>? _logger;
    private readonly string _hostIp;

    private bool _isHost;
    public bool IsHost
    {
        get => _isHost;
        set => this.RaiseAndSetIfChanged(ref _isHost, value);
    }
    public IReactionOverlayService ReactionService => _reactionService;
    public IVoiceChatService VoiceService => _voiceService;

    private Series? _currentSeries;
    public Series? CurrentSeries
    {
        get => _currentSeries;
        set => this.RaiseAndSetIfChanged(ref _currentSeries, value);
    }
    
    // -- Playback State --
    private bool _isPlaying;
    public bool IsPlaying { get => _isPlaying; set => this.RaiseAndSetIfChanged(ref _isPlaying, value); }
    private double _currentPosition;
    public double CurrentPosition { get => _currentPosition; set => this.RaiseAndSetIfChanged(ref _currentPosition, value); }
    private double _duration;
    public double Duration { get => _duration; set => this.RaiseAndSetIfChanged(ref _duration, value); }
    public double PositionPercent => Duration > 0 ? (CurrentPosition / Duration * 100) : 0;
    public string FormattedPosition => TimeSpan.FromSeconds(CurrentPosition).ToString(CurrentPosition >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");
    public string FormattedDuration => TimeSpan.FromSeconds(Duration).ToString(Duration >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");
    private int _volume = 100;
    public int Volume { get => _volume; set => this.RaiseAndSetIfChanged(ref _volume, value); }
    private bool _isMuted;
    public bool IsMuted { get => _isMuted; set => this.RaiseAndSetIfChanged(ref _isMuted, value); }
    private bool _isBuffering;
    public bool IsBuffering { get => _isBuffering; set => this.RaiseAndSetIfChanged(ref _isBuffering, value); }
    private bool _isSyncing;
    public bool IsSyncing { get => _isSyncing; set => this.RaiseAndSetIfChanged(ref _isSyncing, value); }
    
    // -- Controls Visibility --
    private bool _areControlsVisible;
    public bool AreControlsVisible { get => _areControlsVisible; set => this.RaiseAndSetIfChanged(ref _areControlsVisible, value); }
    private bool _isSeekBarHovered;
    public bool IsSeekBarHovered { get => _isSeekBarHovered; set => this.RaiseAndSetIfChanged(ref _isSeekBarHovered, value); }
    
    private ObservableCollection<ParticipantRowVm> _participants = new();
    public ObservableCollection<ParticipantRowVm> Participants
    {
        get => _participants;
        set => this.RaiseAndSetIfChanged(ref _participants, value);
    }

    private ObservableCollection<ChatMessageVm> _chatMessages = new();
    public ObservableCollection<ChatMessageVm> ChatMessages
    {
        get => _chatMessages;
        set => this.RaiseAndSetIfChanged(ref _chatMessages, value);
    }

    public bool IsShowingEntry => !IsInSession && !IsInLobby;

    private bool _isInLobby;
    public bool IsInLobby
    {
        get => _isInLobby;
        set
        {
            this.RaiseAndSetIfChanged(ref _isInLobby, value);
            this.RaisePropertyChanged(nameof(IsShowingEntry));
            if (value)
            {
                _logger?.LogInformation("[LOBBY] IsHost={IsHost}, VideoSource={Video}", IsHost, VideoSource);
            }
        }
    }

    private bool _isInSession;
    public bool IsInSession
    {
        get => _isInSession;
        set
        {
            this.RaiseAndSetIfChanged(ref _isInSession, value);
            this.RaisePropertyChanged(nameof(IsShowingEntry));
            if (value) InitializeSession();
        }
    }

    private string _messageInput = string.Empty;
    public string MessageInput
    {
        get => _messageInput;
        set => this.RaiseAndSetIfChanged(ref _messageInput, value);
    }

    private bool _isReady;
    public bool IsReady
    {
        get => _isReady;
        set => this.RaiseAndSetIfChanged(ref _isReady, value);
    }

    public ReactiveCommand<Unit, Unit> LeaveCommand { get; }
    private string? _downloadUrl;
    public string? DownloadUrl
    {
        get => _downloadUrl;
        set => this.RaiseAndSetIfChanged(ref _downloadUrl, value);
    }

    private string? _sharedDownloadUrl;
    public string? SharedDownloadUrl
    {
        get => _sharedDownloadUrl;
        set => this.RaiseAndSetIfChanged(ref _sharedDownloadUrl, value);
    }

    public ReactiveCommand<Unit, Unit> SendMessageCommand { get; }
    public ReactiveCommand<Unit, Unit> ShareDownloadCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenSharedDownloadCommand { get; }
    
    // Playback Commands
    public ReactiveCommand<Unit, Unit> PlayPauseCommand { get; }
    public ReactiveCommand<double, Unit> SeekCommand { get; }
    public ReactiveCommand<Unit, Unit> SkipForwardCommand { get; }
    public ReactiveCommand<Unit, Unit> SkipBackCommand { get; }
    public ReactiveCommand<int, Unit> SetVolumeCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleMuteCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleFullscreenCommand { get; }
    public ReactiveCommand<string, Unit> SendReactionCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleReadyCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenCreatePartyCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenJoinPartyCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportChatCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenDiscordCommand { get; }
    public ReactiveCommand<Unit, Unit> StartCommand { get; }

    public string VideoSource { get; set; } = string.Empty;

    public IPlayerBridgeService Player => _player;

    public WatchPartyViewModel(
        IWatchPartyClientService client,
        IWatchPartyHostService host,
        IPlayerBridgeService player,
        ISyncEngine sync,
        IReactionOverlayService reactionService,
        IVoiceChatService voiceService,
        IDbContextFactory<AppDbContext> dbFactory,
        ILanDiscoveryService lanService,
        ILogger<WatchPartyViewModel>? logger = null,
        Series? currentSeries = null,
        string hostIp = "127.0.0.1")
    {
        _client = client;
        _host = host;
        _player = player;
        _sync = sync;
        _reactionService = reactionService;
        _voiceService = voiceService;
        _dbFactory = dbFactory;
        _lanService = lanService;
        _logger = logger;
        _hostIp = hostIp;
        CurrentSeries = currentSeries;
        // Don't set IsInSession here — defer until user explicitly enters

        // Disconnect & Download
        LeaveCommand = ReactiveCommand.CreateFromTask(async ct => await DisconnectAsync(ct));
        SendMessageCommand = ReactiveCommand.CreateFromTask(async ct => await SendMessageAsync(ct));
        ShareDownloadCommand = ReactiveCommand.CreateFromTask(async ct => await ShareDownloadLinkAsync(ct));
        OpenSharedDownloadCommand = ReactiveCommand.Create(() => OpenSharedDownload());
        
        // Playback Commands
        var isHostObservable = this.WhenAnyValue(x => x.IsHost);
        PlayPauseCommand = ReactiveCommand.CreateFromTask(async ct => await PlayPauseAsync(ct), isHostObservable);
        SeekCommand = ReactiveCommand.CreateFromTask<double>(async (pos, ct) => await SeekAsync(pos, ct), isHostObservable);
        SkipForwardCommand = ReactiveCommand.CreateFromTask(async ct => await SkipForwardAsync(ct), isHostObservable);
        SkipBackCommand = ReactiveCommand.CreateFromTask(async ct => await SkipBackAsync(ct), isHostObservable);
        SetVolumeCommand = ReactiveCommand.CreateFromTask<int>(async (vol, ct) => await SetVolumeAsync(vol, ct));
        ToggleMuteCommand = ReactiveCommand.CreateFromTask(async ct => await ToggleMuteAsync(ct));
        ToggleFullscreenCommand = ReactiveCommand.Create(ToggleFullscreen);

        SendReactionCommand = ReactiveCommand.CreateFromTask<string>(async (r, ct) => await SendReactionAsync(r, ct));
        ToggleReadyCommand = ReactiveCommand.CreateFromTask(async ct => await ToggleReadyAsync(ct));
        ExportChatCommand = ReactiveCommand.CreateFromTask(async ct => await ExportChatAsync(ct));
        
        OpenCreatePartyCommand = ReactiveCommand.Create(() => 
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
            {
                mainVm.ShowCreatePartyCommand.Execute().Subscribe();
            }
        });

        OpenJoinPartyCommand = ReactiveCommand.Create(() => 
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
            {
                mainVm.ShowJoinPartyCommand.Execute().Subscribe();
            }
        });

        OpenDiscordCommand = ReactiveCommand.Create(() => 
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://discord.gg/anime") { UseShellExecute = true }); }
            catch { /* Log error */ }
        });

        StartCommand = ReactiveCommand.CreateFromTask(StartSessionAsync);

        // NOTE: Subscriptions are NOT set up here. They are deferred to
        // EnsureSubscribedAsync() which is called when the user actually
        // enters a session (via ConnectHostAsync / StartSessionAsync).
    }

    // Named handler methods so they can be unsubscribed
    private void OnUserJoined(string user)
    {
        Dispatcher.UIThread.Post(() => Participants.Add(new ParticipantRowVm { Username = user, Guid = user }));
        Dispatcher.UIThread.Post(() => ChatMessages.Add(new ChatMessageVm 
        { 
            Sender = "SYSTEM", 
            Content = $"{user} joined the party.", 
            Timestamp = DateTime.Now 
        }));
    }

    private void OnUserLeft(string user)
    {
        var p = Participants.FirstOrDefault(x => x.Guid == user);
        if (p != null) Dispatcher.UIThread.Post(() => Participants.Remove(p));
        
        Dispatcher.UIThread.Post(() => ChatMessages.Add(new ChatMessageVm 
        { 
            Sender = "SYSTEM", 
            Content = $"{user} left the party.", 
            Timestamp = DateTime.Now 
        }));
    }

    private void OnSessionInfoReceived(string title, int ep)
    {
        Dispatcher.UIThread.Post(() => {
            CurrentSeries = new Series { Title = title };
        });
    }

    private void OnDownloadLinkReceived(string url)
    {
        SharedDownloadUrl = url;
        Dispatcher.UIThread.Post(() => ChatMessages.Add(new ChatMessageVm 
        { 
            Sender = "SYSTEM", 
            Content = "The host shared a download link for this episode.", 
            Timestamp = DateTime.Now 
        }));
    }

    private void OnChatMessageReceived(string user, string msg) =>
        Dispatcher.UIThread.Post(() => ChatMessages.Add(new ChatMessageVm { Sender = user, Content = msg, Timestamp = DateTime.Now }));

    private void OnReconnecting(string? err) =>
        Dispatcher.UIThread.Post(() => ChatMessages.Add(new ChatMessageVm { Sender = "SYSTEM", Content = "Connection lost. Reconnecting...", Timestamp = DateTime.Now }));

    private void OnReconnected(string? id) =>
        Dispatcher.UIThread.Post(() => ChatMessages.Add(new ChatMessageVm { Sender = "SYSTEM", Content = "Reconnected!", Timestamp = DateTime.Now }));

    private async void OnPlaybackCommandReceived(string cmd, double pos)
    {
        if (cmd == "Play") await Dispatcher.UIThread.InvokeAsync(() => _player.Play());
        else if (cmd == "Pause") await Dispatcher.UIThread.InvokeAsync(() => _player.Pause());
        else if (cmd == "Seek") await Dispatcher.UIThread.InvokeAsync(() => _player.Seek(pos));
        // Note: SyncEngine now handles ReportPosition internally via client service subscription
    }

    private void OnReactionReceived(string user, string reaction) =>
        _reactionService.ShowReaction(user, reaction);

    private void OnParticipantReadyChanged(string user, bool ready)
    {
        var p = Participants.FirstOrDefault(x => x.Guid == user);
        if (p != null) p.IsReady = ready;
    }

    private void OnSyncCorrection(SyncCorrection correction)
    {
        if (correction.Type == SyncCorrectionType.Hard)
        {
            Dispatcher.UIThread.Post(() => ChatMessages.Add(new ChatMessageVm 
            { 
                Sender = "SYSTEM", 
                Content = $"Syncing... ({correction.DriftSeconds:F1}s drift corrected)", 
                Timestamp = DateTime.Now 
            }));
        }
    }

    private void InitializeSession()
    {
        if (CurrentSeries == null) return;

        // Initialize Voice Chat with proper IP
        if (IsHost)
        {
            _voiceService.StartServer(7743);
            _ = _lanService.StartBroadcastingAsync(Guid.NewGuid().ToString(), CurrentSeries.Title, CancellationToken.None);
        }
        else
        {
            _voiceService.StartClient(_hostIp, 7743);
        }
    }

    private bool _isSubscribed;

    /// <summary>
    /// Sets up event handlers and sync engine. Safe to call multiple times — idempotent.
    /// </summary>
    private void EnsureSubscribed()
    {
        if (_isSubscribed) return;
        _isSubscribed = true;

        _sync.SetLocalPlayer(_player);

        _client.UserJoined += OnUserJoined;
        _client.ChatMessageReceived += OnChatMessageReceived;
        _client.Reconnecting += OnReconnecting;
        _client.Reconnected += OnReconnected;
        _client.PlaybackCommandReceived += OnPlaybackCommandReceived;
        _client.ReactionReceived += OnReactionReceived;
        _client.ParticipantReadyChanged += OnParticipantReadyChanged;
        _client.DownloadLinkReceived += OnDownloadLinkReceived;
        _client.SessionInfoReceived += OnSessionInfoReceived;
        _client.UserLeft += OnUserLeft;

        _sync.CorrectionRequired += OnSyncCorrection;
        _sync.OnSyncStatusChanged += OnSyncStatusChanged;

        _player.PositionChanged += OnPositionChanged;
        _player.PlaybackStateChanged += OnPlaybackStateChanged;
        _player.DurationChanged += OnDurationChanged;
    }

    private void OnSyncStatusChanged(bool isSyncing)
    {
        Dispatcher.UIThread.Post(() => IsSyncing = isSyncing);
    }

    private void UnsubscribeEventHandlers()
    {
        if (!_isSubscribed) return;
        _isSubscribed = false;

        _client.UserJoined -= OnUserJoined;
        _client.ChatMessageReceived -= OnChatMessageReceived;
        _client.Reconnecting -= OnReconnecting;
        _client.Reconnected -= OnReconnected;
        _client.PlaybackCommandReceived -= OnPlaybackCommandReceived;
        _client.ReactionReceived -= OnReactionReceived;
        _client.ParticipantReadyChanged -= OnParticipantReadyChanged;
        _client.DownloadLinkReceived -= OnDownloadLinkReceived;
        _client.SessionInfoReceived -= OnSessionInfoReceived;
        _client.UserLeft -= OnUserLeft;

        _sync.CorrectionRequired -= OnSyncCorrection;
        _sync.OnSyncStatusChanged -= OnSyncStatusChanged;
        
        _player.PositionChanged -= OnPositionChanged;
        _player.PlaybackStateChanged -= OnPlaybackStateChanged;
        _player.DurationChanged -= OnDurationChanged;
        
        _ = _sync.StopAsync();
    }

    private async void OnPositionChanged(double pos)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            CurrentPosition = pos;
            this.RaisePropertyChanged(nameof(PositionPercent));
            this.RaisePropertyChanged(nameof(FormattedPosition));
            if (Duration <= 0 && pos > 0)
            {
                Duration = await _player.GetDurationAsync();
                this.RaisePropertyChanged(nameof(PositionPercent));
                this.RaisePropertyChanged(nameof(FormattedDuration));
            }
        });
    }

    private void OnPlaybackStateChanged(bool playing)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsPlaying = playing;
            if (!playing) AreControlsVisible = true;
        });
    }

    private void OnDurationChanged(double dur)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Duration = dur;
            this.RaisePropertyChanged(nameof(FormattedDuration));
            this.RaisePropertyChanged(nameof(PositionPercent));
        });
    }

    public void StartVoiceCapture() => _voiceService.StartCapture();
    public void StopVoiceCapture() => _voiceService.StopCapture();

    private async Task SendMessageAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(MessageInput)) return;
        await _client.SendMessageAsync(MessageInput, ct);
        MessageInput = string.Empty;
    }

    private async Task ShareDownloadLinkAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(DownloadUrl)) return;
        await _client.ShareDownloadLinkAsync(DownloadUrl, ct);
        Dispatcher.UIThread.Post(() => ChatMessages.Add(new ChatMessageVm 
        { 
            Sender = "SYSTEM", 
            Content = "You shared a download link with the lobby.", 
            Timestamp = DateTime.Now 
        }));
    }

    private void OpenSharedDownload()
    {
        if (string.IsNullOrEmpty(SharedDownloadUrl)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SharedDownloadUrl) { UseShellExecute = true }); }
        catch { /* Log error */ }
    }

    private async Task SendReactionAsync(string reaction, CancellationToken ct)
    {
        await _client.SendReactionAsync(reaction, ct);
    }

    private async Task ToggleReadyAsync(CancellationToken ct)
    {
        IsReady = !IsReady;
        await _client.SetReadyAsync(IsReady, ct);
    }

    private async Task StartSessionAsync(CancellationToken ct)
    {
        _logger?.LogInformation("[START] StartCommand fired");

        // Ensure event handlers are wired up before starting
        EnsureSubscribed();

        _logger?.LogInformation("[START] Broadcasting Play to all participants");

        // Broadcast to all connected participants via SignalR
        await _host.BroadcastPlaybackCommandAsync("Play", 0, ct);

        // Open the video in the local player
        if (!string.IsNullOrWhiteSpace(VideoSource))
        {
            _logger?.LogInformation("[START] Opening video: {Path}", VideoSource);
            try
            {
                _player.SetSource(VideoSource);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[START] Player failed to open video");
                // Continue anyway -- participants may have different paths
            }
        }

        // Transition view state on UI thread
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsInLobby = false;
            IsInSession = true;
            _logger?.LogInformation("[START] View transitioned to active session");
        });

        // Start sync engine as host
        await _sync.StartAsync(isHost: true, ct);
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        _logger?.LogInformation("[DISCONNECT] Disconnecting from watch party");
        
        UnsubscribeEventHandlers();
        
        _player.Stop();
        if (IsHost)
        {
            await _host.StopPartyAsync(ct);
        }
        else
        {
            await _client.DisconnectAsync(ct);
        }

        await _sync.StopAsync();
        await _lanService.StopBroadcastingAsync();
        
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsInSession = false;
            IsInLobby = false;
        });
    }

    private async Task PlayPauseAsync(CancellationToken ct)
    {
        if (!IsHost) return;
        double pos = await _player.GetPositionAsync();
        if (IsPlaying)
            await _host.BroadcastPlaybackCommandAsync("Pause", pos, ct);
        else
            await _host.BroadcastPlaybackCommandAsync("Play", pos, ct);
    }

    private async Task SeekAsync(double percent, CancellationToken ct)
    {
        if (!IsHost || Duration <= 0) return;
        double targetSeconds = percent / 100.0 * Duration;
        await _host.BroadcastPlaybackCommandAsync("Seek", targetSeconds, ct);
        await _player.SeekAsync(targetSeconds);
    }

    private async Task SkipForwardAsync(CancellationToken ct)
    {
        if (!IsHost || Duration <= 0) return;
        double current = await _player.GetPositionAsync();
        double target = Math.Min(current + 10, Duration);
        await _host.BroadcastPlaybackCommandAsync("Seek", target, ct);
        await _player.SeekAsync(target);
    }

    private async Task SkipBackAsync(CancellationToken ct)
    {
        if (!IsHost) return;
        double current = await _player.GetPositionAsync();
        double target = Math.Max(current - 10, 0);
        await _host.BroadcastPlaybackCommandAsync("Seek", target, ct);
        await _player.SeekAsync(target);
    }

    private async Task SetVolumeAsync(int vol, CancellationToken ct)
    {
        Volume = vol;
        IsMuted = vol == 0;
        await _player.SetVolumeAsync(vol);
    }

    private async Task ToggleMuteAsync(CancellationToken ct)
    {
        IsMuted = !IsMuted;
        await _player.SetVolumeAsync(IsMuted ? 0 : Volume);
    }

    private void ToggleFullscreen()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow != null)
            {
                desktop.MainWindow.WindowState = desktop.MainWindow.WindowState == Avalonia.Controls.WindowState.FullScreen
                    ? Avalonia.Controls.WindowState.Normal
                    : Avalonia.Controls.WindowState.FullScreen;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        UnsubscribeEventHandlers();
        if (_isHost)
            await _host.StopPartyAsync();
        else
            await _client.DisconnectAsync();
        await _lanService.StopBroadcastingAsync();
    }

    private async Task ExportChatAsync(CancellationToken ct)
    {
        if (ChatMessages.Count == 0) return;

        try
        {
            var fileName = $"ChatLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var filePath = System.IO.Path.Combine(desktop, fileName);

            using var sw = new System.IO.StreamWriter(filePath);
            await sw.WriteLineAsync($"Chat Log for {CurrentSeries?.Title ?? "Unknown"}");
            await sw.WriteLineAsync($"Exported at {DateTime.Now}");
            await sw.WriteLineAsync("----------------------------------------");

            foreach (var msg in ChatMessages)
            {
                await sw.WriteLineAsync($"[{msg.Timestamp:HH:mm:ss}] {msg.Sender}: {msg.Content}");
            }

            ChatMessages.Add(new ChatMessageVm { Sender = "SYSTEM", Content = $"Chat exported to Desktop as {fileName}", Timestamp = DateTime.Now });
        }
        catch (Exception ex)
        {
            ChatMessages.Add(new ChatMessageVm { Sender = "SYSTEM", Content = $"Export failed: {ex.Message}", Timestamp = DateTime.Now });
        }
    }

    public async Task<bool> ConnectHostAsync(string username, string password, CancellationToken ct = default)
    {
        if (_client == null || _host == null || string.IsNullOrEmpty(_host.CurrentSessionId)) return false;
        
        // Ensure event handlers are wired up before connecting
        EnsureSubscribed();

        bool hostSelfJoin = true; 
        try 
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var config = await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == "HostSelfJoin", ct);
            if (config != null && bool.TryParse(config.Value, out var boolVal))
                hostSelfJoin = boolVal;
            else if (config == null)
            {
                db.AppConfigs.Add(new AppConfig { Key = "HostSelfJoin", Value = "true" });
                await db.SaveChangesAsync(ct);
            }
        }
        catch { }

        if (hostSelfJoin)
        {
            return await _client.ConnectAsync("127.0.0.1", _host.Port, username, password, _host.CurrentSessionId, ct);
        }
        return true;
    }
}
