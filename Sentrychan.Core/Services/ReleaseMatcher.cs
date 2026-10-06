using System.Text.RegularExpressions;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.Services;

/// <summary>Whether a release belongs to a series: yes, no, or nothing to go on (fall back to title matching).</summary>
public enum ReleaseVerdict { Unknown, Yes, No }

/// <summary>
/// Decides whether a release is an episode of a library series, and which episode in that series'
/// own numbering. MAL splits many shows into seasons, cours and parts that each start at 1, while
/// release groups often number them straight through, from whichever title they use:
/// "Bleach - Sennen Kessen Hen - 48" is episode 8 of the fourth cour, "Jujutsu Kaisen - 50" is
/// episode 3 of its third season, "Enen no Shouboutai S3 - 14" is episode 2 of season 3's second
/// part. A number is counted from the entry the release names — or the season it names — through
/// the show's season chain (<see cref="ITitleResolverService.GetSeasonChain"/>).
/// </summary>
public static partial class ReleaseMatcher
{
    public static (ReleaseVerdict Verdict, int? Episode) Match(ITitleResolverService resolver, string release, Series series)
    {
        if (!resolver.IsReady || series.MalId <= 0 || string.IsNullOrWhiteSpace(release)) return (ReleaseVerdict.Unknown, null);

        var parsed = resolver.ParseRelease(release);
        var resolved = resolver.ResolveRelease(release);
        if (resolved is not { MalId: > 0 }) return (ReleaseVerdict.Unknown, parsed.Episode);

        var chain = resolver.GetSeasonChain(series.MalId);
        var mine = IndexOf(chain, series.MalId);
        var named = IndexOf(chain, resolved.MalId);
        if (mine < 0 || named < 0)
            return resolved.MalId == series.MalId ? (ReleaseVerdict.Yes, parsed.Episode) : (ReleaseVerdict.No, null);

        // Where the release's numbering starts: the season it names ("S04E08", "S3 - 14"), else the
        // entry its title names. A named season that isn't in the chain can't be placed.
        var from = named;
        if (parsed.Season > 1)
        {
            // The season counts from the entry the title names on its own: the resolver may already
            // have used the season to pick a later entry ("TYBW S04E08" → the fourth cour).
            var seasonBase = resolver.ResolveTitle(parsed.Title) is { MalId: > 0 } titleOnly && IndexOf(chain, titleOnly.MalId) is >= 0 and var b
                ? b : named;
            if (SeasonEntry(chain, seasonBase, parsed.Season) is not { } seasonStart) return (ReleaseVerdict.No, null);
            from = seasonStart;
        }

        if (parsed.Episode is not { } number)
            return from == mine ? (ReleaseVerdict.Yes, null) : (ReleaseVerdict.No, null);

        // Counted on from the start, the number lands in one entry; that's whose episode it is.
        if (CourOf(chain, number, from) is { } owner)
            return owner == mine && Offset(chain, mine, from) is { } offset
                ? (ReleaseVerdict.Yes, number - offset)
                : (ReleaseVerdict.No, null);

        // Can't be placed (a finished entry of unknown length on the way, or past the chain's end):
        // only taken at face value by the entry it starts from — a long show whose count has run
        // past the database's ("One Piece - 1150").
        return from == mine ? (ReleaseVerdict.Yes, number) : (ReleaseVerdict.No, null);
    }

    /// <summary>
    /// The chain entry a release's season means. A title that carries its own season ("… 3rd Season",
    /// "Mushoku Tensei III") makes the release's season absolute; a title that doesn't (the first
    /// season, or an arc's own name such as "Sennen Kessen-hen") counts the release's season from it.
    /// Parts of one season ("Part 2", "Cour 2") share that season's number; the first part is returned.
    /// </summary>
    internal static int? SeasonEntry(IReadOnlyList<ResolvedAnime> chain, int named, int season)
    {
        var seasons = SeasonsOf(chain);
        var target = OwnSeason(chain[named].CanonicalTitle) is not null ? season : seasons[named] + season - 1;
        // A title that says its season beats a count: an arc between two seasons ("Shiguang Dailiren:
        // Yingdu Pian" before "… III") would otherwise be taken for the season after.
        for (var i = named; i < chain.Count; i++)
            if (OwnSeason(chain[i].CanonicalTitle) == target) return i;
        var j = Array.IndexOf(seasons, target);
        return j >= 0 ? j : null;
    }

