using System.Text.Json.Serialization;

namespace EasyShare.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProtocolType
{
    Http,
    Https
}
