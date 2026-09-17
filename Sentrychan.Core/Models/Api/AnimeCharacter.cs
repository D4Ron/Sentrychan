using System.Text.Json.Serialization;

namespace Sentrychan.Core.Models.Api;

public class AnimeCharacter
{
    [JsonPropertyName("character")]
    public CharacterEntry Character { get; set; } = new();

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("voice_actors")]
    public List<VoiceActor> VoiceActors { get; set; } = [];
}

public class CharacterEntry
{
    [JsonPropertyName("mal_id")]
    public int MalId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("images")]
    public AnimeImages Images { get; set; } = new();
}

public class VoiceActor
{
    [JsonPropertyName("language")]
    public string Language { get; set; } = string.Empty;

    [JsonPropertyName("person")]
    public VoiceActorPerson Person { get; set; } = new();
}

public class VoiceActorPerson
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}