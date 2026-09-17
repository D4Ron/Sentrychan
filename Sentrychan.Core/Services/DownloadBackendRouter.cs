using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services.Backends;

namespace Sentrychan.Core.Services;

public class DownloadBackendRouter : IDownloadBackendRouter
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<DownloadBackendRouter> _logger;
    private readonly List<IDownloadBackend> _all;
    private IDownloadBackend _active;

    public IDownloadBackend Active => _active;
    public string ActiveBackendType => Active.BackendType;
    public IReadOnlyList<IDownloadBackend> All => _all.AsReadOnly();

    public DownloadBackendRouter(
        FdmBackend fdm,
        QBittorrentBackend qbit,
        MonoTorrentBackend mono,
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<DownloadBackendRouter> logger)
    {
        _dbFactory = dbFactory;
        _logger    = logger;
        _all       = [fdm, qbit, mono];
        _active    = mono;  // built-in client is the default until InitializeAsync runs
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // Load qBittorrent configuration and apply it before selecting backend
            var qbit = (QBittorrentBackend)_all.First(b => b is QBittorrentBackend);
            var qbitUrl  = (await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == "QBitUrl", ct))?.Value
                           ?? "http://localhost:8080";
            var qbitUser = (await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == "QBitUsername", ct))?.Value
                           ?? "admin";
            // Stored DPAPI-encrypted; Unprotect passes legacy plaintext through unchanged.
            var qbitPass = SecretProtector.Unprotect(
                (await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == "QBitPassword", ct))?.Value);
            if (string.IsNullOrEmpty(qbitPass)) qbitPass = "adminadmin";
            qbit.Configure(qbitUrl, qbitUser, qbitPass);

            var backendName = (await db.AppConfigs
                .FirstOrDefaultAsync(c => c.Key == "SelectedDownloadBackend", ct))?.Value
                ?? "MonoTorrent";

            var match = _all.FirstOrDefault(b => b.BackendType == backendName);
            if (match != null) _active = match;

            _logger.LogInformation("[BackendRouter] Active backend: {Name}", _active.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BackendRouter] InitializeAsync failed, using FDM default");
        }
    }

    public async Task SetActiveAsync(string backend, CancellationToken ct = default)
    {
        var match = _all.FirstOrDefault(b => b.BackendType == backend);
        if (match == null)
        {
            _logger.LogWarning("[BackendRouter] Unknown backend: {Backend}", backend);
            return;
        }
        _active = match;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var entry = await db.AppConfigs
                .FirstOrDefaultAsync(c => c.Key == "SelectedDownloadBackend", ct);
            if (entry != null) entry.Value = backend;
            else db.AppConfigs.Add(new Models.AppConfig
                { Key = "SelectedDownloadBackend", Value = backend });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BackendRouter] Failed to persist backend selection");
        }
    }
}
