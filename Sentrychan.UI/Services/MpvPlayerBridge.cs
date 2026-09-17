using Sentrychan.Core.Interfaces;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Sentrychan.UI.Services;

public class MpvPlayerBridge : IPlayerBridgeService, IDisposable
{
    private readonly ILogger<MpvPlayerBridge> _logger;
    private Process? _mpvProcess;
    private NamedPipeClientStream? _pipeClient;
    private StreamWriter? _pipeWriter;
    private CancellationTokenSource? _cts;
    private readonly string _pipeName = "sentrychan-mpv";
    private readonly string _mpvPath;

    public PlayerType PlayerType => PlayerType.External;
    public double CurrentPositionSeconds { get; private set; }
    public double DurationSeconds { get; private set; }
    public bool IsPlaying { get; private set; }
    public int Volume { get; set; } = 100;
    public bool IsMuted { get; set; }

#pragma warning disable CS0067
    public event Action<double>? PositionChanged;
    public event Action<bool>? PlaybackStateChanged;
    public event Action<double>? DurationChanged;
#pragma warning restore CS0067
    public event Action<string>? PlaybackError;

    public MpvPlayerBridge(ILogger<MpvPlayerBridge> logger, string mpvPath = "mpv")
    {
        _logger = logger;
        _mpvPath = mpvPath;
    }

    public void SetSource(string sourcePath)
    {
        Stop();
        
        var startInfo = new ProcessStartInfo
        {
            FileName = _mpvPath,
            Arguments = $"\"{sourcePath}\" --input-ipc-server=\\\\.\\pipe\\{_pipeName} --idle --force-window",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            _mpvProcess = Process.Start(startInfo);
            StartIpc();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start MPV");
            PlaybackError?.Invoke("Failed to start MPV");
        }
    }

    private void StartIpc()
    {
        _cts = new CancellationTokenSource();
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000); // Wait for MPV to start the pipe
                _pipeClient = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut);
                await _pipeClient.ConnectAsync(5000, _cts.Token);
                _pipeWriter = new StreamWriter(_pipeClient) { AutoFlush = true };

                _logger.LogInformation("Connected to MPV IPC");

                // Start reader loop
                using var reader = new StreamReader(_pipeClient);
                while (!_cts.IsCancellationRequested && _pipeClient.IsConnected)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) break;
                    HandleIpcMessage(line);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MPV IPC Error");
            }
        }, _cts.Token);

        // Polling loop for properties if MPV doesn't push them
        Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                await SendCommandAsync("get_property", "time-pos");
                await SendCommandAsync("get_property", "duration");
                await SendCommandAsync("get_property", "pause");
                await Task.Delay(500);
            }
        }, _cts.Token);
    }

    private void HandleIpcMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("event", out var eventProp))
            {
                // Handle events if needed
            }
            else if (doc.RootElement.TryGetProperty("data", out var dataProp))
            {
                // This is a response to get_property
                // We need to know which property it was. 
                // A better implementation would track request IDs.
                // For now, let's just infer from value type or use a simple hack.
            }
        }
        catch { }
    }

    private async Task SendCommandAsync(params object[] args)
    {
        if (_pipeWriter == null) return;
        var cmd = new { command = args };
        var json = JsonSerializer.Serialize(cmd);
        await _pipeWriter.WriteLineAsync(json);
    }

    public Task PlayAsync() => SendCommandAsync("set_property", "pause", false);
    public Task PauseAsync() => SendCommandAsync("set_property", "pause", true);
    public Task SeekAsync(double positionSeconds) => SendCommandAsync("set_property", "time-pos", positionSeconds);
    public Task<double> GetPositionAsync() => Task.FromResult(CurrentPositionSeconds);
    public Task<double> GetDurationAsync() => Task.FromResult(DurationSeconds);
    public Task SetVolumeAsync(int volume)
    {
        Volume = volume;
        return SendCommandAsync("set_property", "volume", volume);
    }

    public void Play() => _ = SendCommandAsync("set_property", "pause", false);
    public void Pause() => _ = SendCommandAsync("set_property", "pause", true);
    public void Seek(double pos) => _ = SendCommandAsync("set_property", "time-pos", pos);
    public void Stop()
    {
        _cts?.Cancel();
        _mpvProcess?.Kill();
        _pipeClient?.Dispose();
        _pipeWriter?.Dispose();
    }

    public void Dispose() => Stop();
}
