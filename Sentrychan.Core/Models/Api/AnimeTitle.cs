using System.Text.Json.Serialization;

namespace Sentrychan.Core.Models.Api;

public class AnimeTitle
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
}