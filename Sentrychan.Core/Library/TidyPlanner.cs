using System.Text.RegularExpressions;

namespace Sentrychan.Core.Library;

/// <summary>
/// Builds a <see cref="TidyPlan"/>: for every video in the library, where the naming template
/// would put it. Reads the disk, moves nothing. Folders starting with '_' (the app's own
/// _Unmatched and _Standalone) or '.' (the vault, torrent caches) are never touched.
/// </summary>
public sealed class TidyPlanner
{
    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".avi", ".webm", ".m4v", ".mov", ".flv", ".wmv" };

    private static readonly HashSet<string> SidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ass", ".srt", ".ssa", ".vtt", ".nfo", ".sub", ".idx", ".sup" };

    private static readonly Regex SeasonFolder  = new(@"^(?:Season|Series|S)\s*0*(\d{1,2})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SpecialFolder = new(@"^(?:Specials?|OVAs?|SP)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Bonus material has no episode identity; renaming it by a number it happens to carry would lie.
    private static readonly Regex ExtrasFolder  = new(@"^(?:Extras?|Bonus|NC(?:OP|ED)?s?|Creditless|Menus?|Scans|Fonts|Trailers?|Featurettes?|PV|CM)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly NamingTemplate _naming;
    private readonly Func<string, string?> _skipReason;
    private readonly Func<string, int?>? _resolveMalId;

    /// <param name="skipReason">Why a file must not move now (open, downloading…), or null.</param>
    /// <param name="resolveMalId">Optional: a MAL id for a folder name the titles don't match.</param>
    public TidyPlanner(NamingTemplate naming, Func<string, string?>? skipReason = null, Func<string, int?>? resolveMalId = null)
    {
        _naming = naming;
        _skipReason = skipReason ?? (_ => null);
        _resolveMalId = resolveMalId;
    }

    public TidyPlan Build(string libraryPath, IReadOnlyList<TidySeries> series)
    {
        var plan = new TidyPlan { LibraryPath = libraryPath, Naming = _naming };
        if (!Directory.Exists(libraryPath)) return plan;

        var shows = LibraryShows.Group(series);
        var byKey = shows.ToDictionary(s => s.Key);
        var claimedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var folders = Directory.EnumerateDirectories(libraryPath)
            .Select(d => new DirectoryInfo(d))
            .Where(d => !d.Name.StartsWith('_') && !d.Name.StartsWith('.'))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            var show = FindShow(folder.Name, byKey, shows);
            PlanFolder(plan, folder, show, claimedTargets);
        }
        return plan;
    }

    private LibraryShow? FindShow(string folderName, Dictionary<string, LibraryShow> byKey, List<LibraryShow> shows)
    {
        foreach (var key in LibraryShows.FolderKeys(folderName))
            if (byKey.TryGetValue(key, out var show)) return show;

        if (_resolveMalId?.Invoke(FolderNameCleaner.Clean(folderName).Title) is > 0 and var malId)
            return shows.FirstOrDefault(s => s.Seasons.Any(x => x.MalId == malId));
        return null;
    }

    private void PlanFolder(TidyPlan plan, DirectoryInfo folder, LibraryShow? show, HashSet<string> claimed)
    {
        var (cleanedTitle, folderYear) = FolderNameCleaner.Clean(folder.Name);

        if (show != null && show.Seasons.All(s => s.Excluded))
        {
            plan.Notes.Add($"{folder.Name} — \"Don't tidy\" is set for this series");
            return;
        }

        var title = show?.Title ?? cleanedTitle;
        var year  = show?.YearOr(folderYear) ?? folderYear;
        var totals = show?.Totals ?? new Dictionary<int, int?>();

        List<string> videos;
        try
        {
            videos = Directory.EnumerateFiles(folder.FullName, "*", SearchOption.AllDirectories)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            plan.Notes.Add($"{folder.Name} — couldn't be read ({ex.Message})");
            return;
        }

        // A movie folder holds one film; with more than one video there's no telling which it is.
        if (show?.IsMovie == true)
        {
            var movie = show.Seasons[0];
            foreach (var video in videos)
            {
                if (videos.Count > 1)
                {
                    plan.Items.Add(Unsure(video, folder.Name, movie.Id, "a movie folder with more than one video"));
                    continue;
                }
                var naming = new EpisodeNaming(title, year, 0, null, Path.GetExtension(video), IsMovie: true);
                AddMove(plan, claimed, video, folder.Name, movie.Id, naming, movie.KeepFileNames);
            }
            return;
        }

        foreach (var video in videos)
            PlanEpisode(plan, claimed, folder, video, show, title, year, totals);
    }

    private void PlanEpisode(TidyPlan plan, HashSet<string> claimed, DirectoryInfo folder, string video,
        LibraryShow? show, string title, int? year, IReadOnlyDictionary<int, int?> totals)
    {
        var rel = Path.GetRelativePath(folder.FullName, Path.GetDirectoryName(video)!);
        var dirs = rel == "." ? [] : rel.Split(Path.DirectorySeparatorChar);

        if (dirs.Any(d => ExtrasFolder.IsMatch(d)))
        {
            plan.Items.Add(Unsure(video, folder.Name, null, "bonus material (extras folder)"));
            return;
        }

        var parsed = ReleaseNameParser.Parse(Path.GetFileName(video));
        if (parsed.IsExtra)
        {
            plan.Items.Add(Unsure(video, folder.Name, null, "bonus material (an opening, ending or preview)"));
            return;
        }

        int? dirSeason = null;
        foreach (var d in dirs.Reverse())
        {
            var m = SeasonFolder.Match(d);
            if (m.Success) { dirSeason = int.Parse(m.Groups[1].Value); break; }
            if (SpecialFolder.IsMatch(d)) { dirSeason = 0; break; }
        }

        int? nameSeason = parsed.IsSpecial ? 0 : parsed.Season;
        if (dirSeason != null && nameSeason != null && dirSeason != nameSeason)
        {
            plan.Items.Add(Unsure(video, folder.Name, null,
                $"the folder says season {dirSeason} but the name says season {nameSeason}"));
            return;
        }

        var season = nameSeason ?? dirSeason ?? DefaultSeason(show);

        if (parsed.IsEpisodeRange)
        {
            plan.Items.Add(Unsure(video, folder.Name, null, "several episodes in one file"));
            return;
        }
        if (parsed.Episode is not { } episode)
        {
            plan.Items.Add(Unsure(video, folder.Name, null, "no episode number in the name"));
            return;
        }

        var resolved = EpisodeNumbering.Resolve(season, episode, parsed.HasExplicitSeasonEpisode, totals, out var why);
        if (resolved is not { } se)
        {
            plan.Items.Add(Unsure(video, folder.Name, show?.ForSeason(season)?.Id, why!));
            return;
        }

        var row = show?.ForSeason(se.Season) ?? (se.Season == 0 ? show?.ForSeason(1) : null);
        if (row?.Excluded == true)
        {
            plan.Items.Add(new TidyItem
            {
                Source = video, ShowFolder = folder.Name, SeriesId = row.Id,
                Status = TidyItemStatus.Skipped, Note = "\"Don't tidy\" is set for this season",
            });
            return;
        }

        var naming = new EpisodeNaming(title, year, se.Season, se.Episode, Path.GetExtension(video),
            parsed.Group, parsed.Resolution, parsed.Version);
        AddMove(plan, claimed, video, folder.Name, row?.Id,
            naming, keepFileName: row?.KeepFileNames ?? show?.Seasons.Any(s => s.KeepFileNames) ?? false,
            note: se.Season != season || se.Episode != episode
                ? $"episode {episode} counted across seasons → S{se.Season:00}E{se.Episode:00}"
                : null);
    }

    // With no season in the path or name: the only tracked season, else season 1.
    private static int DefaultSeason(LibraryShow? show)
    {
        var tv = show?.Seasons.Where(s => !s.IsMovie).ToList();
        return tv is { Count: 1 } ? tv[0].EffectiveSeason : 1;
    }

    private void AddMove(TidyPlan plan, HashSet<string> claimed, string video, string showFolder, int? seriesId,
        EpisodeNaming naming, bool keepFileName, string? note = null)
    {
        var relative = keepFileName
            ? Path.Combine(_naming.RenderFolder(naming), Path.GetFileName(video))
            : _naming.Render(naming);
        var dest = Path.GetFullPath(Path.Combine(plan.LibraryPath, relative));

        if (string.Equals(dest, video, StringComparison.Ordinal))
        {
            plan.AlreadyTidy++;
            claimed.Add(dest);
            return;
        }

        var skip = _skipReason(video);
        if (skip != null)
        {
            plan.Items.Add(new TidyItem
            {
                Source = video, Destination = dest, ShowFolder = showFolder, SeriesId = seriesId,
                Status = TidyItemStatus.Skipped, Note = skip,
            });
            return;
        }

        var sidecars = FindSidecars(video, dest);
        var item = new TidyItem
        {
            Source = video, Destination = dest, ShowFolder = showFolder, SeriesId = seriesId,
            Sidecars = sidecars, Note = note, Status = TidyItemStatus.Ready, Selected = true,
        };

        var clash = Clash(video, dest, claimed) ?? sidecars.Select(s => Clash(s.From, s.To, claimed)).FirstOrDefault(c => c != null);
        if (clash != null)
        {
            item.Status = TidyItemStatus.Collision;
            item.Selected = false;
            item.Note = clash;
        }
        else
        {
            claimed.Add(dest);
            foreach (var s in sidecars) claimed.Add(s.To);
        }
        plan.Items.Add(item);
    }

    // A case-only rename ("show s01e01" → "Show S01E01") targets the file itself; that's fine.
    private static string? Clash(string from, string to, HashSet<string> claimed)
    {
        if (claimed.Contains(to)) return $"another file in this plan would also be named {Path.GetFileName(to)}";
        if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase) && (File.Exists(to) || Directory.Exists(to)))
            return $"{Path.GetFileName(to)} already exists there";
        return null;
    }

    /// <summary>
    /// Subtitles, .nfo and thumbnails named after the video: "Ep 01.mkv" owns "Ep 01.ass",
    /// "Ep 01.en.forced.srt", "Ep 01.nfo" and "Ep 01-thumb.jpg". Whatever follows the video's
    /// name (a language suffix) is kept.
    /// </summary>
    public static List<SidecarMove> FindSidecars(string video, string dest)
    {
        var dir = Path.GetDirectoryName(video)!;
        var oldBase = Path.GetFileNameWithoutExtension(video);
        var newBase = Path.GetFileNameWithoutExtension(dest);
        var destDir = Path.GetDirectoryName(dest)!;
        var result = new List<SidecarMove>();

        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(file, video, StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.StartsWith(oldBase, StringComparison.OrdinalIgnoreCase)) continue;

            var rest = name[oldBase.Length..];
            var isThumb = rest.Equals("-thumb.jpg", StringComparison.OrdinalIgnoreCase)
                       || rest.Equals("-thumb.png", StringComparison.OrdinalIgnoreCase);
            var isSidecar = rest.StartsWith('.') && SidecarExtensions.Contains(Path.GetExtension(name));
            if (!isThumb && !isSidecar) continue;

            result.Add(new SidecarMove(file, Path.Combine(destDir, newBase + rest)));
        }
        return result;
    }

    private static TidyItem Unsure(string video, string showFolder, int? seriesId, string why) => new()
    {
        Source = video, ShowFolder = showFolder, SeriesId = seriesId,
        Status = TidyItemStatus.Unsure, Note = why,
    };
}
