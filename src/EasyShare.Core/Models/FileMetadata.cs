using System.Text.Json.Serialization;

namespace EasyShare.Core.Models;

public record FileMetadata
{
    [JsonPropertyName("modified")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? Modified { get; init; }

    [JsonPropertyName("accessed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? Accessed { get; init; }
}
