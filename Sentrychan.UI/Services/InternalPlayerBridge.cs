using LibVLCSharp.Shared;
using Sentrychan.Core.Interfaces;
using Sentrychan.UI.Controls;
using System;
using Avalonia.Threading;

namespace Sentrychan.UI.Services;

public class InternalPlayerBridge : IPlayerBridgeService, IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly VideoPlayerControl _control;

    public PlayerType PlayerType => PlayerType.Internal;

    public double CurrentPositionSeconds => _mediaPlayer.Time / 1000.0;
    public double DurationSeconds => _mediaPlayer.Length / 1000.0;
    public bool IsPlaying => _mediaPlayer.IsPlaying;

    public int Volume
    {
        get => _mediaPlayer.Volume;
        set => _mediaPlayer.Volume = value;
    }

    public bool IsMuted
    {
        get => _mediaPlayer.Mute;
        set => _mediaPlayer.Mute = value;
    }

    public event Action<double>? PositionChanged;
    public event Action<bool>? PlaybackStateChanged;
    public event Action<double>? DurationChanged;
    public event Action<string>? PlaybackError;

    public InternalPlayerBridge(VideoPlayerControl control, LibVLC libVlc, MediaPlayer mediaPlayer)
    {
        _control = control;
        _libVlc = libVlc;
        _mediaPlayer = mediaPlayer;
        
        _control.View.MediaPlayer = _mediaPlayer;

        _mediaPlayer.PositionChanged += (s, e) => PositionChanged?.Invoke(CurrentPositionSeconds);
        _mediaPlayer.Playing += (s, e) => PlaybackStateChanged?.Invoke(true);
        _mediaPlayer.Paused += (s, e) => PlaybackStateChanged?.Invoke(false);
        _mediaPlayer.Stopped += (s, e) => PlaybackStateChanged?.Invoke(false);
        _mediaPlayer.LengthChanged += (s, e) => DurationChanged?.Invoke(DurationSeconds);
        _mediaPlayer.EncounteredError += (s, e) => PlaybackError?.Invoke("VLC Error encountered");
    }

    public Task PlayAsync() { _mediaPlayer.Play(); return Task.CompletedTask; }
    public Task PauseAsync() { _mediaPlayer.Pause(); return Task.CompletedTask; }
    public Task SeekAsync(double positionSeconds) { _mediaPlayer.Time = (long)(positionSeconds * 1000); return Task.CompletedTask; }
    public Task<double> GetPositionAsync() => Task.FromResult(CurrentPositionSeconds);
    public Task<double> GetDurationAsync() => Task.FromResult(DurationSeconds);
    public Task SetVolumeAsync(int volume)
    {
        Volume = volume;
        return Task.CompletedTask;
    }

    public void Play() => _mediaPlayer.Play();
    public void Pause() => _mediaPlayer.Pause();
    public void Seek(double positionSeconds) => _mediaPlayer.Time = (long)(positionSeconds * 1000);
    
    public void SetSource(string sourcePath)
    {
        using var media = new Media(_libVlc, new Uri(sourcePath));
        _mediaPlayer.Media = media;
        _mediaPlayer.Play();
    }

    public void Stop() => _mediaPlayer.Stop();

    public void Dispose()
    {
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
    }
}
