using System.Text.Json.Serialization;

namespace EasyShare.Core.Models;

public class CancelUploadRequest
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;
}