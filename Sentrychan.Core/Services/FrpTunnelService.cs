using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.Services;

public class FrpTunnelService : ITunnelService, IAsyncDisposable
{
    private readonly ILogger<FrpTunnelService> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private Process? _frpProcess;
    private string? _publicAddress;
    private bool _isActive;

    public bool IsActive => _isActive;
    public string? PublicAddress => _publicAddress;

    public FrpTunnelService(ILogger<FrpTunnelService> logger, IDbContextFactory<AppDbContext> _dbFactory)
    {
        this._logger = logger;
        this._dbFactory = _dbFactory;
    }

    public async Task<TunnelResult> StartTunnelAsync(int localPort, CancellationToken ct)
    {
        if (_isActive) return new TunnelResult(true, _publicAddress);

        try
        {
            var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sentrychan");
            var frpcExePath = Path.Combine(AppContext.BaseDirectory, "Assets", "frpc.exe");
            var configPath = Path.Combine(appDataPath, "frpc.toml");

            if (!File.Exists(frpcExePath))
            {
                throw new FileNotFoundException($"frpc.exe not found at {frpcExePath}. Reinstall the application.");
            }

            string serverAddr = await GetFrpServerAddressAsync(ct);
            if (string.IsNullOrEmpty(serverAddr))
            {
                throw new InvalidOperationException("FRP Server Address is not configured in Settings.");
            }

            string sessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
            string config = $@"
serverAddr = ""{serverAddr}""
serverPort = 7000

[[proxies]]
name = ""sentrychan-{sessionId}""
type = ""tcp""
localIP = ""127.0.0.1""
localPort = {localPort}
remotePort = 0
";
            await File.WriteAllTextAsync(configPath, config, ct);

            _frpProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = frpcExePath,
                    Arguments = $"--config \"{configPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            var tcs = new TaskCompletionSource<TunnelResult>();
            
            _frpProcess.OutputDataReceived += (s, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                _logger.LogInformation($"FRP: {e.Data}");

                // Look for "start proxy success" and extract port
                // Example: [I] [proxy.go:94] [sentrychan-abc12345] start proxy success, remote addr [1.2.3.4:54321]
                var match = Regex.Match(e.Data, @"start proxy success, remote addr \[(.*?)\]");
                if (match.Success)
                {
                    _publicAddress = match.Groups[1].Value;
                    _isActive = true;
                    tcs.TrySetResult(new TunnelResult(true, _publicAddress));
                }
            };

            _frpProcess.Start();
            _frpProcess.BeginOutputReadLine();
            
            _frpProcess.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    _logger.LogWarning("FRP stderr: {Data}", e.Data);
            };
            _frpProcess.BeginErrorReadLine();

            // Wait for success or exit
            var timeoutTask = Task.Delay(5000, ct);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                await StopTunnelAsync();
                return new TunnelResult(false, ErrorMessage: "FRP tunnel timed out starting.");
            }

            return await tcs.Task;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start FRP tunnel");
            return new TunnelResult(false, ErrorMessage: ex.Message);
        }
    }

    public async Task StopTunnelAsync()
    {
        if (_frpProcess != null && !_frpProcess.HasExited)
        {
            _frpProcess.Kill();
            await _frpProcess.WaitForExitAsync();
        }
        _frpProcess?.Dispose();
        _frpProcess = null;
        _isActive = false;
        _publicAddress = null;
    }

    private async Task<string> GetFrpServerAddressAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var config = await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == "FrpServerAddress", ct);
        return config?.Value ?? string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        await StopTunnelAsync();
    }

    public void Dispose()
    {
        StopTunnelAsync().GetAwaiter().GetResult();
    }
}
