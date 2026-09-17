using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.Services;

public class ConfigService : IConfigService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public ConfigService(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<T> GetValueAsync<T>(string key, T defaultValue, CancellationToken ct = default)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var entry = await db.AppConfigs
                .FirstOrDefaultAsync(c => c.Key == key, ct);

            if (entry == null) return defaultValue;
            return (T)Convert.ChangeType(entry.Value, typeof(T), CultureInfo.InvariantCulture);
        }
        catch
        {
            return defaultValue;
        }
    }

    public async Task SetValueAsync<T>(string key, T value, CancellationToken ct = default)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var entry = await db.AppConfigs.FirstOrDefaultAsync(c => c.Key == key, ct);
            var str = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            if (entry == null) db.AppConfigs.Add(new AppConfig { Key = key, Value = str });
            else entry.Value = str;
            await db.SaveChangesAsync(ct);
        }
        catch { /* best-effort; a failed persist just means the setting doesn't stick */ }
    }
}
