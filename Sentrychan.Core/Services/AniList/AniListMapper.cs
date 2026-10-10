using System.Net;
using System.Text.RegularExpressions;
using Sentrychan.Core.Models.Api;

namespace Sentrychan.Core.Services.AniList;

/// <summary>
/// AniList's shapes and words in the ones the rest of the app already uses: Jikan's
/// <see cref="AnimeResult"/> for details and search, the offline database's vocabulary for the
/// title index.
/// </summary>
public static partial class AniListMapper
{
    /// <summary>A show as the app's screens read it. <paramref name="malId"/> is its checked MyAnimeList id.</summary>
    public static AnimeResult ToAnimeResult(AniListMedia m, int malId, Func<AniListRelationNode, int?>? relationMal = null)
    {
        var titles = new List<AnimeTitle> { new() { Type = "Default", Title = m.Title.Romaji ?? m.Title.English ?? "" } };
        if (m.Title.English is { Length: > 0 } en) titles.Add(new() { Type = "English", Title = en });
        if (m.Title.Native is { Length: > 0 } jp) titles.Add(new() { Type = "Japanese", Title = jp });
        foreach (var s in m.Synonyms ?? []) titles.Add(new() { Type = "Synonym", Title = s });

        var cover = m.CoverImage?.ExtraLarge ?? m.CoverImage?.Large;
        return new AnimeResult
        {
            MalId = malId,
            Title = m.Title.Romaji ?? m.Title.English ?? m.Title.Native ?? "",
            TitleEnglish = m.Title.English,
            TitleJapanese = m.Title.Native,
            Titles = titles,
            Images = new AnimeImages { Jpg = new AnimeImageSet { ImageUrl = m.CoverImage?.Large ?? cover, LargeImageUrl = cover } },
            Status = JikanStatus(m.Status),
            Episodes = m.Episodes is > 0 ? m.Episodes : null,
            Score = m.AverageScore is > 0 ? m.AverageScore / 10.0 : null,
            Members = m.Popularity ?? 0,
            Synopsis = PlainText(m.Description),
            Year = m.Year,
            Season = m.Season?.ToLowerInvariant(),
            Source = SourceName(m.Source),
            Type = JikanType(m.Format),
            Rating = m.IsAdult ? "Rx - Hentai" : null,
            Genres = (m.Genres ?? []).Select(g => new AnimeTag { Name = g }).ToList(),
            Studios = (m.Studios?.Nodes ?? []).Where(s => s.Name != null).Select(s => new AnimeTag { Name = s.Name! }).ToList(),
            Aired = new AnimeAired { From = Iso(m.StartDate), To = Iso(m.EndDate) },
            Broadcast = Broadcast(m.NextAiringEpisode),
            Relations = Relations(m, relationMal),
        };
    }

    /// <summary>The status words Jikan uses ("Currently Airing"…), which the library stores.</summary>
    public static string? JikanStatus(string? status) => status switch
    {
        "RELEASING" or "HIATUS" => AiringStatusNormalizer.Airing,
        "FINISHED" => AiringStatusNormalizer.Finished,
        "NOT_YET_RELEASED" => AiringStatusNormalizer.NotYetAired,
        // A cancelled show stopped where it stopped; nothing more is coming.
        "CANCELLED" => AiringStatusNormalizer.Finished,
        _ => null,
    };

    /// <summary>The offline database's status words, which the matcher reads (only FINISHED lengths are trusted).</summary>
    public static string CatalogStatus(string? status) => status switch
    {
        "RELEASING" or "HIATUS" => "ONGOING",
        "FINISHED" => "FINISHED",
        "NOT_YET_RELEASED" => "UPCOMING",
        // Its planned length was never reached: not a length to count on.
        _ => "UNKNOWN",
    };

    /// <summary>The offline database's kinds: TV, ONA, OVA, MOVIE, SPECIAL, UNKNOWN.</summary>
    public static string CatalogType(string? format) => format switch
    {
        "TV" or "TV_SHORT" => "TV",
        "ONA" => "ONA",
        "OVA" => "OVA",
        "MOVIE" => "MOVIE",
        "SPECIAL" => "SPECIAL",
        _ => "UNKNOWN",
    };

    public static string? JikanType(string? format) => format switch
    {
        "TV" or "TV_SHORT" => "TV",
        "ONA" => "ONA",
        "OVA" => "OVA",
        "MOVIE" => "Movie",
        "SPECIAL" => "Special",
        "MUSIC" => "Music",
        _ => null,
    };

    private static string? SourceName(string? source) => source switch
    {
        null => null,
        "MANGA" => "Manga",
        "LIGHT_NOVEL" => "Light novel",
        "WEB_NOVEL" => "Web novel",
        "NOVEL" => "Novel",
        "ORIGINAL" => "Original",
        "VISUAL_NOVEL" => "Visual novel",
        "VIDEO_GAME" => "Game",
        _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(source.Replace('_', ' ').ToLowerInvariant()),
    };

    private static string? Iso(AniListDate? date) =>
        date?.ToDate() is { } d ? new DateTimeOffset(d, TimeSpan.Zero).ToString("yyyy-MM-ddTHH:mm:sszzz") : null;

    /// <summary>The next airing's weekday and time in JST, as MAL states a broadcast ("Thursdays", "23:00").</summary>
    private static AnimeBroadcast? Broadcast(AniListAiring? next)
    {
        if (next == null) return null;
        var jst = DateTimeOffset.FromUnixTimeSeconds(next.AiringAt).ToOffset(TimeSpan.FromHours(9));
        return new AnimeBroadcast { Day = jst.DayOfWeek + "s", Time = jst.ToString("HH:mm"), Timezone = "Asia/Tokyo" };
    }

    private static List<AnimeRelation> Relations(AniListMedia m, Func<AniListRelationNode, int?>? relationMal) =>
        (m.Relations?.Edges ?? [])
            .Where(e => e.Node != null && e.RelationType != null)
            .Select(e => (Relation: RelationName(e.RelationType!), Node: e.Node!, Mal: relationMal?.Invoke(e.Node!) ?? e.Node!.IdMal))
            .Where(x => x.Mal is > 0)
            .GroupBy(x => x.Relation)
            .Select(g => new AnimeRelation
            {
                Relation = g.Key,
                Entry = g.Select(x => new RelationEntry { MalId = x.Mal!.Value, Type = x.Node.Type?.ToLowerInvariant() ?? "anime" }).ToList(),
            })
            .ToList();

    /// <summary>Jikan's relation names, which the season-family walk looks for ("Sequel", "Prequel").</summary>
    public static string RelationName(string relationType) => relationType switch
    {
        "SEQUEL" => "Sequel",
        "PREQUEL" => "Prequel",
        "SIDE_STORY" => "Side Story",
        "PARENT" => "Parent Story",
        "ALTERNATIVE" => "Alternative Version",
        "SPIN_OFF" => "Spin-Off",
        "SUMMARY" => "Summary",
        "ADAPTATION" or "SOURCE" => "Adaptation",
        "CHARACTER" => "Character",
        _ => "Other",
    };

    /// <summary>AniList's descriptions carry a little HTML (<c>&lt;br&gt;</c>, <c>&lt;i&gt;</c>) even as plain text.</summary>
    public static string? PlainText(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var text = BreakPattern().Replace(description, "\n");
        text = TagPattern().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        return BlankLines().Replace(text, "\n\n").Trim();
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)] private static partial Regex BreakPattern();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex TagPattern();
    [GeneratedRegex(@"\n{3,}")] private static partial Regex BlankLines();
}
