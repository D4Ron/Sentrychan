using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.Services;

public class SeriesService : ISeriesService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IAnimeApiService _animeApiService;
    private readonly ILogger<SeriesService> _logger;

    public SeriesService(
        IDbContextFactory<AppDbContext> dbFactory,
        IAnimeApiService animeApiService,
        ILogger<SeriesService> logger)
    {
        _dbFactory = dbFactory;
        _animeApiService = animeApiService;
        _logger = logger;
    }

    public async Task<List<Series>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Series
            .OrderBy(s => s.Title)
            .ToListAsync(ct);
    }

    public async Task<Series?> GetByMalIdAsync(int malId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Series
            .FirstOrDefaultAsync(s => s.MalId == malId, ct);
    }

    public async Task<Series> AddAsync(Series series, string? imageUrl = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var exists = await db.Series
            .AnyAsync(s => s.MalId == series.MalId, ct);

        if (exists)
        {
            _logger.LogWarning("Series {MalId} already exists", series.MalId);
            return series;
        }

        // SeasonNumber was never being populated by any caller, leaving every entry at
        // the default 1 — including shows whose title plainly says otherwise. It's the
        // fallback the file pipeline uses when a release name carries no season tag, so
        // derive it from the title here rather than in each of the six add paths.
        if (series.SeasonNumber <= 1)
        {
            var detected = SeasonDetector.DetectSeason(series.Title);
            if (detected > 1)
            {
                series.SeasonNumber = detected;
                _logger.LogInformation("[Series] Detected season {Season} for '{Title}'",
                    detected, series.Title);
            }
        }

        db.Series.Add(series);
        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrEmpty(imageUrl))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var path = await _animeApiService.GetCachedImagePathAsync(imageUrl);
                    if (!string.IsNullOrEmpty(path))
                    {
                        await using var db2 = await _dbFactory.CreateDbContextAsync();
                        var s = await db2.Series.FindAsync(series.Id);
                        if (s != null)
                        {
                            s.PosterPath = path;
                            await db2.SaveChangesAsync();
                            // Update the in-memory instance for the immediate UI response
                            series.PosterPath = path;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Poster download failed for {Title}", series.Title);
                }
            });
        }

        return series;
    }

    public async Task<bool> RemoveAsync(int malId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var series = await db.Series
                .FirstOrDefaultAsync(s => s.MalId == malId, ct);

            if (series == null) return false;

            // WatchPartySession.HostedSeriesId is DeleteBehavior.Restrict — stale
            // sessions from old watch parties block the delete with a FOREIGN KEY
            // error. Remove them first (their participants/messages cascade).
            var staleSessions = await db.WatchPartySessions
                .Where(w => w.HostedSeriesId == series.Id)
                .ToListAsync(ct);
            if (staleSessions.Count > 0)
                db.WatchPartySessions.RemoveRange(staleSessions);

            db.Series.Remove(series);
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove series {MalId}", malId);
            return false;
        }
    }

    public async Task UpdateLastEpisodeAsync(
        int malId, int episodeNumber, CancellationToken ct = default)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var series = await db.Series
                .FirstOrDefaultAsync(s => s.MalId == malId, ct);

            if (series == null) return;

            series.LastEpisodeNumber = episodeNumber;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update episode for {MalId}", malId);
        }
    }

    public async Task AddAliasAsync(
        int malId, string alias, CancellationToken ct = default)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var series = await db.Series
                .FirstOrDefaultAsync(s => s.MalId == malId, ct);

            if (series == null) return;

            var titles = System.Text.Json.JsonSerializer
                .Deserialize<List<string>>(series.AlternativeTitlesJson) ?? [];

            if (!titles.Contains(alias, StringComparer.OrdinalIgnoreCase))
            {
                titles.Add(alias);
                series.AlternativeTitlesJson = System.Text.Json
                    .JsonSerializer.Serialize(titles);
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add alias for {MalId}", malId);
        }
    }

    public async Task UpdatePosterPathAsync(int seriesId, string posterPath, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var series = await db.Series.FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        if (series != null)
        {
            series.PosterPath = posterPath;
            await db.SaveChangesAsync(ct);
        }
    }
}