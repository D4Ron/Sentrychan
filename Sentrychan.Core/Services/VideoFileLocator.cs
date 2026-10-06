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
    /// The other ways a file of these episodes may be named: the number groups counting straight
    /// through give each ("Sono Bisque Doll - 18" for season 2's episode 6), and the matcher, which
    /// places a name on this series' episode whatever its numbering. Empty when the series isn't in
    /// the library.
    /// </summary>
    private async Task<(Dictionary<int, int> Absolute, Func<string, int?>? Placed)> AlternativesAsync(
        string seriesTitle, IEnumerable<int> episodes, CancellationToken ct)
    {
        var absolute = new Dictionary<int, int>();
        if (_dbFactory == null || _resolver is not { IsReady: true } resolver) return (absolute, null);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var series = await db.Series.AsNoTracking().FirstOrDefaultAsync(s => s.Title == seriesTitle, ct);
            if (series is not { MalId: > 0 }) return (absolute, null);
            foreach (var ep in episodes)
                foreach (var (_, n) in ReleaseMatcher.AbsoluteForms(resolver, series, ep))
                    absolute.TryAdd(n, ep);
            var placed = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
            return (absolute, name => placed.TryGetValue(name, out var known) ? known
                : placed[name] = ReleaseMatcher.Match(resolver, name, series) is (ReleaseVerdict.Yes, var e) ? e : null);
        }
        catch { return (absolute, null); }
    }

    public async Task<string?> FindVideoFileAsync(string seriesTitle, int episodeNumber, CancellationToken ct) =>
        (await FindManyAsync(seriesTitle, [episodeNumber], ct)).GetValueOrDefault(episodeNumber);

    public Task<IReadOnlyDictionary<int, string>> FindVideoFilesAsync(string seriesTitle, int maxEpisode, CancellationToken ct) =>
        FindManyAsync(seriesTitle, Enumerable.Range(1, Math.Max(0, maxEpisode)).ToList(), ct);

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
    private async Task<IReadOnlyDictionary<int, string>> FindManyAsync(string seriesTitle, IReadOnlyCollection<int> episodes, CancellationToken ct)
    {
        var found = new Dictionary<int, string>();
        if (episodes.Count == 0) return found;
        var wanted = episodes.ToHashSet();
        var baseTitle = SeasonDetector.ExtractBaseTitle(seriesTitle);
        var season    = SeasonSearch.EffectiveSeason(seriesTitle, 1);
        var (absolute, placed) = await AlternativesAsync(seriesTitle, wanted, ct);

        // Earlier places win, as each episode's first file used to: its season folder, the rest of
        // the show folder, then the downloads.
        void Take(IEnumerable<(string File, IEnumerable<string> Names)> files, Func<string, int?> episodeOf)
        {
            foreach (var (file, names) in files)
                foreach (var name in names)
                    if (episodeOf(name) is { } ep && wanted.Contains(ep) && found.TryAdd(ep, file)) break;
        }

        var libraryPath = await _configService.GetValueAsync("LibraryPath", string.Empty, ct);
        if (!string.IsNullOrWhiteSpace(libraryPath) && Directory.Exists(libraryPath))
        {
            var origins = await OriginsAsync(libraryPath, ct);
            foreach (var showDir in ShowFolders(libraryPath, baseTitle))
            {
                // The season folder is already scoped to this show and season: the number is enough —
                // the episode's own, or the one counted straight through.
                foreach (var seasonDir in new[] { $"Season {season}", $"Season {season:00}" }.Distinct())
                    Take(Videos(Path.Combine(showDir, seasonDir), origins, recursive: false),
                         name => EpisodeOf(name) is { } n ? (wanted.Contains(n) ? n : absolute.TryGetValue(n, out var e) ? e : (int?)null) : null);

                // Anywhere else in the show folder (a custom template, files dropped in by hand) the
                // name has to say which season it is — or be placed on the episode by the matcher.
                Take(Videos(showDir, origins, recursive: true,
                            skipDir: d => SeasonFolder.Match(Path.GetFileName(d)) is { Success: true } m && int.Parse(m.Groups[1].Value) != season),
                     name => (EpisodeOf(name) is { } n && IsSeason(name, season) ? n : (int?)null) ?? placed?.Invoke(name));
                if (found.Count == wanted.Count) return found;
            }
        }

        var downloadPath = await _configService.GetValueAsync("DownloadPath", string.Empty, ct);
        if (!string.IsNullOrWhiteSpace(downloadPath) && Directory.Exists(downloadPath))
        {
            // A shared folder full of other shows: the name must be this show and this season.
            int? ThisShow(string name) =>
                (EpisodeOf(name) is { } n && _episodeNormalizer.MatchesTitle(name, baseTitle) && SeasonSearch.MatchesSeason(name, season) ? n : (int?)null)
                ?? placed?.Invoke(name);
            Take(Videos(Path.Combine(downloadPath, InvalidFolderChars.Replace(seriesTitle, "_")), null, false), ThisShow);
            Take(Videos(downloadPath, null, false), ThisShow);
        }
        return found;
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

    /// <summary>The videos in a folder, each with its name and, for a renamed file, the name it downloaded as.</summary>
    /// <param name="skipDir">In a recursive search, folders whose files can't be the one (another season's).</param>
    private static IEnumerable<(string File, IEnumerable<string> Names)> Videos(string dir,
        Dictionary<string, string>? origins, bool recursive, Func<string, bool>? skipDir = null)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
        {
            if (!VideoExtensions.Contains(Path.GetExtension(file))) continue;
            if (skipDir != null && skipDir(Path.GetDirectoryName(file)!)) continue;
            var names = new List<string> { Path.GetFileNameWithoutExtension(file) };
            if (origins != null && origins.TryGetValue(file, out var original)) names.Add(Path.GetFileNameWithoutExtension(original));
            yield return (file, names);
        }
    }

    private int? EpisodeOf(string name)
    {
        var parsed = ReleaseNameParser.Parse(name);
        return parsed.IsEpisodeRange ? null : parsed.Episode ?? _episodeNormalizer.ExtractEpisodeNumber(name);
    }
}
