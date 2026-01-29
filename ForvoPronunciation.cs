using System.Text.Json.Serialization;

namespace russian_flashcard_api;

public class ForvoPronunciation
{
    [JsonPropertyName("audioMp3")]
    public string AudioMp3 { get; set; } = string.Empty;

    [JsonPropertyName("sex")]
    public string Sex { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("country")]
    public string Country { get; set; } = string.Empty;
}
