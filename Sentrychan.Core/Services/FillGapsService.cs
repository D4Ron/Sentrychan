using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sentrychan.Core.Services;

public class FillGapsService : IFillGapsService
{
    private readonly IVideoFileLocator _fileLocator;
    private readonly IReleaseProviders _releases;
    private readonly ISecretModeService _secretMode;
    private readonly IConfigService _config;
    private readonly ILogger<FillGapsService> _logger;
    private readonly Microsoft.EntityFrameworkCore.IDbContextFactory<Data.AppDbContext>? _dbFactory;

    public FillGapsService(
        IVideoFileLocator fileLocator,
        IReleaseProviders releases,
        ISecretModeService secretMode,
        IConfigService config,
        ILogger<FillGapsService> logger,
        Microsoft.EntityFrameworkCore.IDbContextFactory<Data.AppDbContext>? dbFactory = null)
    {
        _dbFactory = dbFactory;
        _fileLocator = fileLocator;
        _releases = releases;
        _secretMode = secretMode;
        _config = config;
        _logger = logger;
    }

    public async Task<List<FillGapResult>> FindMissingEpisodesAsync(Series series, CancellationToken ct = default)
    {
        var missing = new List<FillGapResult>();

        int total = Math.Max(series.LastEpisodeNumber, series.TotalEpisodes ?? 0);
        if (total < 1) return missing;

        _logger.LogInformation("Scanning for gaps in {Title} up to episode {Max}", series.Title, total);

        // Query on the season-stripped title with the season passed alongside; the provider
        // phrases it the way releases are named and results come back season-filtered.
        var searchTitle = SeasonSearch.StripSeason(series.Title);
        var season      = SeasonSearch.EffectiveSeason(series.Title, series.SeasonNumber);

        var quality      = await _config.GetValueAsync("QualityPreference", "1080p", ct);
        var downloadPath = await _config.GetValueAsync("DownloadPath", string.Empty, ct);
        var cachedTorrents = string.IsNullOrWhiteSpace(downloadPath)
            ? new Dictionary<string, string>()
            : await VideoIntegrity.CachedTorrentsByFileNameAsync(downloadPath, ct);

        // Scan sequentially
        for (int i = 1; i <= total; i++)
        {
            if (ct.IsCancellationRequested) break;

            var existingPath = await _fileLocator.FindVideoFileAsync(series.Title, i, ct);

            // Present but half-written counts as a gap too — it was filed before it finished.
            string? damaged = null;
            if (!string.IsNullOrEmpty(existingPath))
            {
                var path = existingPath;
                if (!await Task.Run(() => VideoIntegrity.LooksIncomplete(path, ct), ct)) continue;
                damaged = path;
                _logger.LogWarning("Damaged episode: {Title} Ep {Ep} — {File}", series.Title, i, Path.GetFileName(path));
            }
            else
            {
                _logger.LogInformation("Gap found: {Title} Ep {Ep}", series.Title, i);
            }

            // Cached torrents are indexed by the names inside them — the name the file downloaded as,
            // which a renamed library file only has on record.
            string? repairTorrent = null;
            if (damaged != null)
            {
                var original = Path.GetFileName(damaged);
                if (_dbFactory != null)
                {
                    await using var db = await _dbFactory.CreateDbContextAsync(ct);
                    original = await Library.TidyRecords.OriginalNameAsync(db, damaged, ct);
                }
                repairTorrent = cachedTorrents.TryGetValue(original, out var t) ? t : null;
            }

            // In-place repair needs no search: it re-fetches the very torrent the file came from.
            ReleaseResult? best = null;
            if (repairTorrent == null)
            {
                var results = await _releases.FindEpisodeAsync(new EpisodeQuery(searchTitle, i) { Season = season }, ct);
                // A group's 720p can outrank its 1080p on seeders alone; honour the setting first.
                best = results.FirstOrDefault(r => string.Equals(r.Resolution, quality, StringComparison.OrdinalIgnoreCase))
                    ?? results.FirstOrDefault();
            }

            var row = new FillGapResult
            {
                EpisodeNumber     = i,
                BestMatch         = best,
                DamagedPath       = damaged,
                RepairTorrentPath = repairTorrent,
            };
            row.IsSelected = row.CanDownload;
            missing.Add(row);
        }

        return missing;
    }

    /// <summary>
    /// Searches the loaded release providers for a batch torrent for this series.
    /// Returns the best candidate (most seeders) or null — always null with no provider loaded.
    /// </summary>
    public async Task<ReleaseResult?> SearchBatchTorrentAsync(Series series, string quality = "1080p", CancellationToken ct = default)
    {
        // Query on the season-stripped title so we see every season's batches, then
        // filter to the requested season — otherwise a higher-seeded Season 1 batch
        // wins even when the user asked for a later season.
        var baseTitle = SeasonSearch.StripSeason(series.Title);
        var season    = SeasonSearch.EffectiveSeason(series.Title, series.SeasonNumber);

        var queries = new[]
        {
            $"{baseTitle} batch {quality}",
            $"{baseTitle} complete {quality}",
            $"{baseTitle} batch",
        };

        foreach (var q in queries)
        {
            var results = await _releases.SearchAsync(q, secretMode: _secretMode.IsSecretModeActive, ct: ct);
            var batches = results
                .Where(r => r.IsBatch || r.Title.Contains("batch", StringComparison.OrdinalIgnoreCase)
                                      || r.Title.Contains("complete", StringComparison.OrdinalIgnoreCase))
                .Where(r => SeasonSearch.MatchesSeason(r.Title, season))
                .OrderByDescending(r => r.Seeders)
                .ToList();

            if (batches.Count > 0)
            {
                _logger.LogInformation("[FillGaps] Found batch for {Title} S{Season}: {Batch}",
                    series.Title, season, batches[0].Title);
                return batches[0];
            }
        }
        return null;
    }
}
