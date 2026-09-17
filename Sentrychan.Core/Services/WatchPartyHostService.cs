using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.Hubs;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Sentrychan.Core.Services;

public class WatchPartyHostService : IWatchPartyHostService, IAsyncDisposable
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<WatchPartyHostService> _logger;
    private IHost? _host;
    private WatchPartySession? _currentSession;

    public bool IsHosting => _host != null;
    public string? CurrentSessionId => _currentSession?.Id;
    public int Port { get; private set; } = 7742;

    public WatchPartyHostService(
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<WatchPartyHostService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<bool> StartPartyAsync(string name, string password, int? seriesId, string seriesTitle, int episodeNumber, CancellationToken ct = default)
    {
        if (IsHosting) return false;

        var sessionCreated = await CreateSessionAsync(name, password, seriesId, seriesTitle, episodeNumber, ct);
        if (!sessionCreated) return false;

        var serverStarted = await StartServerAsync(ct);
        if (!serverStarted)
        {
            await StopPartyAsync(ct);
            return false;
        }

        return true;
    }

    public async Task<bool> CreateSessionAsync(string name, string password, int? seriesId, string seriesTitle, int episodeNumber, CancellationToken ct = default)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var portConfig = await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == "WatchPartyPort", ct);
            if (portConfig != null && int.TryParse(portConfig.Value, out var port))
            {
                Port = port;
            }

            var passwordHash = string.IsNullOrEmpty(password) 
                ? string.Empty 
                : BCrypt.Net.BCrypt.EnhancedHashPassword(password);

            _currentSession = new WatchPartySession
            {
                Name = name,
                PasswordHash = passwordHash,
                HostedSeriesId = seriesId,
                HostedSeriesTitle = seriesTitle,
                HostedEpisodeNumber = episodeNumber
            };

            db.WatchPartySessions.Add(_currentSession);
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create watch party session");
            return false;
        }
    }

    public async Task<bool> StartServerAsync(CancellationToken ct = default)
    {
        try
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseUrls($"http://0.0.0.0:{Port}");
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddSignalR();
                        services.AddSingleton(_dbFactory);
                    });
                    webBuilder.Configure(app =>
                    {
                        app.UseRouting();
                        app.Use(async (context, next) =>
                        {
                            var reqPassword = context.Request.Query["password"].ToString();
                            if (_currentSession != null && !string.IsNullOrEmpty(_currentSession.PasswordHash))
                            {
                                if (string.IsNullOrEmpty(reqPassword) || 
                                    !BCrypt.Net.BCrypt.EnhancedVerify(reqPassword, _currentSession.PasswordHash))
                                {
                                    context.Response.StatusCode = 401;
                                    await context.Response.WriteAsync("Unauthorized");
                                    return;
                                }
                            }
                            await next();
                        });
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapHub<WatchPartyHub>("/watchparty");
                        });
                    });
                })
                .Build();

            Sentrychan.Core.Hubs.WatchPartyHub.ResetState();
            await _host.StartAsync(ct);
            _logger.LogInformation("Watch Party host started on port {Port}", Port);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start watch party host server");
            return false;
        }
    }

    public async Task StopPartyAsync(CancellationToken ct = default)
    {
        if (_host != null)
        {
            try
            {
                await _host.StopAsync(ct);
                _host.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping watch party host");
            }
            finally
            {
                _host = null;
            }
        }

        if (_currentSession != null)
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var session = await db.WatchPartySessions.FindAsync(new object[] { _currentSession.Id }, ct);
                if (session != null)
                {
                    session.EndedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error removing watch party session from DB");
            }
            finally
            {
                _currentSession = null;
            }
        }
    }

    public async Task BroadcastPlaybackCommandAsync(string command, double positionSeconds, CancellationToken ct = default)
    {
        if (_host != null)
        {
            var hubContext = _host.Services.GetService<IHubContext<WatchPartyHub>>();
            if (hubContext != null)
            {
                await hubContext.Clients.All.SendAsync("PlaybackCommandReceived", command, positionSeconds, cancellationToken: ct);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopPartyAsync();
    }
}
