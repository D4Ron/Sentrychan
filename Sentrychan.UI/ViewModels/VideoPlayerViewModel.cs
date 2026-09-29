using Avalonia.Threading;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Sentrychan.Core.Vault;
using Sentrychan.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;

namespace Sentrychan.UI.ViewModels;

/// <summary>One thing to play: a file in the library, or an encrypted vault entry.</summary>
public sealed record PlaybackItem(string Title, string? FilePath = null, string? VaultId = null);

public sealed record TrackOption(int Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The in-app player. LibVLC plays anything the library holds (MKV with styled ASS subtitles,
/// multiple audio tracks) and reads vault entries straight from their decrypting stream, so
/// a private video is never written to disk in the clear to be watched.
/// </summary>
public sealed class VideoPlayerViewModel : ViewModelBase, IDisposable
{
    private readonly IReadOnlyList<PlaybackItem> _playlist;
    private readonly VaultService? _vault;
    private readonly ILogger? _log;
    private int _index;
    private LibVLC? _libVlc;
    private Media? _media;
    private StreamMediaInput? _input;
    private Stream? _vaultStream;
    private DateTime _lastSave = DateTime.MinValue;
    private bool _disposed;

    public VideoPlayerViewModel(IReadOnlyList<PlaybackItem> playlist, int startIndex, VaultService? vault, ILogger? log)
    {
        _playlist = playlist;
        _index = Math.Clamp(startIndex, 0, Math.Max(0, playlist.Count - 1));
        _vault = vault;
        _log = log;

        PlayPauseCommand = ReactiveCommand.Create(PlayPause);
        NextCommand      = ReactiveCommand.Create(() => Go(_index + 1));
        PreviousCommand  = ReactiveCommand.Create(() => Go(_index - 1));
        ToggleMuteCommand = ReactiveCommand.Create(() => { IsMuted = !IsMuted; });
        BackCommand      = ReactiveCommand.Create(() => SeekBy(-10));
        ForwardCommand   = ReactiveCommand.Create(() => SeekBy(10));
    }

    // ── Bindable state ────────────────────────────────────────────────

    private MediaPlayer? _player;
    public MediaPlayer? Player { get => _player; private set => this.RaiseAndSetIfChanged(ref _player, value); }

    private string _title = string.Empty;
    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }

    private bool _isPlaying;
    public bool IsPlaying { get => _isPlaying; private set => this.RaiseAndSetIfChanged(ref _isPlaying, value); }

    private double _position;
    /// <summary>0–1000 along the seek bar.</summary>
    public double Position { get => _position; private set => this.RaiseAndSetIfChanged(ref _position, value); }

    private string _timeText = "0:00";
    public string TimeText { get => _timeText; private set => this.RaiseAndSetIfChanged(ref _timeText, value); }

    private string _lengthText = "0:00";
    public string LengthText { get => _lengthText; private set => this.RaiseAndSetIfChanged(ref _lengthText, value); }

    private string? _status;
    public string? Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private int _volume = 100;
    public int Volume
    {
        get => _volume;
        set
        {
            this.RaiseAndSetIfChanged(ref _volume, Math.Clamp(value, 0, 200));
            if (Player != null) Player.Volume = _volume;
        }
    }

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            this.RaiseAndSetIfChanged(ref _isMuted, value);
            if (Player != null) Player.Mute = value;
        }
    }

    public bool HasNext => _index < _playlist.Count - 1;
    public bool HasPrevious => _index > 0;
    public bool HasPlaylist => _playlist.Count > 1;

    public ObservableCollection<TrackOption> AudioTracks { get; } = new();
    public ObservableCollection<TrackOption> SubtitleTracks { get; } = new();

    private TrackOption? _audioTrack;
    public TrackOption? AudioTrack
    {
        get => _audioTrack;
        set
        {
            this.RaiseAndSetIfChanged(ref _audioTrack, value);
            if (value != null && Player != null && Player.AudioTrack != value.Id) Player.SetAudioTrack(value.Id);
        }
    }

    private TrackOption? _subtitleTrack;
    public TrackOption? SubtitleTrack
    {
        get => _subtitleTrack;
        set
        {
            this.RaiseAndSetIfChanged(ref _subtitleTrack, value);
            if (value != null && Player != null && Player.Spu != value.Id) Player.SetSpu(value.Id);
        }
    }

    public ReactiveCommand<Unit, Unit> PlayPauseCommand { get; }
    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleMuteCommand { get; }
    public ReactiveCommand<Unit, Unit> BackCommand { get; }
    public ReactiveCommand<Unit, Unit> ForwardCommand { get; }

    // ── Lifecycle ─────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        if (Sentrychan.UI.Services.VideoSupport.Problem is { } problem) { Status = problem; return; }

        // Constructing LibVLC on the UI thread freezes the app for seconds — always off-thread.
        LibVLC libVlc;
        MediaPlayer player;
        try
        {
            (libVlc, player) = await Task.Run(() =>
            {
                var lib = new LibVLC("--no-video-title-show", "--quiet");
                return (lib, new MediaPlayer(lib) { EnableHardwareDecoding = true });
            });
        }
        catch (Exception ex) when (ex is VLCException or DllNotFoundException or TypeInitializationException)
        {
            Status = Sentrychan.UI.Services.VideoSupport.Explain(Sentrychan.Core.AppPaths.CurrentOs);
            return;
        }
        if (_disposed) { player.Dispose(); libVlc.Dispose(); return; }

        _libVlc = libVlc;
        player.Playing          += (_, _) => Ui(() => { IsPlaying = true; LoadTracks(); });
        player.Paused           += (_, _) => Ui(() => IsPlaying = false);
        player.Stopped          += (_, _) => Ui(() => IsPlaying = false);
        player.LengthChanged    += (_, e) => Ui(() => LengthText = Format(e.Length));
        player.TimeChanged      += (_, e) => Ui(() => OnTime(e.Time));
        player.ESAdded          += (_, _) => Ui(LoadTracks);
        // VLC events arrive on VLC's own thread, and calling back into VLC from there
        // deadlocks — so moving on to the next episode is posted to the UI thread.
        player.EndReached       += (_, _) => Ui(() => { SaveProgress(force: true, finished: true); if (HasNext) Go(_index + 1); });
        player.EncounteredError += (_, _) => Ui(() => Status = "This file couldn't be played.");
        Player = player;
        Player.Volume = Volume;

        Go(_index);
    }

    private static void Ui(Action a) => Dispatcher.UIThread.Post(a);

    private void Go(int index)
    {
        if (Player == null || _libVlc == null || index < 0 || index >= _playlist.Count) return;
        SaveProgress(force: true);

        _index = index;
        var item = _playlist[index];
        Title = item.Title;
        Status = null;
        this.RaisePropertyChanged(nameof(HasNext));
        this.RaisePropertyChanged(nameof(HasPrevious));

        ReleaseMedia();
        try
        {
            if (item.VaultId != null && _vault != null)
            {
                _vaultStream = _vault.OpenRead(item.VaultId);
                _input = new StreamMediaInput(_vaultStream);
                _media = new Media(_libVlc, _input);
            }
            else if (item.FilePath != null && File.Exists(item.FilePath))
            {
                _media = new Media(_libVlc, new Uri(item.FilePath));
            }
            else
            {
                Status = "That file is no longer there.";
                return;
            }

            // Resume where it was left, unless that was within the first or last half-minute.
            var resume = ResumePoint(item);
            if (resume > 0) _media.AddOption($":start-time={resume:F0}");

            Player.Play(_media);
            _log?.LogInformation("[Player] Playing {Kind} item {Index}/{Count}{Resume}",
                item.VaultId != null ? "vault" : "library", index + 1, _playlist.Count,
                resume > 0 ? $", resuming at {Format((long)(resume * 1000))}" : "");
        }
        catch (Exception ex)
        {
            Status = "This file couldn't be opened.";
            _log?.LogWarning("[Player] Open failed: {Error}", ex.Message);
        }
    }

    private double ResumePoint(PlaybackItem item)
    {
        (double Position, double Duration)? saved = null;
        if (item.VaultId != null)
        {
            var e = _vault?.Get(item.VaultId);
            if (e != null) saved = (e.PositionSeconds, e.DurationSeconds);
        }
        else if (item.FilePath != null) saved = PlaybackPositionStore.Get(item.FilePath);

        if (saved is not { } s || s.Position < 30) return 0;
        if (s.Duration > 0 && s.Position > s.Duration - 30) return 0;
        return s.Position;
    }

    private void OnTime(long ms)
    {
        TimeText = Format(ms);
        var length = Player?.Length ?? 0;
        if (length > 0) Position = ms * 1000.0 / length;
        SaveProgress(force: false);
    }

    private void SaveProgress(bool force, bool finished = false)
    {
        if (Player == null || _playlist.Count == 0) return;
        if (!force && DateTime.UtcNow - _lastSave < TimeSpan.FromSeconds(10)) return;
        _lastSave = DateTime.UtcNow;

        var length = Player.Length / 1000.0;
        var time = finished ? length : Player.Time / 1000.0;
        if (length <= 0 || time < 0) return;

        var item = _playlist[_index];
        if (item.VaultId != null) _ = _vault?.SavePositionAsync(item.VaultId, time, length);
        else if (item.FilePath != null) PlaybackPositionStore.Set(item.FilePath, time, length);
    }

    private void LoadTracks()
    {
        if (Player == null) return;

        AudioTracks.Clear();
        foreach (var t in Player.AudioTrackDescription.Where(t => t.Id >= 0))
            AudioTracks.Add(new TrackOption(t.Id, t.Name));
        _audioTrack = AudioTracks.FirstOrDefault(t => t.Id == Player.AudioTrack);
        this.RaisePropertyChanged(nameof(AudioTrack));

        SubtitleTracks.Clear();
        foreach (var t in Player.SpuDescription)
            SubtitleTracks.Add(new TrackOption(t.Id, t.Id < 0 ? "Off" : t.Name));
        _subtitleTrack = SubtitleTracks.FirstOrDefault(t => t.Id == Player.Spu);
        this.RaisePropertyChanged(nameof(SubtitleTrack));
    }

    // ── Actions ───────────────────────────────────────────────────────

    public void PlayPause()
    {
        if (Player == null) return;
        if (Player.IsPlaying) Player.Pause();
        else if (Player.State == VLCState.Ended) Go(_index);
        else Player.Play();
    }

    public void SeekBy(double seconds)
    {
        if (Player == null || !Player.IsSeekable) return;
        Player.Time = Math.Clamp(Player.Time + (long)(seconds * 1000), 0, Math.Max(0, Player.Length - 1000));
    }

    /// <summary>Seek to a point on the 0–1000 seek bar.</summary>
    public void SeekTo(double position)
    {
        if (Player == null || !Player.IsSeekable) return;
        Player.Position = (float)Math.Clamp(position / 1000.0, 0, 0.999);
    }

    public void CycleSubtitles()
    {
        if (SubtitleTracks.Count == 0) return;
        var i = SubtitleTrack == null ? -1 : SubtitleTracks.IndexOf(SubtitleTrack);
        SubtitleTrack = SubtitleTracks[(i + 1) % SubtitleTracks.Count];
    }

    public void CycleAudio()
    {
        if (AudioTracks.Count == 0) return;
        var i = AudioTrack == null ? -1 : AudioTracks.IndexOf(AudioTrack);
        AudioTrack = AudioTracks[(i + 1) % AudioTracks.Count];
    }

    private static string Format(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";
    }

    private void ReleaseMedia()
    {
        _media?.Dispose(); _media = null;
        _input?.Dispose(); _input = null;
        _vaultStream?.Dispose(); _vaultStream = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SaveProgress(force: true);

        var player = Player;
        var lib = _libVlc;
        var media = _media; var input = _input; var stream = _vaultStream;
        _media = null; _input = null; _vaultStream = null;
        Player = null;

        // Stopping VLC can block briefly; do it off the UI thread.
        Task.Run(() =>
        {
            try { player?.Stop(); } catch { }
            player?.Dispose();
            media?.Dispose(); input?.Dispose(); stream?.Dispose();
            lib?.Dispose();
        });
    }
}