    /// <summary>Each entry's season number: its own when its title says, else the one before it — plus one, unless it's a later part.</summary>
    public static int[] SeasonsOf(IReadOnlyList<ResolvedAnime> chain)
    {
        var seasons = new int[chain.Count];
        for (var i = 0; i < chain.Count; i++)
        {
            var title = chain[i].CanonicalTitle;
            seasons[i] = OwnSeason(title) ?? (i == 0 ? 1 : IsLaterPart(title) ? seasons[i - 1] : seasons[i - 1] + 1);
        }
        return seasons;
    }

    /// <summary>The season a title names itself, with "Part N"/"Cour N" taken out (a part isn't a season).</summary>
    private static int? OwnSeason(string title)
    {
        var withoutPart = PartPattern().Replace(title, " ");
        var s = SeasonDetector.DetectSeason(withoutPart);
        return s > 1 ? s : null;
    }

    private static bool IsLaterPart(string title) =>
        PartPattern().Match(title) is { Success: true } m && m.Groups["n"].Value is var n && n != "1" && !n.Equals("I", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\b(?:Part|Cour)\s+(?<n>\d+|II|III|IV|V|VI)\b|\b(?<n>2)nd\s+(?:Part|Cour)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PartPattern();

    /// <summary>
    /// The numbers groups that count straight through could give episode <paramref name="episode"/>
    /// of this series, each with the title they'd use: from the start of the chain (the franchise's
    /// first title) and from the first entry sharing the series' own title. Empty for a first season
    /// or a show MAL doesn't split, or when the earlier lengths aren't known.
    /// </summary>
    public static IReadOnlyList<(string Title, int Number)> AbsoluteForms(ITitleResolverService resolver, Series series, int episode)
    {
        if (!resolver.IsReady || series.MalId <= 0) return [];
        var chain = resolver.GetSeasonChain(series.MalId);
        var mine = IndexOf(chain, series.MalId);
        if (mine <= 0) return [];

        var key = TitleResolverService.ChainKey(chain[mine].CanonicalTitle);
        var starts = new List<int> { 0 };
        var sameTitle = Enumerable.Range(0, mine).FirstOrDefault(i => TitleResolverService.ChainKey(chain[i].CanonicalTitle) == key, -1);
        if (sameTitle >= 0) starts.Add(sameTitle);

        return starts.Distinct()
            .Where(s => s < mine)
            .Select(s => (Start: s, Offset: Offset(chain, mine, s)))
            .Where(x => x.Offset is not null)
            .Select(x => (chain[x.Start].CanonicalTitle, x.Offset!.Value + episode))
            .ToList();
    }

    /// <summary>The straight-through number from the chain's start, or null (see <see cref="AbsoluteForms"/>).</summary>
    public static int? AbsoluteEpisode(ITitleResolverService resolver, Series series, int episode) =>
        AbsoluteForms(resolver, series, episode) is [var first, ..] ? first.Number : null;

    /// <summary>
    /// Which entry of the chain episode <paramref name="absolute"/> falls in, counting from entry
    /// <paramref name="from"/>. The first entry still airing (or announced) takes everything after
    /// the finished ones — its length isn't known yet, and nothing after it has started. Null past
    /// every finished entry when none is airing, or after a finished entry of unknown length.
    /// </summary>
    internal static int? CourOf(IReadOnlyList<ResolvedAnime> chain, int absolute, int from = 0)
    {
        if (absolute < 1) return null;
        var start = 0;
        for (var i = from; i < chain.Count; i++)
        {
            if (Length(chain[i]) is { } count)
            {
                if (absolute <= start + count) return i;
                start += count;
                continue;
            }
            return chain[i].Status == "FINISHED" ? null : i;
        }
        return null;
    }

    /// <summary>
    /// Episodes from entry <paramref name="from"/> up to entry <paramref name="index"/>, when every one
    /// of them has finished — a guess would download or file the wrong episode.
    /// </summary>
    internal static int? Offset(IReadOnlyList<ResolvedAnime> chain, int index, int from = 0)
    {
        if (index < from) return null;
        var total = 0;
        for (var i = from; i < index; i++)
        {
            if (Length(chain[i]) is not { } count) return null;
            total += count;
        }
        return total;
    }

    /// <summary>
    /// An entry's length, when it can be relied on. Only finished entries count: one that's airing
    /// or announced carries a placeholder ("Kusuriya no Hitorigoto 3rd Season", 1 episode, in its
    /// first week), and trusting it would push its own episode 2 into the next part.
    /// </summary>
    private static int? Length(ResolvedAnime entry) =>
        entry.Status == "FINISHED" && entry.Episodes is > 0 ? entry.Episodes : null;

    private static int IndexOf(IReadOnlyList<ResolvedAnime> chain, int malId)
    {
        for (var i = 0; i < chain.Count; i++) if (chain[i].MalId == malId) return i;
        return -1;
    }
}
