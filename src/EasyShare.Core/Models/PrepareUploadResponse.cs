using System.Text.Json.Serialization;

namespace EasyShare.Core.Models;

public class PrepareUploadResponse
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public Dictionary<string, string> Files { get; set; } = new();
}
