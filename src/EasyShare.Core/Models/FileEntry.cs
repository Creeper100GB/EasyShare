using System.Text.Json.Serialization;

namespace EasyShare.Core.Models;

public record FileEntry
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonIgnore]
    public string? LocalFilePath { get; init; }

    [JsonPropertyName("fileType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileType { get; init; }

    [JsonPropertyName("sha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha256 { get; init; }

    [JsonPropertyName("preview")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Preview { get; init; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FileMetadata? Metadata { get; init; }
}
