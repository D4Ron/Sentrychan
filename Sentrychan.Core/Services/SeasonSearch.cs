using System;

namespace Sentrychan.Core.Services;

/// <summary>
/// Season-aware search helpers, layered on top of <see cref="SeasonDetector"/>.
///
/// Release trackers list every season of a show under near-identical titles, so a
/// naive "Title batch" query happily returns Season 1 (usually the most-seeded) when
/// the user asked for Season 2. The pattern is: query on the base title (so every
/// season's releases come back), then filter the results to the season requested.
///
/// All the actual parsing lives in SeasonDetector — this only adds the
/// "does this release match the season I asked for?" decision.
/// </summary>
public static class SeasonSearch
{
    /// <summary>
    /// Strips season qualifiers so the search fetches every season.
    /// "My Dress-Up Darling Season 2" → "My Dress-Up Darling".
    /// </summary>
    public static string StripSeason(string title) => SeasonDetector.ExtractBaseTitle(title);

    /// <summary>
    /// The season a series actually represents. Series.SeasonNumber has historically
    /// never been populated (it defaults to 1), so the season usually only lives in
    /// the title text — we trust whichever source is higher.
    /// </summary>
    public static int EffectiveSeason(string title, int storedSeason)
    {
        var stored = storedSeason <= 0 ? 1 : storedSeason;
        return Math.Max(stored, SeasonDetector.DetectSeason(title));
    }

    /// <summary>
    /// True if a release title is consistent with the requested season. If the release
    /// names a season, it must match exactly. If it names none, we only accept it for
    /// Season 1 — an unmarked release is almost always Season 1, so accepting it for a
    /// Season ≥2 request is exactly the bug that grabbed the wrong season.
    /// </summary>
    public static bool MatchesSeason(string releaseTitle, int requestedSeason)
    {
        if (SeasonDetector.HasSeasonIndicator(releaseTitle))
            return SeasonDetector.DetectSeason(releaseTitle) == requestedSeason;

        return requestedSeason <= 1;
    }
}
