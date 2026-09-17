using System.Text.RegularExpressions;

namespace Sentrychan.Core.Services;

public class EpisodeNormalizer : Interfaces.IEpisodeNormalizer
{
    private static readonly Regex EpisodePatternBracket = new(@"[- ](\d{1,4})(?:\s*v\d+)?\s*[\[\(]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EpisodePatternSxEx = new(@"[Ss](?:eason)?\s*\d+\s*[Ee](\d{1,3})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EpisodePatternRange = new(@"[- ](\d{1,3})-(\d{1,3})", RegexOptions.Compiled); // Multi-part
    private static readonly Regex EpisodePatternENum = new(@"[- ][Ee](\d{1,3})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EpisodePatternWord = new(@"Episode\s*(\d{1,3})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EpisodePatternTrailing = new(@"\s(\d{1,4})\s*$", RegexOptions.Compiled);
    private static readonly Regex GroupTagPattern = new(@"^\[.*?\]\s*", RegexOptions.Compiled);
    private static readonly Regex NonWordPattern = new(@"[^\w\s]", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex Quality1080p = new(@"1080p|1920x1080", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Quality720p = new(@"720p", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Quality480p = new(@"480p", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SourceGroupPattern = new(@"^\[(.*?)\]", RegexOptions.Compiled);

    public string NormalizeTitle(string title)
    {
        title = GroupTagPattern.Replace(title, "");
        title = NonWordPattern.Replace(title, " ");
        title = WhitespacePattern.Replace(title, " ").Trim();
        return title.ToLowerInvariant();
    }

    public int? ExtractEpisodeNumber(string title)
    {
        // Check for range first (e.g. 13-14), take the first one
        var rangeMatch = EpisodePatternRange.Match(title);
        if (rangeMatch.Success && int.TryParse(rangeMatch.Groups[1].Value, out var rangeNum))
            return rangeNum;

        Regex[] patterns =
        [
            EpisodePatternBracket,
            EpisodePatternSxEx,
            EpisodePatternENum,
            EpisodePatternWord,
            EpisodePatternTrailing
        ];

        foreach (var pattern in patterns)
        {
            var match = pattern.Match(title);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var num))
                if (num is >= 0 and <= 2000)
                    return num;
        }

        return null;
    }

    public string ExtractSourceGroup(string title)
    {
        var match = SourceGroupPattern.Match(title);
        return match.Success ? match.Groups[1].Value : "Unknown";
    }

    public bool MatchesQuality(string title, string preference) => preference switch
    {
        "1080p" => Quality1080p.IsMatch(title),
        "720p" => Quality720p.IsMatch(title),
        "480p" => Quality480p.IsMatch(title),
        _ => true
    };

    public bool MatchesTitle(string rssTitle, string targetTitle)
    {
        var rssNorm = NormalizeTitle(rssTitle);
        var targetNorm = NormalizeTitle(targetTitle);

        if (targetNorm.Length == 0) return false;

        // Is it a strict substring match? (Target contains RSS or RSS contains Target)
        bool isSubstringMatch = rssNorm.Contains(targetNorm);

        var rssKeywords = ExtractKeywords(rssNorm);
        var targetKeywords = ExtractKeywords(targetNorm);

        if (targetKeywords.Count == 0) return false;

        // Calculate keyword overlap
        var overlap = rssKeywords.Intersect(targetKeywords).Count();
        double overlapRatio = (double)overlap / targetKeywords.Count;

        // NEW STRICT RULE: 
        // 1. Must have at least 80% keyword overlap
        // 2. Must either be a substring match, or have at least 2 matching keywords
        if (overlapRatio >= 0.8 && (isSubstringMatch || overlap >= 2))
        {
            return true;
        }

        return false;
    }

    private static readonly HashSet<string> StopWords =
    [
        "the", "a", "an", "and", "or", "but", "in", "on", "at", "to",
        "for", "of", "with", "by", "from", "no", "ni", "wo", "ga", "wa",
        "season", "part", "cour", "episode", "ep"
    ];

    private HashSet<string> ExtractKeywords(string normalizedTitle) =>
        normalizedTitle
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !StopWords.Contains(w))
            .ToHashSet();
}
