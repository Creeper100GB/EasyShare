using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EasyShare.Core.Models;

namespace EasyShare.Core.Discovery;

public class MulticastDiscovery : IDisposable
{
    private readonly string _multicastAddress;
    private readonly int _port;
    private UdpClient? _client;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, KnownDevice> _knownDevices = new();
    private Timer? _cleanupTimer;
    private Timer? _announceTimer;
    private DeviceAnnouncement? _self;
    private List<IPAddress> _localAddresses = new();
    private List<(uint Network, uint Mask)> _localSubnets = new();

    private readonly record struct KnownDevice(DateTime LastSeen, string Signature);

    public event EventHandler<DeviceInfo>? DeviceFound;
    public event EventHandler<DeviceInfo>? DeviceSeen;
    public event EventHandler<string>? DeviceLost;

    public MulticastDiscovery(string multicastAddress, int port)
    {
        _multicastAddress = multicastAddress;
        _port = port;
    }

    public void Start(DeviceAnnouncement? self = null)
    {
        _self = self;
        _cts = new CancellationTokenSource();
        _localAddresses = GetLocalIpv4Addresses();
        _localSubnets = GetLocalIpv4Subnets();

        _client = new UdpClient();
        _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _client.Client.Bind(new IPEndPoint(IPAddress.Any, _port));

        var group = IPAddress.Parse(_multicastAddress);
        foreach (var local in _localAddresses)
        {
            try { _client.JoinMulticastGroup(group, local); }
            catch { }
        }

        _cleanupTimer = new Timer(CleanupStaleDevices, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        Task.Run(() => ListenAsync(_cts.Token));

        if (_self is not null)
        {
            Announce();
            _announceTimer = new Timer(_ => Announce(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }
    }

    public static List<IPAddress> GetLocalIpv4Addresses()
    {
        var result = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            const string badName = "veth|vethernet|wsl|docker|vmnet|npcap|loopback|hyper-v|tun|tap|tunnel|vpn|wg|zerotier|tailscale|ppp";
            if (System.Text.RegularExpressions.Regex.IsMatch(nic.Name, badName, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;
                var bytes = ua.Address.GetAddressBytes();
                // Link-local (169.254.x.x, z.B. Thunderbolt/USB-Bridge-Kabel) erlauben,
                // nur "normales" APIPA ohne Netz verwerfen.
                if (bytes[0] == 169 && bytes[1] != 254) continue;

                result.Add(ua.Address);
            }
        }
        return result;
    }

    private static List<(uint Network, uint Mask)> GetLocalIpv4Subnets()
    {
        var result = new List<(uint, uint)>();
        const string badName = "veth|vethernet|wsl|docker|vmnet|npcap|loopback|hyper-v|tun|tap|tunnel|vpn|wg|zerotier|tailscale|ppp";
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (System.Text.RegularExpressions.Regex.IsMatch(nic.Name, badName, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var prefix = ua.PrefixLength;
                if (prefix is < 1 or > 32) continue;

                var addr = ToUInt32(ua.Address.GetAddressBytes());
                var mask = prefix == 32 ? uint.MaxValue : (uint.MaxValue << (32 - prefix));
                result.Add((addr & mask, mask));
            }
        }
        return result;
    }

    private static uint ToUInt32(byte[] bytes) =>
        ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];

    private bool IsOnLocalSubnet(IPAddress address)
    {
        if (_localSubnets.Count == 0) return false;
        var addr = ToUInt32(address.GetAddressBytes());
        foreach (var (network, mask) in _localSubnets)
        {
            if ((addr & mask) == network) return true;
        }
        return false;
    }

    private string ResolveAnnouncedIp(string? announced, string sourceIp)
    {
        if (!string.IsNullOrEmpty(announced)
            && IPAddress.TryParse(announced, out var parsed)
            && (IPAddress.IsLoopback(parsed) || IsOnLocalSubnet(parsed) || _localSubnets.Count == 0))
        {
            return announced;
        }

        return sourceIp;
    }

    public void UpdateSelf(DeviceAnnouncement self)
    {
        _self = self;
    }

    private void Announce()
    {
        try
        {
            var self = _self;
            if (self is null || _client is null) return;
            var json = JsonSerializer.Serialize(self);
            var bytes = Encoding.UTF8.GetBytes(json);
            var group = new IPEndPoint(IPAddress.Parse(_multicastAddress), _port);

            if (_localAddresses.Count == 0)
            {
                _client.Send(bytes, bytes.Length, group);
                return;
            }

            foreach (var local in _localAddresses)
            {
                try
                {
                    var announce = new DeviceAnnouncement
                    {
                        Alias = self.Alias,
                        Version = self.Version,
                        DeviceModel = self.DeviceModel,
                        DeviceType = self.DeviceType,
                        Fingerprint = self.Fingerprint,
                        Port = self.Port,
                        Protocol = self.Protocol,
                        Download = self.Download,
                        Announce = self.Announce,
                        Ip = local.ToString(),
                    };
                    _client.Client.SetSocketOption(
                        SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                    var announceBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(announce));
                    _client.Send(announceBytes, announceBytes.Length, group);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _client!.ReceiveAsync().WaitAsync(ct);
                var json = Encoding.UTF8.GetString(result.Buffer);
                var announcement = JsonSerializer.Deserialize<DeviceAnnouncement>(json);
                if (announcement is null || string.IsNullOrEmpty(announcement.Fingerprint))
                    continue;

                // Eigene Ankündigung ignorieren (multicast loopback)
                if (_self is not null && string.Equals(announcement.Fingerprint, _self.Fingerprint, StringComparison.OrdinalIgnoreCase))
                    continue;

                var now = DateTime.UtcNow;
                var sourceIp = result.RemoteEndPoint.Address?.ToString() ?? "";
                var announcedIp = ResolveAnnouncedIp(announcement.Ip, sourceIp);
                var signature = $"{announcement.Alias}|{announcement.Port}|{announcement.Version}|{announcement.DeviceModel}|{announcement.DeviceType}|{announcement.Protocol}|{announcement.Download}|{announcedIp}";
                var info = new DeviceInfo
                {
                    Alias = announcement.Alias,
                    Version = announcement.Version,
                    DeviceModel = announcement.DeviceModel,
                    DeviceType = announcement.DeviceType,
                    Fingerprint = announcement.Fingerprint,
                    Port = announcement.Port,
                    Protocol = announcement.Protocol,
                    Download = announcement.Download,
                    IpAddress = announcedIp,
                    LastSeen = now,
                };

                var isNewOrChanged = false;
                if (_knownDevices.TryGetValue(announcement.Fingerprint, out var existing))
                {
                    if (existing.Signature != signature)
                    {
                        _knownDevices[announcement.Fingerprint] = new KnownDevice(now, signature);
                        isNewOrChanged = true;
                    }
                    else
                    {
                        _knownDevices[announcement.Fingerprint] = existing with { LastSeen = now };
                    }
                }
                else
                {
                    _knownDevices[announcement.Fingerprint] = new KnownDevice(now, signature);
                    isNewOrChanged = true;
                }

                DeviceSeen?.Invoke(this, info);
                if (isNewOrChanged)
                    DeviceFound?.Invoke(this, info);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }
        }
    }

    private void CleanupStaleDevices(object? state)
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-60);
        var stale = _knownDevices
            .Where(kv => kv.Value.LastSeen < cutoff)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var fp in stale)
        {
            _knownDevices.TryRemove(fp, out _);
            DeviceLost?.Invoke(this, fp);
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _client?.Close();
        _cleanupTimer?.Dispose();
        _announceTimer?.Dispose();
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _client?.Dispose();
    }
}
