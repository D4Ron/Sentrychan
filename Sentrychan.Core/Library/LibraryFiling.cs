using System.Text.RegularExpressions;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services;

namespace Sentrychan.Core.Library;

/// <summary>
/// Where the filing pipeline puts a finished download: the naming template applied to one
/// file, with the same rules as Tidy library so a later tidy finds nothing to change. Whatever
/// can't be named with confidence keeps its release name, inside the template's folders.
/// </summary>
public static class LibraryFiling
{
    private static readonly Regex InvalidFolderChars = new(@"[<>:""/\\|?*\x00-\x1F]", RegexOptions.Compiled);

    /// <param name="series">The series the download belongs to.</param>
    /// <param name="showSeasons">Every tracked series; the ones sharing its base title share its folder.</param>
    /// <param name="fileName">The downloaded file's name.</param>
    /// <param name="season">The season it was downloaded as (series record, or the release title).</param>
    /// <param name="episode">The episode it was downloaded as, when the download said; else parsed from the name.</param>
    /// <returns>The path relative to the library, and whether the file name changes.</returns>
    public static (string Relative, bool Renamed) Destination(
        NamingTemplate naming, Series series, IEnumerable<Series> showSeasons, string fileName, int season, int? episode)
    {
        // "Don't tidy": the layout the app always used, name untouched.
        if (series.TidyExcluded)
            return (Path.Combine(LegacyShowFolder(series.Title), $"Season {season}", fileName), false);

        var me = TidySeries.From(series);
        var key = LibraryShows.KeyOf(me);
        var show = LibraryShows.Group(showSeasons.Select(TidySeries.From).Where(s => s.Id != me.Id).Append(me))
                               .First(s => s.Key == key);
        var ext = Path.GetExtension(fileName);

        if (series.IsMovie)
        {
            var movie = new EpisodeNaming(show.Title, show.YearOr(null) ?? series.Year, 0, null, ext, IsMovie: true);
            return Keep(naming, movie, fileName, series.KeepFileNames);
        }

        var parsed = ReleaseNameParser.Parse(fileName);
        if (parsed.IsSpecial) season = 0;
        var n = new EpisodeNaming(show.Title, show.YearOr(null), season, null, ext, parsed.Group, parsed.Resolution, parsed.Version);

        // Placed by its season family: its own season, and its episode continued through the parts
        // before it in that season. The episode is the series' own (what the download was for, or
        // what the matcher makes of the name — it may count straight through the whole show).
        if (me.Placement is { } place && !parsed.IsSpecial && !parsed.IsEpisodeRange)
        {
            n = n with { Season = place.Season };
            var own = episode is > 0 ? episode
                : SeasonLayout.Resolver is { IsReady: true } r && ReleaseMatcher.Match(r, fileName, series) is (ReleaseVerdict.Yes, { } matched) ? matched
                : null;
            return own is { } e && place.EpisodeOffset is { } offset
                ? Keep(naming, n with { Episode = offset + e }, fileName, series.KeepFileNames)
                : (Path.Combine(naming.RenderFolder(n), fileName), false);
        }

        var ep = episode is > 0 ? episode : parsed.Episode;
        if (ep is not { } number || parsed.IsEpisodeRange)
            return (Path.Combine(naming.RenderFolder(n), fileName), false);

        var resolved = EpisodeNumbering.Resolve(season, number, parsed.HasExplicitSeasonEpisode, show.Totals, out _);
        if (resolved is not { } se)
            return (Path.Combine(naming.RenderFolder(n), fileName), false);

        return Keep(naming, n with { Season = se.Season, Episode = se.Episode }, fileName, series.KeepFileNames);
    }

    private static (string, bool) Keep(NamingTemplate naming, EpisodeNaming n, string fileName, bool keepFileName)
    {
        if (keepFileName) return (Path.Combine(naming.RenderFolder(n), fileName), false);
        var rendered = naming.Render(n);
        return (rendered, !string.Equals(Path.GetFileName(rendered), fileName, StringComparison.Ordinal));
    }

    /// <summary>
    /// The top folder a series' episodes are filed under (relative to the library) — what a
    /// download would create, so a folder made ahead of time is the one the downloads go into.
    /// </summary>
    public static string ShowFolder(NamingTemplate naming, Series series, IEnumerable<Series> showSeasons)
    {
        var (relative, _) = Destination(naming, series, showSeasons, "placeholder.mkv", SeasonSearch.EffectiveSeason(series.Title, series.SeasonNumber), 1);
        return relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)[0];
    }

    /// <summary>The show folder the app used before naming templates: the base title, sanitised.</summary>
    public static string LegacyShowFolder(string seriesTitle) =>
        InvalidFolderChars.Replace(SeasonDetector.ExtractBaseTitle(seriesTitle), string.Empty).Trim();
}
