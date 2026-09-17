using System.Text.RegularExpressions;

namespace Sentrychan.Core.Services;

/// <summary>
/// Extracts season number and base title from anime title strings.
/// Handles all common season naming conventions used in RSS feeds and MAL titles.
/// </summary>
public static class SeasonDetector
{
    // Ordered by specificity/confidence (most explicit first)
    private static readonly (Regex Pattern, bool IsRoman, int GroupIndex)[] SeasonPatterns =
    [
        // "1st Season", "2nd Season", "3rd Season", "4th Season"
        (new Regex(@"\b(\d+)(?:st|nd|rd|th)\s+Season\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), false, 1),
        // "Season 2", "Season 02"
        (new Regex(@"\bSeason\s+0?(\d{1,2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), false, 1),
        // Standalone S2, S02–S19 (not S1/S01 — first season is the default)
        // Requires a non-alphanumeric boundary before to avoid matching "S21" episode tags
        (new Regex(@"(?<![A-Za-z0-9])[Ss]0?([2-9]|1[0-9])\b", RegexOptions.Compiled), false, 1),
        // "Part 2", "Part II" treated as season indicator
        (new Regex(@"\bPart\s+(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), false, 1),
        (new Regex(@"\bPart\s+(II|III|IV|V|VI|VII|VIII|IX|X)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), true, 1),
        // "Cour 2"
        (new Regex(@"\bCour\s+(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), false, 1),
        // Roman numerals at end of title, before a tag, or before a colon/dash separator
        // Excludes single-char "I" (too ambiguous) and "V" (too ambiguous)
        (new Regex(@"(?:^|\s)(II|III|IV|VI|VII|VIII|IX|XI|XII)(?:\s*[\[\(:\-]|\s*$)", RegexOptions.Compiled), true, 1),
        // Numeric suffix at very end: "Title 2", "Title 3" (lowest confidence — last resort)
        // Only matches 2–9 at the very end to avoid firing on "Gundam 00"
        (new Regex(@"(?<=\s)([2-9])$", RegexOptions.Compiled), false, 1),
    ];

    private static readonly Dictionary<string, int> RomanNumerals = new(StringComparer.OrdinalIgnoreCase)
    {
        ["II"] = 2, ["III"] = 3, ["IV"] = 4, ["V"] = 5,
        ["VI"] = 6, ["VII"] = 7, ["VIII"] = 8, ["IX"] = 9,
        ["X"] = 10, ["XI"] = 11, ["XII"] = 12
    };

    // Strips to remove when computing base title (only strips numbered indicators)
    private static readonly Regex StripPattern = new(
        @"\s*(?:" +
        @"\d+(?:st|nd|rd|th)\s+Season" +      // "2nd Season", "1st Season"
        @"|Season\s+0?\d{1,2}" +               // "Season 2", "Season 02"
        @"|(?<![A-Za-z0-9])[Ss]0?[2-9]\b" +   // "S2", "S3", "S02"
        @"|(?<![A-Za-z0-9])[Ss]1[0-9]\b" +    // "S10"–"S19"
        @"|Part\s+\d+" +                       // "Part 2"
        @"|Part\s+(?:II|III|IV|V|VI|VII|VIII|IX|X)\b" + // "Part II"
        @"|Cour\s+\d+" +                       // "Cour 2"
        @"|(?<=\s)(?:II|III|IV|VI|VII|VIII|IX|XI|XII)(?=\s*[\[\(:\-]|\s*$)" + // Roman numerals
        @"|(?<=\s)[2-9](?=\s*$)" +             // Trailing single digit
        @")\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Whitespace normalization: collapse multiple spaces to one
    private static readonly Regex MultiSpacePattern =
        new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Returns the season number detected from the title, or 1 if none found.
    /// </summary>
    public static int DetectSeason(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return 1;

        foreach (var (pattern, isRoman, groupIndex) in SeasonPatterns)
        {
            var match = pattern.Match(title);
            if (!match.Success) continue;

            var raw = match.Groups[groupIndex].Value;

            if (isRoman || RomanNumerals.ContainsKey(raw))
            {
                if (RomanNumerals.TryGetValue(raw, out var romanValue))
                    return romanValue;
                continue; // roman flag set but key not found — skip
            }

            if (int.TryParse(raw, out var n) && n >= 1 && n <= 30)
                return n;
        }

        return 1;
    }

    /// <summary>
    /// Strips season qualifiers from a title to produce a clean base title for folder naming.
    /// "Oshi no Ko 2nd Season"            → "Oshi no Ko"
    /// "Attack on Titan Season 3 Part 2"  → "Attack on Titan"
    /// "Re:ZERO Season 2 Part 2"          → "Re:ZERO"
    /// "Overlord IV"                      → "Overlord"
    /// "Sword Art Online S2"              → "Sword Art Online"
    /// "Attack on Titan: The Final Season"→ "Attack on Titan: The Final Season" (no strip — not numbered)
    /// </summary>
    public static string ExtractBaseTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return title;

        var result = StripPattern.Replace(title, " ");

        // Collapse any double-spaces left behind by stripping middle tokens
        result = MultiSpacePattern.Replace(result, " ").Trim();

        // Clean up trailing punctuation artifacts (e.g. trailing dash or colon)
        result = result.TrimEnd(' ', '-', ':', ',').Trim();

        return string.IsNullOrWhiteSpace(result) ? title : result;
    }

    /// <summary>
    /// Returns true if the title contains an explicit <em>numbered</em> season indicator.
    /// Bare "Season" or "Part" alone (without a digit) is NOT considered an indicator
    /// — so "Attack on Titan: The Final Season" returns false.
    /// </summary>
    public static bool HasSeasonIndicator(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;

        // DetectSeason > 1 already covers almost everything; but also catch
        // "1st Season" (returns 1 but IS an explicit indicator) without false-positiving
        // on titles that just happen to contain "Season" or "Part" as English words.
        return DetectSeason(title) > 1
            || Regex.IsMatch(title, @"\b1st\s+Season\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(title, @"\bSeason\s+0?1\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(title, @"(?<![A-Za-z0-9])[Ss]01?\b")
            || Regex.IsMatch(title, @"\bCour\s+1\b", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Returns a display-ready season label, e.g. "Season 2" or "Season 1" if undetected.
    /// </summary>
    public static string GetSeasonLabel(string title) =>
        $"Season {DetectSeason(title)}";
}
