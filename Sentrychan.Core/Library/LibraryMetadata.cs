using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.Library;

/// <summary>Fills in what library naming needs and older series records lack.</summary>
public static class LibraryMetadata
{
    /// <summary>
    /// Sets Year and MediaType from the offline anime database, by MAL id, where they're missing.
    /// Returns how many records changed; the caller saves.
    /// </summary>
    public static int Backfill(IEnumerable<Series> series, ITitleResolverService resolver)
    {
        var changed = 0;
        foreach (var s in series)
        {
            if (s.MalId <= 0 || (s.Year != null && s.MediaType != null)) continue;
            if (resolver.GetByMalId(s.MalId) is not { } entry) continue;
            var before = (s.Year, s.MediaType);
            s.Year ??= entry.Year;
            s.MediaType ??= NormalizeType(entry.Type);
            if ((s.Year, s.MediaType) != before) changed++;
        }
        return changed;
    }

    /// <summary>The year of an ISO date as the anime API gives it ("2023-09-29T00:00:00+00:00").</summary>
    public static int? YearOf(string? isoDate) =>
        isoDate is { Length: >= 4 } && int.TryParse(isoDate.AsSpan(0, 4), out var y) && y > 1900 ? y : null;

    /// <summary>The anime database says "MOVIE", the API "Movie"; store one spelling.</summary>
    public static string? NormalizeType(string? type) => type?.ToUpperInvariant() switch
    {
        null or "" or "UNKNOWN" => null,
        "TV" => "TV",
        "OVA" => "OVA",
        "ONA" => "ONA",
        var t => char.ToUpperInvariant(t[0]) + t[1..].ToLowerInvariant(),
    };
}
