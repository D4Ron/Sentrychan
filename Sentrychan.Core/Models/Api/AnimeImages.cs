using System.Text.Json.Serialization;

namespace Sentrychan.Core.Models.Api;

public class AnimeImages
{
    [JsonPropertyName("jpg")]
    public AnimeImageSet Jpg { get; set; } = new();
}

public class AnimeImageSet
{
    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("large_image_url")]
    public string? LargeImageUrl { get; set; }
}