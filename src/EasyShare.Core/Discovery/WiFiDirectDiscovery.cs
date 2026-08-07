using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;
using Windows.Security.Cryptography;
using EasyShare.Core.Models;

namespace EasyShare.Core.Discovery;

public class WiFiDirectDiscovery : IDisposable
{
    public const string ServiceName = "EasyShare";
    private const byte ElementId = 0xDD;
    private const string Magic = "ESWF";

    private WiFiDirectAdvertisementPublisher? _publisher;
    private WiFiDirectConnectionListener? _listener;
    private Timer? _scanTimer;
    private readonly ConcurrentDictionary<string, KnownWfdDevice> _knownDevices = new();
    private string _selfFingerprint = "";
    private string _alias = "EasyShare";

    private readonly record struct KnownWfdDevice(DateTime LastSeen, string Alias, int Port, string DeviceId);

    public bool IsSupported { get; private set; }
    public bool IsListening { get; private set; }

    public event EventHandler<DeviceInfo>? DeviceFound;
    public event EventHandler<DeviceInfo>? DeviceSeen;

    public void Start(DeviceAnnouncement? self = null)
    {
        try
        {
            if (self is not null)
            {
                _selfFingerprint = self.Fingerprint;
                _alias = self.Alias;
            }

            StartPublisher(self);
            StartListener();
            IsSupported = _publisher?.Status != WiFiDirectAdvertisementPublisherStatus.Aborted || IsListening;
            if (!IsSupported) return;

            _scanTimer = new Timer(_ => _ = ScanAsync(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10));
        }
        catch
        {
            IsSupported = false;
        }
    }

    private void StartPublisher(DeviceAnnouncement? self)
    {
        try
        {
            _publisher = new WiFiDirectAdvertisementPublisher();
            _publisher.Advertisement.ListenStateDiscoverability = WiFiDirectAdvertisementListenStateDiscoverability.Normal;
            _publisher.Advertisement.IsAutonomousGroupOwnerEnabled = true;

            if (self is not null)
            {
                var payload = JsonSerializer.Serialize(new
                {
                    alias = self.Alias,
                    port = self.Port,
                    fingerprint = self.Fingerprint,
                });
                var data = Encoding.UTF8.GetBytes(Magic + payload);
                var value = CryptographicBuffer.CreateFromByteArray(data);
                var oui = CryptographicBuffer.CreateFromByteArray([0x00, 0x16, 0x1E]);
                var ie = new WiFiDirectInformationElement
                {
                    Oui = oui,
                    OuiType = 0x01,
                    Value = value,
                };
                _publisher.Advertisement.InformationElements.Add(ie);
            }

            _publisher.Start();
        }
        catch { }
    }

    private void StartListener()
    {
        try
        {
            _listener = new WiFiDirectConnectionListener();
            _listener.ConnectionRequested += OnConnectionRequested;
            IsListening = true;
        }
        catch { }
    }

    private void OnConnectionRequested(WiFiDirectConnectionListener sender, WiFiDirectConnectionRequestedEventArgs args)
    {
        try
        {
            var request = args.GetConnectionRequest();
            var deviceInfo = request?.DeviceInformation;
            if (deviceInfo is null) { request?.Dispose(); return; }

            var name = deviceInfo.Name;
            var id = deviceInfo.Id;
            var now = DateTime.UtcNow;

            var info = new DeviceInfo
            {
                Alias = string.IsNullOrEmpty(name) ? "WiFi Direct Gerät" : name,
                Version = "2.0",
                DeviceType = DeviceType.Desktop,
                Fingerprint = _selfFingerprint.Length > 8 ? _selfFingerprint[..8] : _selfFingerprint,
                Port = 53317,
                Protocol = ProtocolType.Https,
                Download = true,
                IpAddress = "",
                LastSeen = now,
                WifiDirectDeviceId = id,
            };

            var isNew = _knownDevices.TryAdd(id, new KnownWfdDevice(now, info.Alias, info.Port, id));
            DeviceFound?.Invoke(this, info);
            request?.Dispose();
        }
        catch { }
    }

    public async Task ScanAsync()
    {
        try
        {
            var selector = WiFiDirectDevice.GetDeviceSelector();
            var devices = await DeviceInformation.FindAllAsync(selector);
            foreach (var device in devices)
            {
                if (device.Name is null) continue;
                if (!device.Name.Contains(ServiceName, StringComparison.OrdinalIgnoreCase)
                    && !device.Name.Contains(_alias, StringComparison.OrdinalIgnoreCase))
                    continue;

                var now = DateTime.UtcNow;
                var info = new DeviceInfo
                {
                    Alias = device.Name,
                    Version = "2.0",
                    DeviceType = DeviceType.Desktop,
                    Fingerprint = "",
                    Port = 53317,
                    Protocol = ProtocolType.Https,
                    Download = true,
                    IpAddress = "",
                    LastSeen = now,
                    WifiDirectDeviceId = device.Id,
                };

                var isNew = !_knownDevices.ContainsKey(device.Id);
                _knownDevices[device.Id] = new KnownWfdDevice(now, device.Name, info.Port, device.Id);
                if (isNew)
                    DeviceFound?.Invoke(this, info);
                else
                    DeviceSeen?.Invoke(this, info);
            }
        }
        catch { }
    }

    public void Stop()
    {
        _scanTimer?.Dispose();
        _publisher?.Stop();
        _listener = null;
        IsListening = false;
    }

    public void Dispose()
    {
        Stop();
    }
}
