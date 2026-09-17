using Sentrychan.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sentrychan.Core.Services;

public class VideoFileLocator : IVideoFileLocator
{
    private readonly IConfigService _configService;
    private readonly IEpisodeNormalizer _episodeNormalizer;

    public VideoFileLocator(IConfigService configService, IEpisodeNormalizer episodeNormalizer)
    {
        _configService = configService;
        _episodeNormalizer = episodeNormalizer;
    }

    public async Task<string?> FindVideoFileAsync(string seriesTitle, int episodeNumber, CancellationToken ct)
    {
        var downloadPath = await _configService.GetValueAsync("DownloadPath", string.Empty, ct);
        if (string.IsNullOrWhiteSpace(downloadPath) || !Directory.Exists(downloadPath))
            return null;

        var videoExtensions = new[] { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv" };

        // Build candidate search paths:
        // 1. DownloadPath/SeriesTitle/ (series subfolder)
        // 2. DownloadPath/ (flat structure)
        var searchPaths = new List<string>
        {
            Path.Combine(downloadPath, SanitizeFolderName(seriesTitle)),
            downloadPath
        };

        foreach (var searchPath in searchPaths)
        {
            if (!Directory.Exists(searchPath)) continue;

            var files = Directory.GetFiles(searchPath)
                .Where(f => videoExtensions.Contains(
                    Path.GetExtension(f).ToLowerInvariant()))
                .ToList();

            foreach (var file in files)
            {
                var parsed = _episodeNormalizer.ExtractEpisodeNumber(
                    Path.GetFileNameWithoutExtension(file));
                if (parsed == episodeNumber)
                    return file;
            }
        }
        return null;
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c =>
            invalid.Contains(c) ? "_" : c.ToString()));
    }
}
