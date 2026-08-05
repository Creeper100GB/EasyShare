using System.Text.Json.Serialization;
using EasyShare.Core.Models;

namespace EasyShare.Core.Config;

public class AppConfig
{
    [JsonPropertyName("deviceAlias")]
    public string DeviceAlias { get; set; } = Environment.MachineName;

    [JsonPropertyName("deviceModel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceModel { get; set; }

    [JsonPropertyName("httpPort")]
    public int HttpPort { get; set; } = 53317;

    [JsonPropertyName("multicastAddress")]
    public string MulticastAddress { get; set; } = "224.0.0.167";

    [JsonPropertyName("multicastPort")]
    public int MulticastPort { get; set; } = 53317;

    [JsonPropertyName("pinMode")]
    public PinMode PinMode { get; set; } = PinMode.Optional;

    [JsonPropertyName("defaultSavePath")]
    public string DefaultSavePath { get; set; } = string.Empty;

    [JsonPropertyName("autoAcceptTrusted")]
    public bool AutoAcceptTrusted { get; set; }

    [JsonPropertyName("autoStart")]
    public bool AutoStart { get; set; }

    [JsonPropertyName("theme")]
    public Theme Theme { get; set; } = Theme.Auto;

    [JsonPropertyName("language")]
    public string Language { get; set; } = "de";

    [JsonPropertyName("speedLimitBytesPerSecond")]
    public int SpeedLimitBytesPerSecond { get; set; }
}
