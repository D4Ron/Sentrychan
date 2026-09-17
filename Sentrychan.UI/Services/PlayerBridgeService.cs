using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Data;
using System;
using System.Threading.Tasks;

namespace Sentrychan.UI.Services;

public class PlayerBridgeService : IPlayerBridgeService
{
    private readonly ILogger<PlayerBridgeService> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILoggerFactory _loggerFactory;
    private IPlayerBridgeService _activePlayer = new NullPlayerBridge();
    public PlayerType PlayerType { get; private set; } = PlayerType.Internal;

    public double CurrentPositionSeconds => _activePlayer.CurrentPositionSeconds;
    public double DurationSeconds => _activePlayer.DurationSeconds;
    public bool IsPlaying => _activePlayer.IsPlaying;

    public int Volume
    {
        get => _activePlayer.Volume;
        set => _activePlayer.Volume = value;
    }

    public bool IsMuted
    {
        get => _activePlayer.IsMuted;
        set => _activePlayer.IsMuted = value;
    }

    public event Action<double>? PositionChanged;
    public event Action<bool>? PlaybackStateChanged;
    public event Action<double>? DurationChanged;
    public event Action<string>? PlaybackError;

    public PlayerBridgeService(ILogger<PlayerBridgeService> logger, 
        IDbContextFactory<AppDbContext> dbFactory,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _dbFactory = dbFactory;
        _loggerFactory = loggerFactory;
    }

    public async Task InitializeAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var selectedPlayer = (await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == "SelectedPlayer"))?.Value ?? "Internal";
        
        if (selectedPlayer == "MPV")
        {
            var mpvPath = (await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == "MpvPath"))?.Value ?? "mpv";
            SetActivePlayer(new MpvPlayerBridge(_loggerFactory.CreateLogger<MpvPlayerBridge>(), mpvPath));
        }
        else if (selectedPlayer == "VLC")
        {
            var vlcPath = (await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == "VlcPath"))?.Value ?? "vlc";
            SetActivePlayer(new VlcPlayerBridge(_loggerFactory.CreateLogger<VlcPlayerBridge>(), vlcPath));
        }
        else if (selectedPlayer == "PotPlayer")
        {
            var potPath = (await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == "PotPlayerPath"))?.Value ?? "PotPlayer64.exe";
            SetActivePlayer(new PotPlayerBridge(_loggerFactory.CreateLogger<PotPlayerBridge>(), potPath));
        }
    }

    public void SetActivePlayer(IPlayerBridgeService player)
    {
        if (_activePlayer != null)
        {
            _activePlayer.PositionChanged -= OnPositionChanged;
            _activePlayer.PlaybackStateChanged -= OnPlayStateChanged;
            _activePlayer.DurationChanged -= OnDurationChanged;
            _activePlayer.PlaybackError -= OnPlaybackError;
        }

        _activePlayer = player;
        PlayerType = player.PlayerType;

        _activePlayer.PositionChanged += OnPositionChanged;
        _activePlayer.PlaybackStateChanged += OnPlayStateChanged;
        _activePlayer.DurationChanged += OnDurationChanged;
        _activePlayer.PlaybackError += OnPlaybackError;
    }

    private void OnPositionChanged(double pos) => PositionChanged?.Invoke(pos);
    private void OnPlayStateChanged(bool playing) => PlaybackStateChanged?.Invoke(playing);
    private void OnDurationChanged(double dur) => DurationChanged?.Invoke(dur);
    private void OnPlaybackError(string err) => PlaybackError?.Invoke(err);

    public Task PlayAsync() => _activePlayer.PlayAsync();
    public Task PauseAsync() => _activePlayer.PauseAsync();
    public Task SeekAsync(double positionSeconds) => _activePlayer.SeekAsync(positionSeconds);
    public Task<double> GetPositionAsync() => _activePlayer.GetPositionAsync();
    public Task<double> GetDurationAsync() => _activePlayer.GetDurationAsync();
    public Task SetVolumeAsync(int volume) => _activePlayer.SetVolumeAsync(volume);

    public void Play() => _activePlayer.Play();
    public void Pause() => _activePlayer.Pause();
    public void Seek(double pos) => _activePlayer.Seek(pos);
    public void SetSource(string src) => _activePlayer.SetSource(src);
    public void Stop() => _activePlayer.Stop();

    private sealed class NullPlayerBridge : IPlayerBridgeService
    {
        public PlayerType PlayerType => PlayerType.Internal;
        public double CurrentPositionSeconds => 0;
        public double DurationSeconds => 0;
        public bool IsPlaying => false;
        public int Volume { get; set; } = 100;
        public bool IsMuted { get; set; }
        public event Action<double>? PositionChanged;
        public event Action<bool>? PlaybackStateChanged;
        public event Action<double>? DurationChanged;
        public event Action<string>? PlaybackError;

        public Task PlayAsync() => Task.CompletedTask;
        public Task PauseAsync() => Task.CompletedTask;
        public Task SeekAsync(double positionSeconds) => Task.CompletedTask;
        public Task<double> GetPositionAsync() => Task.FromResult(0.0);
        public Task<double> GetDurationAsync() => Task.FromResult(0.0);
        public Task SetVolumeAsync(int volume) => Task.CompletedTask;

        public void Play() { }
        public void Pause() { }
        public void Seek(double positionSeconds) { }
        public void SetSource(string sourcePath) { }
        public void Stop() { }
    }
}
