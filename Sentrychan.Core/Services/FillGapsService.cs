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
    private readonly INyaaSearchService _nyaaSearch;
    private readonly ILogger<FillGapsService> _logger;

    public FillGapsService(
        IVideoFileLocator fileLocator,
        INyaaSearchService nyaaSearch,
        ILogger<FillGapsService> logger)
    {
        _fileLocator = fileLocator;
        _nyaaSearch = nyaaSearch;
        _logger = logger;
    }

    public async Task<List<FillGapResult>> FindMissingEpisodesAsync(Series series, CancellationToken ct = default)
    {
        var missing = new List<FillGapResult>();

        int total = Math.Max(series.LastEpisodeNumber, series.TotalEpisodes ?? 0);
        if (total < 1) return missing;

        _logger.LogInformation("Scanning for gaps in {Title} up to episode {Max}", series.Title, total);

        // Search without the season baked into the query (so every season's releases
        // come back), then keep only releases that match THIS series' season.
        var searchTitle = SeasonSearch.StripSeason(series.Title);
        var season      = SeasonSearch.EffectiveSeason(series.Title, series.SeasonNumber);

        // Scan sequentially
        for (int i = 1; i <= total; i++)
        {
            if (ct.IsCancellationRequested) break;

            var existingPath = await _fileLocator.FindVideoFileAsync(series.Title, i, ct);
            if (string.IsNullOrEmpty(existingPath))
            {
                _logger.LogInformation("Gap found: {Title} Ep {Ep}", series.Title, i);

                var results = await _nyaaSearch.FindEpisodeAsync(searchTitle, i, null, ct);
                var best = results.FirstOrDefault(r =>
                    SeasonSearch.MatchesSeason(r.Title, season));

                missing.Add(new FillGapResult
                {
                    EpisodeNumber = i,
                    BestMatch = best,
                    IsSelected = best != null
                });
            }
        }

        return missing;
    }

    /// <summary>
    /// Searches Nyaa for a batch torrent for this series.
    /// Returns the best candidate (most seeders from preferred groups) or null.
    /// </summary>
    public async Task<NyaaResult?> SearchBatchTorrentAsync(Series series, string quality = "1080p", CancellationToken ct = default)
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
            var results = await _nyaaSearch.SearchAsync(q, ct: ct);
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
