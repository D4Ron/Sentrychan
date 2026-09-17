namespace Sentrychan.Core.Interfaces;

public interface IEpisodeNormalizer
{
    string NormalizeTitle(string title);
    int? ExtractEpisodeNumber(string title);
    string ExtractSourceGroup(string title);
    bool MatchesQuality(string title, string preference);
    bool MatchesTitle(string rssTitle, string targetTitle);
}
