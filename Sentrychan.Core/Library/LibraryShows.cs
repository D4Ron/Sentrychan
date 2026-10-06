using System.Text;
using Sentrychan.Core.Services;

namespace Sentrychan.Core.Library;

/// <summary>
/// A show as the library sees it: every tracked season that shares a base title shares one
/// folder ("Show (2020)/Season 02" holds "Show Season 2"). Shared by the planner, the filing
/// pipeline and the file locator so they agree on names.
/// </summary>
public sealed class LibraryShow
{
    public required string Key { get; init; }
    public required IReadOnlyList<TidySeries> Seasons { get; init; }

    /// <summary>The show's title, from the earliest tracked season.</summary>
    public string Title => LibraryShows.ShowTitle(First.Title);

    /// <summary>
    /// The show's year is its first season's. A later season's own year would name the folder
    /// wrongly, so without a season-1 record it comes from the existing folder name, if any.
    /// </summary>
    public int? YearOr(int? folderYear) =>
        First.Placement?.ShowYear ?? (First.EffectiveSeason <= 1 && First.Year is { } y ? y : folderYear);

    public bool IsMovie => Seasons.Count > 0 && Seasons.All(s => s.IsMovie);

    private TidySeries First => Seasons.OrderBy(s => s.EffectiveSeason).ThenBy(s => s.Id).First();

    public TidySeries? ForSeason(int season) =>
        Seasons.Where(s => !s.IsMovie && s.EffectiveSeason == season).OrderBy(s => s.Id).FirstOrDefault();

    /// <summary>
    /// Episodes per season where the tracked records say: one record's length, or for a season of
    /// several parts, where its last tracked part ends (its offset plus its length).
    /// </summary>
    public IReadOnlyDictionary<int, int?> Totals =>
        Seasons.Where(s => !s.IsMovie)
               .GroupBy(s => s.EffectiveSeason)
               .ToDictionary(g => g.Key, g => g.Count() == 1 && g.First().EpisodeOffset is null or 0
                   ? g.First().TotalEpisodes
                   : g.All(s => s.EpisodeOffset is not null && s.TotalEpisodes is not null)
                       ? g.Max(s => s.EpisodeOffset!.Value + s.TotalEpisodes!.Value)
                       : (int?)null);
}

public static class LibraryShows
{
    /// <summary>Case-, accent- and punctuation-blind key: "Re:ZERO", "ReZERO" and "re zero" agree.</summary>
    public static string Key(string title)
    {
        var formD = title.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var c in formD)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    public static string KeyOf(TidySeries s) => ShowKey(s.Title);

    /// <summary>
    /// The show a title belongs to, as a folder sees it: its base title without a cour's subtitle
    /// after " - " — "Bleach: Sennen Kessen-hen - Kashin-tan" is filed with "Bleach: Sennen
    /// Kessen-hen" (each cour used to get a folder of its own).
    /// </summary>
    public static string ShowTitle(string title)
    {
        var t = SeasonDetector.ExtractBaseTitle(title);
        var dash = t.IndexOf(" - ", StringComparison.Ordinal);
        return dash > 0 ? t[..dash].Trim() : t;
    }

    public static string ShowKey(string title) => Key(ShowTitle(title));

    public static List<LibraryShow> Group(IEnumerable<TidySeries> series) =>
        series.GroupBy(KeyOf)
              .Where(g => g.Key.Length > 0)
              .Select(g => new LibraryShow { Key = g.Key, Seasons = g.ToList() })
              .ToList();

    /// <summary>
    /// The keys a library folder may be known by: its name as is, without a trailing
    /// "(Year)", and cleaned of release tags — so "Show", "Show (2020)" and
    /// "[Group] Show [BD 1080p]" all find the show "Show".
    /// </summary>
    public static IEnumerable<string> FolderKeys(string folderName)
    {
        yield return Key(folderName);
        var (cleaned, _) = FolderNameCleaner.Clean(folderName);
        yield return Key(cleaned);
    }
}
