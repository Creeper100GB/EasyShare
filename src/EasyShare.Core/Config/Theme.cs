using System.Text.Json.Serialization;

namespace EasyShare.Core.Config;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Theme
{
    Light,
    Dark,
    Auto
}
