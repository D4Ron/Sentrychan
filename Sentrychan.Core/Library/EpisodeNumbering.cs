namespace Sentrychan.Core.Library;

/// <summary>
/// Turns the episode number a file carries into a season-relative one. Many releases keep
/// counting across seasons — season 2's first episode is "13" — while Jellyfin and Plex want
/// S02E01. The app knows how many episodes each tracked season has (Series.TotalEpisodes),
/// which is enough to convert, but only when every season before is known. Anything that
/// could be read two ways is reported as unsure and left alone.
/// </summary>
public static class EpisodeNumbering
{
    /// <param name="season">Season the file appears to belong to (folder or name). 0 = specials.</param>
    /// <param name="episode">The number in the file name.</param>
    /// <param name="explicitSeasonEpisode">The name says "SxxEyy" — by convention already season-relative.</param>
    /// <param name="totals">Episodes per season, where the library knows it; a null value means unknown.</param>
    /// <param name="unsure">Why the number couldn't be trusted, when the result is null.</param>
    public static (int Season, int Episode)? Resolve(
        int season, int episode, bool explicitSeasonEpisode,
        IReadOnlyDictionary<int, int?> totals, out string? unsure)
    {
        unsure = null;
        if (season <= 0) return (0, episode);

        var total = totals.TryGetValue(season, out var t) ? t : null;

        if (total is { } known)
        {
            if (episode >= 1 && episode <= known) return (season, episode);
            if (explicitSeasonEpisode)
            {
                unsure = $"S{season:00}E{episode:00}, but season {season} has {known} episodes";
                return null;
            }
            if (TryAbsolute(episode, season, totals, out var mapped)) return mapped;
            unsure = $"episode {episode} doesn't fit season {season} ({known} episodes), and the seasons before it don't explain the number";
            return null;
        }

        // Season length unknown. Season 1, and an explicit SxxEyy, mean what they say.
        if (season == 1 || explicitSeasonEpisode) return (season, episode);

        // A later season of unknown length: a number past everything before it could be
        // either this season's own episode or a continued count. Only small numbers are safe.
        var before = Cumulative(season - 1, totals);
        if (before is not { } b)
        {
            unsure = $"episode {episode} of season {season}: the earlier seasons' lengths aren't known, so it could be a continued count";
            return null;
        }
        if (episode <= b) return (season, episode);
        unsure = $"episode {episode} of season {season}: past the {b} episodes before it, so it could be a continued count";
        return null;
    }

    // Absolute numbering: the season whose cumulative range holds the number. Never an
    // earlier season than the file already sits in — a file isn't filed too late by a season.
    private static bool TryAbsolute(int episode, int fromSeason, IReadOnlyDictionary<int, int?> totals,
        out (int Season, int Episode) mapped)
    {
        mapped = default;
        var cum = 0;
        var last = totals.Keys.Where(k => k > 0).DefaultIfEmpty(0).Max();
        for (var k = 1; k <= last; k++)
        {
            if (!totals.TryGetValue(k, out var tk) || tk is not { } n) return false;
            if (episode > cum && episode <= cum + n && k >= fromSeason)
            {
                mapped = (k, episode - cum);
                return true;
            }
            cum += n;
        }
        return false;
    }

    private static int? Cumulative(int throughSeason, IReadOnlyDictionary<int, int?> totals)
    {
        var sum = 0;
        for (var k = 1; k <= throughSeason; k++)
        {
            if (!totals.TryGetValue(k, out var tk) || tk is not { } n) return null;
            sum += n;
        }
        return sum;
    }
}
