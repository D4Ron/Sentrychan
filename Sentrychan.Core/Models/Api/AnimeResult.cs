using System.Text.Json.Serialization;

namespace Sentrychan.Core.Models.Api;

public class AnimeResult
{
    [JsonPropertyName("mal_id")]
    public int MalId { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("title_english")]
    public string? TitleEnglish { get; set; }

    [JsonPropertyName("title_japanese")]
    public string? TitleJapanese { get; set; }

    [JsonPropertyName("titles")]
    public List<AnimeTitle> Titles { get; set; } = [];

    [JsonPropertyName("images")]
    public AnimeImages Images { get; set; } = new();

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("episodes")]
    public int? Episodes { get; set; }

    [JsonPropertyName("score")]
    public double? Score { get; set; }

    [JsonPropertyName("scored_by")]
    public int? ScoredBy { get; set; }

    [JsonPropertyName("rank")]
    public int? Rank { get; set; }

    [JsonPropertyName("popularity")]
    public int? Popularity { get; set; }

    [JsonPropertyName("members")]
    public int Members { get; set; }

    [JsonPropertyName("synopsis")]
    public string? Synopsis { get; set; }

    [JsonPropertyName("background")]
    public string? Background { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("season")]
    public string? Season { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("rating")]
    public string? Rating { get; set; }

    [JsonPropertyName("genres")]
    public List<AnimeTag> Genres { get; set; } = [];

    [JsonPropertyName("themes")]
    public List<AnimeTag> Themes { get; set; } = [];

    [JsonPropertyName("studios")]
    public List<AnimeTag> Studios { get; set; } = [];

    [JsonPropertyName("aired")]
    public AnimeAired? Aired { get; set; }

    [JsonPropertyName("broadcast")]
    public AnimeBroadcast? Broadcast { get; set; }

    [JsonPropertyName("relations")]
    public List<AnimeRelation> Relations { get; set; } = [];

    // Computed — not from API
    public string DisplayTitle => TitleEnglish ?? Title;
    public string LargeImageUrl => Images.Jpg.LargeImageUrl ?? Images.Jpg.ImageUrl ?? string.Empty;
}