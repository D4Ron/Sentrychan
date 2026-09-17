using System;

namespace Sentrychan.Core.Services;

/// <summary>
/// Canonicalises Series.AiringStatus.
///
/// Statuses reach us from several sources that don't agree: Jikan ("Currently Airing",
/// "Finished Airing", "Not yet aired"), the offline resolver ("ONGOING"/"FINISHED"),
/// and — accidentally — the Latest page, which reuses AnimeResult.Status to carry a
/// card sub-label like "EP 1 · SubsPlease". That junk was being written straight into
/// the database, where it matched no known status and made the series invisible in the
/// library. Normalising on write keeps only values the library can actually classify.
/// </summary>
public static class AiringStatusNormalizer
{
    public const string Airing      = "Currently Airing";
    public const string Finished    = "Finished Airing";
    public const string NotYetAired = "Not yet aired";

    /// <summary>
    /// Maps any known spelling to a canonical value. Returns null for empty input or
    /// anything unrecognised (e.g. a card sub-label), so we store "unknown" rather
    /// than a string that silently breaks classification.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        var s = (raw ?? string.Empty).Trim();
        if (s.Length == 0) return null;

        if (Is(s, "Currently Airing", "Airing", "ONGOING", "Ongoing", "Releasing"))
            return Airing;

        if (Is(s, "Finished Airing", "Finished", "Completed", "FINISHED", "Complete"))
            return Finished;

        if (Is(s, "Not yet aired", "NOT_YET_RELEASED", "Upcoming", "Not yet released"))
            return NotYetAired;

        return null;
    }

    private static bool Is(string value, params string[] candidates)
    {
        foreach (var c in candidates)
            if (value.Equals(c, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
