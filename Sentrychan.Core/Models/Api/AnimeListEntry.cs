using System.Text.Json.Serialization;

namespace Sentrychan.Core.Models.Api;

public class AnimeListEntry
{
    [JsonPropertyName("anime")]
    public AnimeResult Anime { get; set; } = new();

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("score")]
    public int Score { get; set; }

    [JsonPropertyName("num_episodes_watched")]
    public int WatchedEpisodes { get; set; }
}
