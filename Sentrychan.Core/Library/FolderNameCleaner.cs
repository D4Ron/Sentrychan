using System.Text.RegularExpressions;

namespace Sentrychan.Core.Library;

/// <summary>
/// Turns a downloaded folder name into a show title: "[Group] Show (2023) [BD 1080p x265] 01-24 v2"
/// → "Show", year 2023. Only a fallback — a series the app tracks is named from its own record.
/// </summary>
public static class FolderNameCleaner
{
    private static readonly Regex Brackets     = new(@"\[[^\]]*\]|\{[^}]*\}", RegexOptions.Compiled);
    private static readonly Regex YearParen    = new(@"\((19|20)\d{2}\)", RegexOptions.Compiled);
    private static readonly Regex OtherParen   = new(@"\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Range        = new(@"(?<![A-Za-z0-9])(?:E|EP)?\d{1,4}\s*[-~]\s*(?:E|EP)?\d{1,4}(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Version      = new(@"(?<![A-Za-z0-9])v\d(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Noise        = new(
        @"(?<![A-Za-z0-9])(?:\d{3,4}p|\d{3,4}x\d{3,4}|4K|UHD|BD|BDRip|BD-?Rip|Blu-?Ray|WEB(?:-?DL|-?Rip)?|HDTV|DVD(?:Rip)?|" +
        @"HEVC|AVC|x26[45]|H\.?26[45]|10-?bit|8-?bit|Hi10P?|FLAC|AAC(?:2\.0)?|AC3|DTS|Opus|Dual[ .-]?Audio|Multi[ .-]?Subs?|" +
        @"Batch|Complete(?:\s+Series)?|Uncensored|Remux)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Separators   = new(@"[._]+", RegexOptions.Compiled);
    private static readonly Regex InnerSeparator = new(@"(?<=\S)[._]+(?=\S)", RegexOptions.Compiled);
    private static readonly Regex Spaces       = new(@"\s{2,}", RegexOptions.Compiled);

    // Scene names put the title first and everything after it is release detail:
    // "Show.Name.2021.S02.1080p.NF.WEB-DL.DDP5.1.H.264-GRP". The title ends at the first of these.
    private static readonly Regex SceneTitleEnd = new(
        @"(?<=\s)(?:S\d{1,2}(?:E\d{1,4})?|E\d{1,4}|(?:19|20)\d{2}|\d{3,4}p|REPACK|PROPER|COMPLETE|" +
        @"NF|AMZN|DSNP|CR|ADN|HMAX|HULU|ATVP|WEB(?:-?DL|-?Rip)?|Blu-?Ray|BD(?:-?Rip)?|HDTV|x26[45]|H\s26[45])(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SpacedSeasonTag = new(@"(?<=\S\s+)S\d{2}(?:E\d{1,4})?\s+\S", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static (string Title, int? Year) Clean(string folderName)
    {
        var s = folderName;
        int? year = null;
        var y = YearParen.Match(s);
        if (y.Success) year = int.Parse(y.Value.Trim('(', ')'));

        s = Brackets.Replace(s, " ");
        s = YearParen.Replace(s, " ");
        s = OtherParen.Replace(s, " ").Trim();

        // Dots and underscores as word separators ("Show.Name.S01") — but keep a title's own
        // punctuation elsewhere intact ("Dr. Stone", "D.Gray-man"). Several joins mean a scene name,
        // even after a bracketed tag left a space behind.
        if (!s.Contains(' ') || InnerSeparator.Matches(s).Count >= 2)
        {
            s = Separators.Replace(s, " ");
            var end = SceneTitleEnd.Match(s);
            if (end.Success)
            {
                if (year == null && Regex.IsMatch(end.Value, @"^(19|20)\d{2}$")) year = int.Parse(end.Value);
                s = s[..end.Index];
            }
        }
        else
        {
            // The same shape with spaces ("Show Name S01 1080p WEB-DL-GRP"): a two-digit season tag
            // with more after it ends the title. A bare trailing "Show S2" is left for the season logic.
            var tag = SpacedSeasonTag.Match(s);
            if (tag.Success) s = s[..tag.Index];
        }
        s = Range.Replace(s, " ");
        s = Version.Replace(s, " ");
        s = Noise.Replace(s, " ");
        s = Spaces.Replace(s, " ").Trim(' ', '-', '–', '_', '.', ',');

        return (s.Length > 0 ? s : folderName.Trim(), year);
    }
}
