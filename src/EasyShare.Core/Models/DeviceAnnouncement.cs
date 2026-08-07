using System.Text.Json.Serialization;

namespace EasyShare.Core.Models;

public class DeviceAnnouncement
{
    [JsonPropertyName("alias")]
    public string Alias { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = "2.0";

    [JsonPropertyName("deviceModel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceModel { get; set; }

    [JsonPropertyName("deviceType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeviceType? DeviceType { get; set; }

    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("protocol")]
    public ProtocolType Protocol { get; set; }

    [JsonPropertyName("download")]
    public bool Download { get; set; }

    [JsonPropertyName("announce")]
    public bool Announce { get; set; }

    [JsonPropertyName("ip")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ip { get; set; }
}
