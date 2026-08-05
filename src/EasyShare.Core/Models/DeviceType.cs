using System.Text.Json.Serialization;

namespace EasyShare.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeviceType
{
    Mobile,
    Desktop,
    Web,
    Headless,
    Server
}
