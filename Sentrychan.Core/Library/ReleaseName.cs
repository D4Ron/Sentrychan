using System.Text.RegularExpressions;
using AnitomySharp;

namespace Sentrychan.Core.Library;

/// <summary>What a release or library file name says about itself.</summary>
public sealed record ReleaseName(
    string Title,
    int? Episode,
    int? Season,
    string? Group,
    string? Resolution,
    int? Version,
    int? Year,
    string? AnimeType,
    bool IsEpisodeRange,
    bool HasExplicitSeasonEpisode)
{
    /// <summary>OVA, OAD, special or an "SP" episode: files under Season 00.</summary>
    public bool IsSpecial => AnimeType is { } t &&
        (t.Equals("OVA", StringComparison.OrdinalIgnoreCase) || t.Equals("OAD", StringComparison.OrdinalIgnoreCase) ||
         t.Equals("OAV", StringComparison.OrdinalIgnoreCase) || t.Equals("SP", StringComparison.OrdinalIgnoreCase) ||
         t.StartsWith("Special", StringComparison.OrdinalIgnoreCase));

    public bool IsMovie => AnimeType is { } t && t.Equals("Movie", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Openings, endings, previews and the like. Their number counts the extra ("ED2" is the second
    /// ending), not an episode, so they must never be filed as one.
    /// </summary>
    public bool IsExtra => AnimeType is { } t && ExtraTypes.Contains(t);

    private static readonly HashSet<string> ExtraTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "OP", "ED", "NCOP", "NCED", "Opening", "Ending", "PV", "Preview", "CM", "Menu", "Trailer", "Teaser", "Commercial",
    };
}

/// <summary>
/// Parses release and library file names with AnitomySharp, plus the few shapes it doesn't
/// cover: the bare "01" the Minimal preset writes, and "Title S02E05" read strictly as season 2
/// episode 5 (Anitomy reports both, but not that they came together).
/// </summary>
public static class ReleaseNameParser
{
    private static readonly Regex BareNumber = new(@"^\s*(\d{1,4})(?:\s*v(\d))?\s*$", RegexOptions.Compiled);
    private static readonly Regex SeasonEpisode = new(@"(?<![A-Za-z0-9])S(\d{1,2})E(\d{1,4})(?:v(\d))?(?![0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Anitomy leaves the type in the title for "Show - OVA": "Show - OVA" → "Show".
    private static readonly Regex TrailingType = new(@"\s*[-–]\s*(OVA|OAD|OAV|SP|Specials?|Movie)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <param name="name">A file name, with or without its extension.</param>
    public static ReleaseName Parse(string name)
    {
        var bare = Path.GetFileNameWithoutExtension(name);
        var numeric = BareNumber.Match(bare);
        if (numeric.Success)
            return new ReleaseName(string.Empty, int.Parse(numeric.Groups[1].Value), null, null, null,
                numeric.Groups[2].Success ? int.Parse(numeric.Groups[2].Value) : null, null, null, false, false);

        string title = string.Empty, group = string.Empty, resolution = string.Empty, type = string.Empty;
        int? episode = null, season = null, version = null, year = null;
        var episodeValues = 0;
        var episodeUnparseable = false;

        IEnumerable<Element> elements;
        try { elements = AnitomySharp.AnitomySharp.Parse(name); }
        catch { elements = []; }

        foreach (var el in elements)
        {
            switch (el.Category)
            {
                case Element.ElementCategory.ElementAnimeTitle:    title = el.Value; break;
                case Element.ElementCategory.ElementReleaseGroup:  group = el.Value; break;
                case Element.ElementCategory.ElementVideoResolution: resolution = el.Value; break;
                case Element.ElementCategory.ElementAnimeType:     if (type.Length == 0) type = el.Value; break;
                case Element.ElementCategory.ElementEpisodeNumber:
                    episodeValues++;
                    if (episode == null)
                    {
                        if (int.TryParse(el.Value, out var ep)) episode = ep;
                        else episodeUnparseable = true; // "12.5" — a recap or special, not episode 12
                    }
                    break;
                case Element.ElementCategory.ElementAnimeSeason:
                    if (int.TryParse(el.Value, out var s) && s is >= 0 and <= 99) season = s;
                    break;
                case Element.ElementCategory.ElementReleaseVersion:
                    if (int.TryParse(el.Value, out var v)) version = v;
                    break;
                case Element.ElementCategory.ElementAnimeYear:
                    if (int.TryParse(el.Value, out var y) && y is > 1900 and < 2200) year = y;
                    break;
            }
        }

        var sxe = SeasonEpisode.Match(bare);
        if (sxe.Success)
        {
            season  = int.Parse(sxe.Groups[1].Value);
            episode = int.Parse(sxe.Groups[2].Value);
            if (sxe.Groups[3].Success) version = int.Parse(sxe.Groups[3].Value);
            episodeValues = 1;
            episodeUnparseable = false;
        }

        // "2nd Season" and friends that Anitomy left inside the title.
        if (season == null && Services.SeasonDetector.HasSeasonIndicator(title))
        {
            season = Services.SeasonDetector.DetectSeason(title);
            title  = Services.SeasonDetector.ExtractBaseTitle(title);
        }

        title = TrailingType.Replace(title, string.Empty).Trim();
        if (episodeUnparseable) episode = null;

        return new ReleaseName(
            title, episode, season,
            group.Length > 0 ? group : null,
            resolution.Length > 0 ? resolution : null,
            version, year,
            type.Length > 0 ? type : null,
            IsEpisodeRange: episodeValues > 1,
            HasExplicitSeasonEpisode: sxe.Success);
    }
}
