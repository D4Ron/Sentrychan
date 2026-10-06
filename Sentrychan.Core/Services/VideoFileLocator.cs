using Microsoft.EntityFrameworkCore;
using Sentrychan.Core.Data;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Library;
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
    private readonly IDbContextFactory<AppDbContext>? _dbFactory;
    private readonly ITitleResolverService? _resolver;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".webm" };

    private static readonly Regex InvalidFolderChars = new(@"[<>:""/\\|?*\x00-\x1F]", RegexOptions.Compiled);
    private static readonly Regex SeasonFolder = new(@"^Season\s*0*(\d{1,2})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public VideoFileLocator(IConfigService configService, IEpisodeNormalizer episodeNormalizer,
        IDbContextFactory<AppDbContext>? dbFactory = null, ITitleResolverService? resolver = null)
    {
        _configService = configService;
        _episodeNormalizer = episodeNormalizer;
        _dbFactory = dbFactory;
        _resolver = resolver;
    }

    /// <summary>
    /// The other ways a file of this episode may be named: the numbers groups counting straight
    /// through give it ("Sono Bisque Doll - 18" for season 2's episode 6), and a matcher that
    /// places any name on this series and episode. Empty when the series isn't in the library.
    /// </summary>
    private async Task<(HashSet<int> Numbers, Func<string, bool>? Placed)> AlternativesAsync(string seriesTitle, int episode, CancellationToken ct)
    {
        var numbers = new HashSet<int> { episode };
        if (_dbFactory == null || _resolver is not { IsReady: true } resolver) return (numbers, null);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var series = await db.Series.AsNoTracking().FirstOrDefaultAsync(s => s.Title == seriesTitle, ct);
            if (series is not { MalId: > 0 }) return (numbers, null);
            foreach (var (_, n) in ReleaseMatcher.AbsoluteForms(resolver, series, episode)) numbers.Add(n);
            return (numbers, name => ReleaseMatcher.Match(resolver, name, series) is (ReleaseVerdict.Yes, var e) && e == episode);
        }
        catch { return (numbers, null); }
    }

    /// <summary>
    /// Finds an episode's file in the library — under any show folder that names this show
    /// ("Show", "Show (2023)", a release-tagged folder), in its season folder ("Season 2" or the
    /// naming template's "Season 02") or anywhere else in it that names the season — and, failing
    /// that, still sitting in the download folder.
    ///
    /// A renamed file answers to its current name or to the name it was downloaded as: a tidy
    /// that turned absolute episode 13 into "S02E01" must not make episode 13 look missing.
    ///
    /// This used to look only in the download folder and check only the episode number, so
    /// it missed everything already in the library and accepted any show's episode N: with
    /// "Youjo Senki S2 - 12" mid-download, Mushoku Tensei's episode 12 counted as present.
    /// </summary>
    public async Task<string?> FindVideoFileAsync(string seriesTitle, int episodeNumber, CancellationToken ct)
    {
        var baseTitle = SeasonDetector.ExtractBaseTitle(seriesTitle);
        var season    = SeasonSearch.EffectiveSeason(seriesTitle, 1);
        var (numbers, placed) = await AlternativesAsync(seriesTitle, episodeNumber, ct);

        var libraryPath = await _configService.GetValueAsync("LibraryPath", string.Empty, ct);
        if (!string.IsNullOrWhiteSpace(libraryPath) && Directory.Exists(libraryPath))
        {
            var origins = await OriginsAsync(libraryPath, ct);
            foreach (var showDir in ShowFolders(libraryPath, baseTitle))
            {
                // The season folder is already scoped to this show and season: the number is enough —
                // this episode's own, or the one counted straight through.
                foreach (var seasonDir in new[] { $"Season {season}", $"Season {season:00}" }.Distinct())
                {
                    var hit = FirstEpisode(Path.Combine(showDir, seasonDir), numbers, _ => true, origins, recursive: false);
                    if (hit != null) return hit;
                }

                // Anywhere else in the show folder (a custom template, files dropped in by hand) the
                // name has to say which season it is — or be placed on this episode by the matcher.
                var loose = FirstEpisode(showDir, new HashSet<int> { episodeNumber }, name => IsSeason(name, season), origins, recursive: true,
                                         skipDir: d => SeasonFolder.Match(Path.GetFileName(d)) is { Success: true } m
                                                       && int.Parse(m.Groups[1].Value) != season,
                                         placed: placed);
                if (loose != null) return loose;
            }
        }

        var downloadPath = await _configService.GetValueAsync("DownloadPath", string.Empty, ct);
        if (!string.IsNullOrWhiteSpace(downloadPath) && Directory.Exists(downloadPath))
        {
            // A shared folder full of other shows: the name must be this show and this season.
            bool IsThisShow(string name) =>
                _episodeNormalizer.MatchesTitle(name, baseTitle) && SeasonSearch.MatchesSeason(name, season);

            return FirstEpisode(Path.Combine(downloadPath, InvalidFolderChars.Replace(seriesTitle, "_")), new HashSet<int> { episodeNumber }, IsThisShow, null, false, placed: placed)
                ?? FirstEpisode(downloadPath, new HashSet<int> { episodeNumber }, IsThisShow, null, false, placed: placed);
        }

        return null;
    }

    /// <summary>Library folders for this show, the old exact-title folder first.</summary>
    private static IEnumerable<string> ShowFolders(string libraryPath, string baseTitle)
    {
        var legacy = LibraryFiling.LegacyShowFolder(baseTitle);
        var key = LibraryShows.Key(baseTitle);
        return Directory.EnumerateDirectories(libraryPath)
            .Where(d =>
            {
                var name = Path.GetFileName(d);
                return !name.StartsWith('_') && !name.StartsWith('.') && LibraryShows.FolderKeys(name).Contains(key);
            })
            .OrderBy(d => string.Equals(Path.GetFileName(d), legacy, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
    }

    // "Show S02E05" says season 2 outright; otherwise the usual rule — an unmarked name is season 1.
    private static bool IsSeason(string name, int season)
    {
        var parsed = ReleaseNameParser.Parse(name);
        if (parsed.HasExplicitSeasonEpisode) return parsed.Season == season;
        return SeasonSearch.MatchesSeason(name, season);
    }

    private async Task<Dictionary<string, string>> OriginsAsync(string libraryPath, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_dbFactory == null) return map;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            foreach (var o in await db.LibraryFileOrigins.AsNoTracking().ToListAsync(ct))
                map[o.Path] = o.OriginalName;
        }
        catch { /* the current names still work */ }
        return map;
    }

    /// <param name="numbers">The numbers the file may carry: the episode's own, and straight-through ones where they apply.</param>
    /// <param name="skipDir">In a recursive search, folders whose files can't be the one (another season's).</param>
    /// <param name="placed">The matcher's say: true when a name is this series' episode whatever its numbering.</param>
    private string? FirstEpisode(string dir, IReadOnlySet<int> numbers, Func<string, bool> accept,
        Dictionary<string, string>? origins, bool recursive, Func<string, bool>? skipDir = null, Func<string, bool>? placed = null)
    {
        if (!Directory.Exists(dir)) return null;

        bool Is(string name) =>
            (EpisodeOf(name) is { } n && numbers.Contains(n) && accept(name)) || placed?.Invoke(name) == true;

        foreach (var file in Directory.EnumerateFiles(dir, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
        {
            if (!VideoExtensions.Contains(Path.GetExtension(file))) continue;
            if (skipDir != null && skipDir(Path.GetDirectoryName(file)!)) continue;
            if (Is(Path.GetFileNameWithoutExtension(file))) return file;
            if (origins != null && origins.TryGetValue(file, out var original) && Is(Path.GetFileNameWithoutExtension(original)))
                return file;
        }
        return null;
    }

    private int? EpisodeOf(string name)
    {
        var parsed = ReleaseNameParser.Parse(name);
        return parsed.IsEpisodeRange ? null : parsed.Episode ?? _episodeNormalizer.ExtractEpisodeNumber(name);
    }
}
