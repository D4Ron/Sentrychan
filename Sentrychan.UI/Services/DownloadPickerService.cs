using Microsoft.EntityFrameworkCore;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Sentrychan.UI.Views.Dialogs;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace Sentrychan.UI.Services;

public class DownloadPickerService : IDownloadPickerService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IDownloadBackendRouter _router;

    public DownloadPickerService(
        IDbContextFactory<AppDbContext> dbFactory,
        IDownloadBackendRouter router)
    {
        _dbFactory = dbFactory;
        _router    = router;
    }

    public async Task<IDownloadBackend?> PickBackendAsync(
        DownloadContext context,
        CancellationToken ct = default)
    {
        // Check for a remembered choice
        var rememberedType = await GetRememberedChoiceAsync(context, ct);
        if (rememberedType != null)
        {
            var remembered = _router.All.FirstOrDefault(
                b => b.BackendType == rememberedType);
            if (remembered != null &&
                await remembered.IsAvailableAsync(ct))
                return remembered;

            // Remembered backend is no longer available — clear and show picker
            await ClearRememberedChoiceAsync(context, ct);
        }

        // Show the picker dialog on the UI thread
        IDownloadBackend? chosen = null;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes
                    .IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (mainWindow == null) return;

            var dialog = new DownloadPickerDialog(_router, context, _dbFactory);
            chosen = await dialog.ShowDialog<IDownloadBackend?>(mainWindow);
        });

        return chosen;
    }

    public async Task ClearRememberedChoiceAsync(
        DownloadContext context,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var key    = GetConfigKey(context);
        var config = await db.AppConfigs
            .FirstOrDefaultAsync(c => c.Key == key, ct);
        if (config != null)
        {
            db.AppConfigs.Remove(config);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<string?> GetRememberedChoiceAsync(
        DownloadContext context,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var key    = GetConfigKey(context);
        var config = await db.AppConfigs
            .FirstOrDefaultAsync(c => c.Key == key, ct);
        return string.IsNullOrWhiteSpace(config?.Value) ? null : config.Value;
    }

    private static string GetConfigKey(DownloadContext context) => context switch
    {
        DownloadContext.Hub      => "RememberedBackend_Hub",
        DownloadContext.RssFeed  => "RememberedBackend_RSS",
        DownloadContext.FillGaps => "RememberedBackend_FillGaps",
        _                        => "RememberedBackend_Hub"
    };
}
