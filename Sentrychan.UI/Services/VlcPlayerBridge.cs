using Sentrychan.Core.Interfaces;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Sentrychan.UI.Services;

public class VlcPlayerBridge : IPlayerBridgeService, IDisposable
{
    private readonly ILogger<VlcPlayerBridge> _logger;
    private Process? _vlcProcess;
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private readonly string _vlcPath;
    private readonly int _rcPort = 4212;

    public PlayerType PlayerType => PlayerType.External;
    public double CurrentPositionSeconds { get; private set; }
    public double DurationSeconds { get; private set; }
    public bool IsPlaying { get; private set; }
    public int Volume { get; set; } = 100;
    public bool IsMuted { get; set; }

    public event Action<double>? PositionChanged;
    public event Action<bool>? PlaybackStateChanged;
    public event Action<double>? DurationChanged;
    public event Action<string>? PlaybackError;

    public VlcPlayerBridge(ILogger<VlcPlayerBridge> logger, string vlcPath = "vlc")
    {
        _logger = logger;
        _vlcPath = vlcPath;
    }

    public void SetSource(string sourcePath)
    {
        Stop();
        
        var startInfo = new ProcessStartInfo
        {
            FileName = _vlcPath,
            Arguments = $"\"{sourcePath}\" --extraintf rc --rc-host 127.0.0.1:{_rcPort} --rc-quiet",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            _vlcProcess = Process.Start(startInfo);
            StartControlLoop();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start VLC");
            PlaybackError?.Invoke("Failed to start VLC. Check path in Settings.");
        }
    }

    private void StartControlLoop()
    {
        _cts = new CancellationTokenSource();
        Task.Run(async () =>
        {
            try
            {
                // Wait for VLC to bind port
                await Task.Delay(2000);
                
                int retries = 5;
                while (retries > 0)
                {
                    try
                    {
                        _tcpClient = new TcpClient();
                        await _tcpClient.ConnectAsync("127.0.0.1", _rcPort, _cts.Token);
                        _stream = _tcpClient.GetStream();
                        _logger.LogInformation("Connected to VLC RC interface");
                        break;
                    }
                    catch
                    {
                        retries--;
                        await Task.Delay(1000);
                    }
                }

                if (_stream == null)
                {
                    PlaybackError?.Invoke("Could not connect to VLC control interface.");
                    return;
                }

                using var reader = new StreamReader(_stream, Encoding.UTF8);
                
                // Polling loop
                while (!_cts.IsCancellationRequested && _vlcProcess != null && !_vlcProcess.HasExited)
                {
                    await SendRawCommandAsync("get_time");
                    var timeLine = await reader.ReadLineAsync();
                    if (double.TryParse(timeLine, out var time))
                    {
                        if (Math.Abs(CurrentPositionSeconds - time) > 0.1)
                        {
                            CurrentPositionSeconds = time;
                            PositionChanged?.Invoke(time);
                        }
                    }

                    await SendRawCommandAsync("get_length");
                    var lengthLine = await reader.ReadLineAsync();
                    if (double.TryParse(lengthLine, out var length))
                    {
                        if (Math.Abs(DurationSeconds - length) > 0.1)
                        {
                            DurationSeconds = length;
                            DurationChanged?.Invoke(length);
                        }
                    }

                    await SendRawCommandAsync("is_playing");
                    var playingLine = await reader.ReadLineAsync();
                    bool playing = playingLine?.Trim() == "1";
                    if (IsPlaying != playing)
                    {
                        IsPlaying = playing;
                        PlaybackStateChanged?.Invoke(playing);
                    }

                    await Task.Delay(500, _cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in VLC control loop");
            }
        }, _cts.Token);
    }

    private async Task SendRawCommandAsync(string cmd)
    {
        if (_stream == null) return;
        var data = Encoding.UTF8.GetBytes(cmd + "\n");
        await _stream.WriteAsync(data, 0, data.Length);
        await _stream.FlushAsync();
    }

    public Task PlayAsync() => SendRawCommandAsync("play");
    public Task PauseAsync() => SendRawCommandAsync("pause");
    public Task SeekAsync(double positionSeconds) => SendRawCommandAsync($"seek {(int)positionSeconds}");
    public Task<double> GetPositionAsync() => Task.FromResult(CurrentPositionSeconds);
    public Task<double> GetDurationAsync() => Task.FromResult(DurationSeconds);
    public Task SetVolumeAsync(int volume)
    {
        Volume = volume;
        return SendRawCommandAsync($"volume {(int)(volume * 2.56)}");
    }

    public void Play() => _ = SendRawCommandAsync("play");
    public void Pause() => _ = SendRawCommandAsync("pause");
    public void Seek(double pos) => _ = SendRawCommandAsync($"seek {(int)pos}");
    
    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        try
        {
            if (_vlcProcess != null && !_vlcProcess.HasExited)
            {
                _vlcProcess.Kill();
            }
        }
        catch { }
        
        _vlcProcess?.Dispose();
        _vlcProcess = null;

        _stream?.Dispose();
        _stream = null;
        _tcpClient?.Dispose();
        _tcpClient = null;
    }

    public void Dispose() => Stop();
}
