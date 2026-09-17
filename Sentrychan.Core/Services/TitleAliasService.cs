using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.Services;

public class TitleAliasService : ITitleAliasService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public TitleAliasService(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<List<TitleAlias>> GetForSeriesAsync(int seriesId, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.TitleAliases
            .Where(a => a.SeriesId == seriesId)
            .ToListAsync(ct);
    }

    public async Task AddAliasAsync(int seriesId, string alias, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(alias)) return;

        using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (await db.TitleAliases.AnyAsync(a => a.SeriesId == seriesId && a.Alias == alias, ct)) return;

        db.TitleAliases.Add(new TitleAlias { SeriesId = seriesId, Alias = alias });
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAliasAsync(int aliasId, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var alias = await db.TitleAliases.FindAsync(new object[] { aliasId }, ct);
        if (alias != null)
        {
            db.TitleAliases.Remove(alias);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<int?> FindSeriesIdByAliasAsync(string alias, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(alias)) return null;

        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entry = await db.TitleAliases
            .FirstOrDefaultAsync(a => a.Alias.ToLower() == alias.ToLower(), ct);
        
        return entry?.SeriesId;
    }
}
