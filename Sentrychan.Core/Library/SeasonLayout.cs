using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;
using Sentrychan.Core.Services;

namespace Sentrychan.Core.Library;

/// <summary>Where a series sits in its show's library folder: its season, and where its episodes start in it.</summary>
/// <param name="Season">The season folder it's filed in.</param>
/// <param name="EpisodeOffset">
/// Episodes of the same season before it — a later part continues its season ("… Part 2" episode 1
/// is E13 after a 12-episode first part). Null when an earlier part's length isn't known yet.
/// </param>
/// <param name="ShowYear">The year the folder's first season started — it names the folder, whichever season is tracked.</param>
public sealed record SeasonPlacement(int Season, int? EpisodeOffset, int? ShowYear = null);

/// <summary>
/// The library layout of a show split into seasons, parts and cours (see ReleaseMatcher's season
/// chains). Parts share their season — "Part 2", "2nd Season Part 2", and a cour named by a
/// subtitle after " - " ("Bleach: Sennen Kessen-hen - Ketsubetsu-tan") — and continue its episode
/// numbers, the way Jellyfin and Plex usually list them; per series, "separate seasons for parts"
/// gives each its own. A folder named after a later arc ("Bleach: Sennen Kessen-hen") counts its
/// seasons from that arc. Without the title resolver, or for a show MAL doesn't split, the series'
/// title decides as before.
/// </summary>
public static class SeasonLayout
{
    /// <summary>Set at startup, once the title resolver exists. Null (tests, tools): titles decide.</summary>
    public static ITitleResolverService? Resolver { get; set; }

    public static SeasonPlacement? For(Series series) =>
        Resolver is { IsReady: true } r ? Place(r, series.MalId, series.Title, series.SeparateParts) : null;

    /// <param name="title">The series' own title in the library — it names the folder.</param>
    public static SeasonPlacement? Place(ITitleResolverService resolver, int malId, string title, bool separateParts)
    {
        if (malId <= 0) return null;
        var chain = resolver.GetSeasonChain(malId);
        var mine = IndexOf(chain, malId);
        if (chain.Count < 2 || mine < 0) return null;

        var seasons = separateParts ? Enumerable.Range(1, chain.Count).ToArray() : MergedSeasons(chain);

        // A folder named after a later arc counts from that arc: its first entry is season 1.
        var folderKey = LibraryShows.ShowKey(title);
        var arcStart = Enumerable.Range(0, chain.Count).FirstOrDefault(i => LibraryShows.ShowKey(chain[i].CanonicalTitle) == folderKey, -1);
        var shift = arcStart > 0 && arcStart <= mine ? seasons[arcStart] - 1 : 0;
        var season = seasons[mine] - shift;

        int? offset = 0;
        if (!separateParts)
        {
            var arc = LibraryShows.ShowKey(chain[mine].CanonicalTitle);
            for (var i = 0; i < mine && offset is not null; i++)
            {
                // Only the parts of this arc: an arc of its own between two seasons ("… Yingdu Pian"
                // before "… III") has its own folder and doesn't push the next season's numbers on.
                if (seasons[i] != seasons[mine] || LibraryShows.ShowKey(chain[i].CanonicalTitle) != arc) continue;
                offset = Length(chain[i]) is { } n ? offset + n : null;
            }
        }
        // Only an arc's own folder takes its start year from here: other folders keep the name they have
        // (a show tracked from its third season would otherwise move to "Show (first year)").
        return new SeasonPlacement(Math.Max(1, season), offset, shift > 0 ? chain[arcStart].Year : null);
    }

    /// <summary>
    /// Each entry's season with parts merged: its own number when its title says, the season before
    /// it when it's a later part (or a cour of the same title), else the next one.
    /// </summary>
    public static int[] MergedSeasons(IReadOnlyList<ResolvedAnime> chain)
    {
        var seasons = new int[chain.Count];
        for (var i = 0; i < chain.Count; i++)
        {
            var title = chain[i].CanonicalTitle;
            seasons[i] = ReleaseMatcher.OwnSeason(title)
                ?? (i == 0 ? 1
                    : ReleaseMatcher.IsLaterPart(title) || TitleResolverService.ChainKey(title) == TitleResolverService.ChainKey(chain[i - 1].CanonicalTitle)
                        ? seasons[i - 1]
                        : seasons[i - 1] + 1);
        }
        return seasons;
    }

    private static int? Length(ResolvedAnime entry) =>
        entry.Status == "FINISHED" && entry.Episodes is > 0 ? entry.Episodes : null;

    private static int IndexOf(IReadOnlyList<ResolvedAnime> chain, int malId)
    {
        for (var i = 0; i < chain.Count; i++) if (chain[i].MalId == malId) return i;
        return -1;
    }
}
