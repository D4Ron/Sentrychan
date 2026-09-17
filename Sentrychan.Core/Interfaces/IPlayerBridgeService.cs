using System;

namespace Sentrychan.Core.Interfaces;

public enum PlayerType
{
    Internal,
    External
}

public interface IPlayerBridgeService
{
    PlayerType PlayerType { get; }
    double CurrentPositionSeconds { get; }
    double DurationSeconds { get; }
    bool IsPlaying { get; }
    int Volume { get; set; }
    bool IsMuted { get; set; }

    event Action<double>? PositionChanged;
    event Action<bool>? PlaybackStateChanged;
    event Action<double>? DurationChanged;
    event Action<string>? PlaybackError;

    Task PlayAsync();
    Task PauseAsync();
    Task SeekAsync(double positionSeconds);
    Task<double> GetPositionAsync();
    Task<double> GetDurationAsync();
    Task SetVolumeAsync(int volume);

    void Play();
    void Pause();
    void Seek(double positionSeconds);
    void SetSource(string sourcePath);
    void Stop();
}
