using Sentrychan.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Sentrychan.Core.Services;

public class VideoFileLocator : IVideoFileLocator
{
    private readonly IConfigService _configService;
    private readonly IEpisodeNormalizer _episodeNormalizer;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".webm" };

    // Must match FileMovementPipeline's folder naming, or we'd look where it never files.
    private static readonly Regex InvalidFolderChars = new(@"[<>:""/\\|?*\x00-\x1F]", RegexOptions.Compiled);

    public VideoFileLocator(IConfigService configService, IEpisodeNormalizer episodeNormalizer)
    {
        _configService = configService;
        _episodeNormalizer = episodeNormalizer;
    }

    /// <summary>
    /// Finds an episode's file where the pipeline files it — Library/&lt;Title&gt;/Season N —
    /// and, failing that, still sitting in the download folder.
    ///
    /// This used to look only in the download folder and check only the episode number, so
    /// it missed everything already in the library and accepted any show's episode N: with
    /// "Youjo Senki S2 - 12" mid-download, Mushoku Tensei's episode 12 counted as present.
    /// </summary>
    public async Task<string?> FindVideoFileAsync(string seriesTitle, int episodeNumber, CancellationToken ct)
    {
        var baseTitle = SeasonDetector.ExtractBaseTitle(seriesTitle);
        var season    = SeasonSearch.EffectiveSeason(seriesTitle, 1);

        var libraryPath = await _configService.GetValueAsync("LibraryPath", string.Empty, ct);
        if (!string.IsNullOrWhiteSpace(libraryPath) && Directory.Exists(libraryPath))
        {
            var seriesDir = Path.Combine(libraryPath, InvalidFolderChars.Replace(baseTitle, string.Empty).Trim());

            // The season folder is already scoped to this show and season: episode number is enough.
            var hit = FirstEpisode(Path.Combine(seriesDir, $"Season {season}"), episodeNumber, _ => true);
            if (hit != null) return hit;

            // Files dropped straight into the show's folder carry no season folder to trust.
            hit = FirstEpisode(seriesDir, episodeNumber, name => SeasonSearch.MatchesSeason(name, season));
            if (hit != null) return hit;
        }

        var downloadPath = await _configService.GetValueAsync("DownloadPath", string.Empty, ct);
        if (!string.IsNullOrWhiteSpace(downloadPath) && Directory.Exists(downloadPath))
        {
            // A shared folder full of other shows: the name must be this show and this season.
            bool IsThisShow(string name) =>
                _episodeNormalizer.MatchesTitle(name, baseTitle) && SeasonSearch.MatchesSeason(name, season);

            return FirstEpisode(Path.Combine(downloadPath, InvalidFolderChars.Replace(seriesTitle, "_")), episodeNumber, IsThisShow)
                ?? FirstEpisode(downloadPath, episodeNumber, IsThisShow);
        }

        return null;
    }

    private string? FirstEpisode(string dir, int episodeNumber, Func<string, bool> accept)
    {
        if (!Directory.Exists(dir)) return null;

        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (!VideoExtensions.Contains(Path.GetExtension(file))) continue;
            var name = Path.GetFileNameWithoutExtension(file);
            if (_episodeNormalizer.ExtractEpisodeNumber(name) == episodeNumber && accept(name))
                return file;
        }
        return null;
    }
}
