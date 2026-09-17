using System.Text.Json.Serialization;

namespace Sentrychan.Core.Models.Api;

public class AnimeTag
{
    [JsonPropertyName("mal_id")]
    public int MalId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}