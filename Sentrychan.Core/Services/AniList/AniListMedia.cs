using System.Text.Json.Serialization;

namespace Sentrychan.Core.Services.AniList;

/// <summary>An anime as AniList's GraphQL API describes it (only the fields the app asks for).</summary>
public sealed class AniListMedia
{
    [JsonPropertyName("id")] public int Id { get; set; }
    /// <summary>AniList's MyAnimeList id. Not always right: it gives a split show's later parts the first part's id.</summary>
    [JsonPropertyName("idMal")] public int? IdMal { get; set; }
    [JsonPropertyName("isAdult")] public bool IsAdult { get; set; }
    [JsonPropertyName("title")] public AniListTitle Title { get; set; } = new();
    [JsonPropertyName("synonyms")] public List<string>? Synonyms { get; set; }
    /// <summary>TV, TV_SHORT, MOVIE, SPECIAL, OVA, ONA, MUSIC.</summary>
    [JsonPropertyName("format")] public string? Format { get; set; }
    /// <summary>FINISHED, RELEASING, NOT_YET_RELEASED, CANCELLED, HIATUS.</summary>
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("episodes")] public int? Episodes { get; set; }
    [JsonPropertyName("duration")] public int? Duration { get; set; }
    /// <summary>WINTER, SPRING, SUMMER, FALL.</summary>
    [JsonPropertyName("season")] public string? Season { get; set; }
    [JsonPropertyName("seasonYear")] public int? SeasonYear { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("startDate")] public AniListDate? StartDate { get; set; }
    [JsonPropertyName("endDate")] public AniListDate? EndDate { get; set; }
    [JsonPropertyName("coverImage")] public AniListCover? CoverImage { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("averageScore")] public int? AverageScore { get; set; }
    [JsonPropertyName("popularity")] public int? Popularity { get; set; }
    /// <summary>JP, CN, KR…</summary>
    [JsonPropertyName("countryOfOrigin")] public string? CountryOfOrigin { get; set; }
    [JsonPropertyName("genres")] public List<string>? Genres { get; set; }
    [JsonPropertyName("studios")] public AniListStudios? Studios { get; set; }
    [JsonPropertyName("nextAiringEpisode")] public AniListAiring? NextAiringEpisode { get; set; }
    [JsonPropertyName("relations")] public AniListRelations? Relations { get; set; }

    /// <summary>Every name it goes by: romaji, English, native, then the synonyms.</summary>
    public IEnumerable<string> AllTitles()
    {
        foreach (var t in new[] { Title.Romaji, Title.English, Title.Native })
            if (!string.IsNullOrWhiteSpace(t)) yield return t;
        foreach (var s in Synonyms ?? [])
            if (!string.IsNullOrWhiteSpace(s)) yield return s;
    }

    public int? Year => SeasonYear ?? StartDate?.Year;
}

public sealed class AniListTitle
{
    [JsonPropertyName("romaji")] public string? Romaji { get; set; }
    [JsonPropertyName("english")] public string? English { get; set; }
    [JsonPropertyName("native")] public string? Native { get; set; }
}

public sealed class AniListDate
{
    [JsonPropertyName("year")] public int? Year { get; set; }
    [JsonPropertyName("month")] public int? Month { get; set; }
    [JsonPropertyName("day")] public int? Day { get; set; }

    public DateTime? ToDate() => Year is { } y ? new DateTime(y, Month is >= 1 and <= 12 ? Month.Value : 1, 1).AddDays((Day ?? 1) - 1) : null;
}

public sealed class AniListCover
{
    [JsonPropertyName("extraLarge")] public string? ExtraLarge { get; set; }
    [JsonPropertyName("large")] public string? Large { get; set; }
}

public sealed class AniListStudios
{
    [JsonPropertyName("nodes")] public List<AniListNamed>? Nodes { get; set; }
}

public sealed class AniListNamed
{
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class AniListAiring
{
    [JsonPropertyName("episode")] public int Episode { get; set; }
    /// <summary>Unix seconds.</summary>
    [JsonPropertyName("airingAt")] public long AiringAt { get; set; }
}

public sealed class AniListRelations
{
    [JsonPropertyName("edges")] public List<AniListRelationEdge>? Edges { get; set; }
}

public sealed class AniListRelationEdge
{
    /// <summary>SEQUEL, PREQUEL, SIDE_STORY, PARENT, ALTERNATIVE, SPIN_OFF, SUMMARY, ADAPTATION, OTHER…</summary>
    [JsonPropertyName("relationType")] public string? RelationType { get; set; }
    [JsonPropertyName("node")] public AniListRelationNode? Node { get; set; }
}

public sealed class AniListRelationNode
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("idMal")] public int? IdMal { get; set; }
    /// <summary>ANIME or MANGA.</summary>
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("format")] public string? Format { get; set; }
}

/// <summary>One scheduled airing: which episode, when, of what.</summary>
public sealed class AniListAiringSchedule
{
    [JsonPropertyName("episode")] public int Episode { get; set; }
    [JsonPropertyName("airingAt")] public long AiringAt { get; set; }
    [JsonPropertyName("media")] public AniListMedia? Media { get; set; }
}
