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
    private static readonly Regex Spaces       = new(@"\s{2,}", RegexOptions.Compiled);

    public static (string Title, int? Year) Clean(string folderName)
    {
        var s = folderName;
        int? year = null;
        var y = YearParen.Match(s);
        if (y.Success) year = int.Parse(y.Value.Trim('(', ')'));

        s = Brackets.Replace(s, " ");
        s = YearParen.Replace(s, " ");
        s = OtherParen.Replace(s, " ");
        // Dots and underscores as word separators ("Show.Name.S01") — but keep a title's own
        // punctuation elsewhere intact.
        if (!s.Contains(' ')) s = Separators.Replace(s, " ");
        s = Range.Replace(s, " ");
        s = Version.Replace(s, " ");
        s = Noise.Replace(s, " ");
        s = Spaces.Replace(s, " ").Trim(' ', '-', '–', '_', '.', ',');

        return (s.Length > 0 ? s : folderName.Trim(), year);
    }
}
