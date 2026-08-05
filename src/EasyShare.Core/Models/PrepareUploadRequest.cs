using System.Text.Json.Serialization;

namespace EasyShare.Core.Models;

public class PrepareUploadRequest
{
    [JsonPropertyName("info")]
    public DeviceAnnouncement Info { get; set; } = new();

    [JsonPropertyName("files")]
    public Dictionary<string, FileEntry> Files { get; set; } = new();
}
