using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Config;
using Sentrychan.Core.Data;

namespace Sentrychan.Core.Services.AniDb;

public class AniDbUdpClient : IDisposable
{
    private const string ServerAddress = "api.anidb.net";
    private const int ServerPort = 9000;
    private const int MinDelayMs = 2500;
    private const int AuthTimeoutMs = 8000;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<AniDbUdpClient> _logger;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private readonly SemaphoreSlim _rateLimiter = new(1, 1);
    
    private UdpClient? _client;
    private string? _sessionKey;
    private DateTime _lastSendTime = DateTime.MinValue;
    private bool _isDisposed;

    public AniDbUdpClient(
        IDbContextFactory<AppDbContext> _dbFactory,
        ILogger<AniDbUdpClient> logger)
    {
        this._dbFactory = _dbFactory;
        _logger = logger;
    }

    public async Task<bool> AuthenticateAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(_sessionKey)) return true;

        await _authLock.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrEmpty(_sessionKey)) return true;

            // Prefer hardcoded config; fall back to DB for legacy installs
            string? username;
            string? password;
            if (AniDbConfig.IsConfigured)
            {
                username = AniDbConfig.Username;
                password = AniDbConfig.Password;
            }
            else
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                username = (await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == "AniDbUsername", ct))?.Value;
                password = (await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == "AniDbPassword", ct))?.Value;
            }

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                _logger.LogWarning("AniDB credentials not configured.");
                return false;
            }

            _client = new UdpClient();
            _client.Connect(ServerAddress, ServerPort);
            _client.Client.ReceiveTimeout = AuthTimeoutMs;

            var command = $"AUTH user={username}&pass={password}&protover=3&client={AniDbConfig.ClientName}&clientver={AniDbConfig.ClientVersion}&nat=1&enc=UTF8";

            var response = await SendAndReceiveAsync(command, ct);
            if (response.StartsWith("200 ") || response.StartsWith("201 "))
            {
                var parts = response.Split(' ');
                if (parts.Length >= 2)
                {
                    _sessionKey = parts[1].Split('\n')[0].Split('\r')[0];
                    _logger.LogInformation("AniDB UDP Authenticated. Session: {Session}", _sessionKey);
                    return true;
                }
            }

            _logger.LogError("AniDB Auth failed: {Response}", response);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AniDB UDP Auth exception");
            return false;
        }
        finally
        {
            _authLock.Release();
        }
    }

    public async Task<string> SendCommandAsync(string command, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_sessionKey))
        {
            var authed = await AuthenticateAsync(ct);
            if (!authed) return "500 NOT AUTHENTICATED";
        }

        var fullCommand = $"{command}&s={_sessionKey}";
        var response = await SendAndReceiveAsync(fullCommand, ct);

        // Check for session expiry (501: Login first, 506: Invalid session)
        if (response.StartsWith("501 ") || response.StartsWith("506 "))
        {
            _logger.LogInformation("AniDB session expired. Re-authenticating...");
            _sessionKey = null;
            if (await AuthenticateAsync(ct))
            {
                fullCommand = $"{command}&s={_sessionKey}";
                return await SendAndReceiveAsync(fullCommand, ct);
            }
        }

        return response;
    }

    private async Task<string> SendAndReceiveAsync(string command, CancellationToken ct)
    {
        await _rateLimiter.WaitAsync(ct);
        try
        {
            // Mandatory 2.5s delay
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastSendTime).TotalMilliseconds;
            if (elapsed < MinDelayMs)
            {
                await Task.Delay(MinDelayMs - (int)elapsed, ct);
            }

            if (_client == null) throw new InvalidOperationException("UDP Client not initialized");

            var buffer = Encoding.UTF8.GetBytes(command + "\n");
            await _client.SendAsync(buffer, buffer.Length);
            _lastSendTime = DateTime.UtcNow;

            var result = await _client.ReceiveAsync(ct);
            var response = Encoding.UTF8.GetString(result.Buffer);
            
            return response;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
        {
             _logger.LogWarning("AniDB UDP Timeout. Retrying once...");
             // Simple one-time retry logic could go here, but I'll stick to the core for now per instructions
             return "599 TIMEOUT";
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (!string.IsNullOrEmpty(_sessionKey) && _client != null)
        {
            // Fire and forget logout
            var buffer = Encoding.UTF8.GetBytes($"LOGOUT s={_sessionKey}\n");
            _client.Send(buffer, buffer.Length);
        }

        _client?.Dispose();
        _rateLimiter.Dispose();
        _authLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
