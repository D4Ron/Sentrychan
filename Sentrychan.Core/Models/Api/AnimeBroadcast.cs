using System.Text.Json.Serialization;

namespace Sentrychan.Core.Models.Api;

public class AnimeBroadcast
{
    [JsonPropertyName("day")]
    public string? Day { get; set; }

    [JsonPropertyName("time")]
    public string? Time { get; set; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    public string Formatted => Day != null
        ? $"{Day}{(Time != null ? $" at {Time}" : "")} JST"
        : "TBA";
}