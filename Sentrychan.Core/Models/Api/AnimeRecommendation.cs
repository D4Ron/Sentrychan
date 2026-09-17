using System.Text.Json.Serialization;

namespace Sentrychan.Core.Models.Api;

public class AnimeRecommendation
{
    [JsonPropertyName("entry")]
    public AnimeResult Entry { get; set; } = new();

    [JsonPropertyName("votes")]
    public int Votes { get; set; }
}