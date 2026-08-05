using System.Net;
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
    private readonly Dictionary<string, DateTime> _knownDevices = new();
    private Timer? _cleanupTimer;

    public event EventHandler<DeviceInfo>? DeviceFound;
    public event EventHandler<string>? DeviceLost;

    public MulticastDiscovery(string multicastAddress, int port)
    {
        _multicastAddress = multicastAddress;
        _port = port;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _client = new UdpClient();
        _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _client.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
        _client.JoinMulticastGroup(IPAddress.Parse(_multicastAddress));
        _cleanupTimer = new Timer(CleanupStaleDevices, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        Task.Run(() => ListenAsync(_cts.Token));
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
            _knownDevices.Remove(fp);
            DeviceLost?.Invoke(this, fp);
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _client?.Close();
        _cleanupTimer?.Dispose();
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _client?.Dispose();
    }
}
