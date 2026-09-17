using System.Text.Json.Serialization;

namespace Sentrychan.Core.Models.Api;

public class AnimeRelation
{
    [JsonPropertyName("relation")]
    public string Relation { get; set; } = string.Empty;

    [JsonPropertyName("entry")]
    public List<RelationEntry> Entry { get; set; } = [];
}

public class RelationEntry
{
    [JsonPropertyName("mal_id")]
    public int MalId { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}