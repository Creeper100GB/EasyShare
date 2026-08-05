using System.Text.Json.Serialization;

namespace EasyShare.Core.Models;

public class PrepareDownloadResponse
{
    [JsonPropertyName("info")]
    public DeviceAnnouncement Info { get; set; } = new();

    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public Dictionary<string, FileEntry> Files { get; set; } = new();
}
