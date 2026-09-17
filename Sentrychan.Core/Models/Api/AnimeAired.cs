using System.Text.Json.Serialization;

namespace Sentrychan.Core.Models.Api;

public class AnimeAired
{
    [JsonPropertyName("from")]
    public string? From { get; set; }

    [JsonPropertyName("to")]
    public string? To { get; set; }

    public string Formatted
    {
        get
        {
            var start = From?.Split('T')[0] ?? "?";
            var end = To?.Split('T')[0];
            return end != null ? $"{start} to {end}" : start;
        }
    }
}