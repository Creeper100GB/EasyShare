namespace EasyShare.Core.Models;

public record DeviceInfo
{
    public string Alias { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string? DeviceModel { get; init; }
    public DeviceType? DeviceType { get; init; }
    public string Fingerprint { get; init; } = string.Empty;
    public int Port { get; init; }
    public ProtocolType Protocol { get; init; }
    public bool Download { get; init; }
    public string IpAddress { get; init; } = string.Empty;
    public DateTime LastSeen { get; init; }
    public List<string> AllIpAddresses { get; init; } = [];
    public string BluetoothAddress { get; init; } = "";
    public bool HasBluetooth => !string.IsNullOrEmpty(BluetoothAddress);
    public string WifiDirectDeviceId { get; init; } = "";
    public bool HasWifiDirect => !string.IsNullOrEmpty(WifiDirectDeviceId);
}
