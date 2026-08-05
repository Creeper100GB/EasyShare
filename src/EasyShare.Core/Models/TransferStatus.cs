using System.Text.Json.Serialization;

namespace EasyShare.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TransferStatus
{
    Pending,
    Active,
    Completed,
    Cancelled,
    Failed,
    Rejected
}
