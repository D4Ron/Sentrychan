using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.Services;

/// <summary>Whether a release belongs to a series: yes, no, or nothing to go on (fall back to title matching).</summary>
public enum ReleaseVerdict { Unknown, Yes, No }

/// <summary>
/// Decides whether a release is an episode of a library series, and which episode in that series'
/// own numbering. MAL splits many shows into cours or parts that each start at 1, while release
/// groups often number them straight through: "Bleach - Sennen Kessen Hen - 48" and
/// "Bleach Thousand-Year Blood War S01E45" are episodes 8 and 5 of the fourth cour, which MAL lists
/// as its own entry. Both releases name the show by its first cour, so a plain MAL-id comparison
/// sent them to the finished first cour and never to the one airing.
/// </summary>
public static class ReleaseMatcher
{
    public static (ReleaseVerdict Verdict, int? Episode) Match(ITitleResolverService resolver, string release, Series series)
    {
        if (!resolver.IsReady || series.MalId <= 0 || string.IsNullOrWhiteSpace(release)) return (ReleaseVerdict.Unknown, null);

        var parsed = resolver.ParseRelease(release);
        var resolved = resolver.ResolveRelease(release);
        if (resolved is not { MalId: > 0 }) return (ReleaseVerdict.Unknown, parsed.Episode);

        var chain = resolver.GetSeasonChain(series.MalId);
        var mine = IndexOf(chain, series.MalId);
        var episode = parsed.Episode;
        // "S04E08": the season the release names, as an entry of the chain.
        var namedSeason = parsed.Season > 1 ? SeasonEntry(chain, parsed.Season) : null;

        if (resolved.MalId == series.MalId)
        {
            // Resolved to this entry by its base title, but the release names another season of it.
            if (mine >= 0 && namedSeason is { } other && other != mine) return (ReleaseVerdict.No, null);

            // A number past this entry's own length is counted straight through the show: either
            // it lands in this entry (renumber it) or in another cour, whose episode it is — a
            // finished first cour must not take cour 4's 48.
            if (episode is { } e && mine >= 0 && chain[mine].Episodes is { } own && e > own)
            {
                var owner = CourOf(chain, e);
                if (owner == mine && Offset(chain, mine) is { } start) return (ReleaseVerdict.Yes, e - start);
                if (owner is not null) return (ReleaseVerdict.No, null);
            }
            return (ReleaseVerdict.Yes, episode);
        }

        // A different entry: only the same show, split by MAL, can still be a match.
        var named = IndexOf(chain, resolved.MalId);
        if (mine < 0 || named < 0 || episode is not { } number)
            return (ReleaseVerdict.No, null);

        // Named after a later cour and numbered within it ("… Ketsubetsu-tan - 05"): that cour's episode.
        if (named > 0 && (chain[named].Episodes is not { } namedCount || number <= namedCount))
            return (ReleaseVerdict.No, null);

        // A season named outright and numbered within it: that season's episode.
        if (namedSeason is { } season && (chain[season].Episodes is not { } seasonCount || number <= seasonCount))
            return season == mine ? (ReleaseVerdict.Yes, number) : (ReleaseVerdict.No, null);

        // Numbered straight through from the first cour.
        if (CourOf(chain, number) == mine && Offset(chain, mine) is { } offset)
            return (ReleaseVerdict.Yes, number - offset);

        return (ReleaseVerdict.No, null);
    }

    /// <summary>
    /// The chain entry a season number means. MAL titles usually carry it ("… 3rd Season"), and
    /// a season split in parts has several entries ("2nd Season", "2nd Season Part 2"), so the
    /// titles decide when they say; a show whose cours carry no numbers counts them in order.
    /// </summary>
    internal static int? SeasonEntry(IReadOnlyList<ResolvedAnime> chain, int season)
    {
        var numbered = chain.Select(a => SeasonDetector.DetectSeason(a.CanonicalTitle)).ToList();
        if (numbered.Any(s => s > 1))
        {
            var i = numbered.IndexOf(season);
            return i >= 0 ? i : null;
        }
        return season - 1 < chain.Count ? season - 1 : null;
    }

    /// <summary>
    /// The episode number groups that count straight through would give episode
    /// <paramref name="episode"/> of this series, or null when it's the same (a first cour, or a
    /// show MAL doesn't split) or can't be worked out.
    /// </summary>
    public static int? AbsoluteEpisode(ITitleResolverService resolver, Series series, int episode)
    {
        if (!resolver.IsReady || series.MalId <= 0) return null;
        var chain = resolver.GetSeasonChain(series.MalId);
        var mine = IndexOf(chain, series.MalId);
        return mine > 0 && Offset(chain, mine) is { } offset ? offset + episode : null;
    }

    /// <summary>
    /// Which entry of the chain episode <paramref name="absolute"/> falls in, counting straight
    /// through. Null when an earlier entry's length isn't known — a guess there would download or
    /// file the wrong episode.
    /// </summary>
    internal static int? CourOf(IReadOnlyList<ResolvedAnime> chain, int absolute)
    {
        if (absolute < 1) return null;
        var start = 0;
        for (var i = 0; i < chain.Count; i++)
        {
            if (chain[i].Episodes is not { } count || count <= 0)
                return i == chain.Count - 1 ? i : null; // the last entry may still be airing
            if (absolute <= start + count) return i;
            start += count;
        }
        return null; // past every known entry
    }

    /// <summary>Episodes before entry <paramref name="index"/>, when every one of them is known.</summary>
    internal static int? Offset(IReadOnlyList<ResolvedAnime> chain, int index)
    {
        var total = 0;
        for (var i = 0; i < index; i++)
        {
            if (chain[i].Episodes is not { } count || count <= 0) return null;
            total += count;
        }
        return total;
    }

    private static int IndexOf(IReadOnlyList<ResolvedAnime> chain, int malId)
    {
        for (var i = 0; i < chain.Count; i++) if (chain[i].MalId == malId) return i;
        return -1;
    }
}
