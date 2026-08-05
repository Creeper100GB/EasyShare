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
    private readonly ConcurrentDictionary<string, DateTime> _knownDevices = new();
    private Timer? _cleanupTimer;
    private Timer? _announceTimer;
    private DeviceAnnouncement? _self;
    private List<IPAddress> _localAddresses = new();

    public event EventHandler<DeviceInfo>? DeviceFound;
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

    private static List<IPAddress> GetLocalIpv4Addresses()
    {
        var result = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            const string badName = "veth|vethernet|wsl|docker|vmnet|npcap|loopback|hyper-v";
            if (System.Text.RegularExpressions.Regex.IsMatch(nic.Name, badName, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;
                if (ua.Address.GetAddressBytes()[0] == 169) continue; // APIPA / link-local ohne Netz

                result.Add(ua.Address);
            }
        }
        return result;
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
                    _client.Client.SetSocketOption(
                        SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                    _client.Send(bytes, bytes.Length, group);
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

                _knownDevices[announcement.Fingerprint] = DateTime.UtcNow;

                DeviceFound?.Invoke(this, new DeviceInfo
                {
                    Alias = announcement.Alias,
                    Version = announcement.Version,
                    DeviceModel = announcement.DeviceModel,
                    DeviceType = announcement.DeviceType,
                    Fingerprint = announcement.Fingerprint,
                    Port = announcement.Port,
                    Protocol = announcement.Protocol,
                    Download = announcement.Download,
                    IpAddress = result.RemoteEndPoint.Address.ToString(),
                    LastSeen = DateTime.UtcNow,
                });
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
            .Where(kv => kv.Value < cutoff)
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
