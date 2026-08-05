namespace EasyShare.Core.Models;

public class TransferSession
{
    public string SessionId { get; set; } = string.Empty;
    public DeviceInfo TargetDevice { get; set; } = new();
    public List<FileEntry> Files { get; set; } = new();
    public Dictionary<string, string> FileTokens { get; set; } = new();
    public TransferDirection Direction { get; set; }
    public TransferStatus Status { get; set; }
    public DateTime StartedAt { get; set; }
    public double? BytesPerSecond { get; set; }
    public long BytesTransferred { get; set; }
}
