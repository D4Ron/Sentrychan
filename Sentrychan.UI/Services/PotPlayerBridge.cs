using Sentrychan.Core.Interfaces;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace Sentrychan.UI.Services;

public class PotPlayerBridge : IPlayerBridgeService, IDisposable
{
    private readonly ILogger<PotPlayerBridge> _logger;
    private readonly string _potPlayerPath;
    private CancellationTokenSource? _cts;
    private Process? _process;

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    private const uint WM_COMMAND = 0x0111;
    private const int POT_PLAY_PAUSE = 10014;
    private const int POT_STOP = 10015;

    public PotPlayerBridge(ILogger<PotPlayerBridge> logger, string potPlayerPath = "PotPlayer64.exe")
    {
        _logger = logger;
        _potPlayerPath = potPlayerPath;
    }

    public void SetSource(string sourcePath)
    {
        Stop();
        
        try
        {
            _process = Process.Start(_potPlayerPath, $"\"{sourcePath}\"");
            StartMonitoring();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start PotPlayer");
            PlaybackError?.Invoke("Failed to start PotPlayer. Check path in Settings.");
        }
    }

    private void StartMonitoring()
    {
        _cts = new CancellationTokenSource();
        Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                UpdateStateFromWindow();
                await Task.Delay(1000, _cts.Token);
            }
        }, _cts.Token);
    }

    private void UpdateStateFromWindow()
    {
        IntPtr hwnd = FindWindow("PotPlayer64", null);
        if (hwnd == IntPtr.Zero) hwnd = FindWindow("PotPlayer", null);
        if (hwnd == IntPtr.Zero) return;

        var sb = new StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        string title = sb.ToString();

        // PotPlayer title format: [00:12:34 / 00:24:00] Filename - PotPlayer
        var match = System.Text.RegularExpressions.Regex.Match(title, @"\[(\d{1,2}:\d{2}:\d{2})\s*/\s*(\d{1,2}:\d{2}:\d{2})\]");
        if (match.Success)
        {
            var current = TimeSpan.Parse(match.Groups[1].Value).TotalSeconds;
            var total = TimeSpan.Parse(match.Groups[2].Value).TotalSeconds;

            if (Math.Abs(CurrentPositionSeconds - current) > 0.5)
            {
                CurrentPositionSeconds = current;
                PositionChanged?.Invoke(current);
            }

            if (Math.Abs(DurationSeconds - total) > 0.5)
            {
                DurationSeconds = total;
                DurationChanged?.Invoke(total);
            }
        }
    }

    private void SendPotCommand(int cmd)
    {
        IntPtr hwnd = FindWindow("PotPlayer64", null);
        if (hwnd == IntPtr.Zero) hwnd = FindWindow("PotPlayer", null);
        if (hwnd != IntPtr.Zero)
        {
            SendMessage(hwnd, WM_COMMAND, (IntPtr)cmd, IntPtr.Zero);
        }
    }

    public Task PlayAsync() 
    {
        if (!IsPlaying) SendPotCommand(POT_PLAY_PAUSE);
        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        if (IsPlaying) SendPotCommand(POT_PLAY_PAUSE);
        return Task.CompletedTask;
    }

    public Task SeekAsync(double positionSeconds)
    {
        var ts = TimeSpan.FromSeconds(positionSeconds);
        var seekArg = $"/seek={ts:hh\\:mm\\:ss}";
        Process.Start(_potPlayerPath, seekArg);
        return Task.CompletedTask;
    }

    public Task<double> GetPositionAsync() => Task.FromResult(CurrentPositionSeconds);
    public Task<double> GetDurationAsync() => Task.FromResult(DurationSeconds);
    public Task SetVolumeAsync(int volume)
    {
        Volume = volume;
        return Task.CompletedTask;
    }

    public void Play() => PlayAsync();
    public void Pause() => PauseAsync();
    public void Seek(double pos) => SeekAsync(pos);
    
    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        SendPotCommand(POT_STOP);
        _process?.Dispose();
        _process = null;
    }

    public void Dispose() => Stop();
}
