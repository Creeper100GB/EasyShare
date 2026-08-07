using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;
using EasyShare.Core.Models;

namespace EasyShare.Core.Discovery;

public class BluetoothDiscovery : IDisposable
{
    private const ushort ManufacturerId = 0xFFE0;
    private const byte ProtocolVersion = 1;

    private readonly record struct KnownBtDevice(DateTime LastSeen, string Alias, int Port, string FingerprintPrefix);

    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEAdvertisementPublisher? _publisher;
    private readonly ConcurrentDictionary<ulong, KnownBtDevice> _knownDevices = new();
    private Timer? _cleanupTimer;
    private string _selfFingerprint = "";

    public event EventHandler<DeviceInfo>? DeviceFound;
    public event EventHandler<DeviceInfo>? DeviceSeen;
    public event EventHandler<string>? DeviceLost;

    public void Start(DeviceAnnouncement? self = null)
    {
        if (self is not null)
        {
            _selfFingerprint = self.Fingerprint;
            StartPublisher(self);
        }

        StartWatcher();
        _cleanupTimer = new Timer(CleanupStaleDevices, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private void StartPublisher(DeviceAnnouncement self)
    {
        try
        {
            _publisher = new BluetoothLEAdvertisementPublisher();
            var writer = new DataWriter();
            writer.WriteByte(ProtocolVersion);
            writer.WriteUInt16((ushort)self.Port);

            var fingerprintBytes = Convert.FromHexString(self.Fingerprint);
            var prefixLen = Math.Min(8, fingerprintBytes.Length);
            writer.WriteByte((byte)prefixLen);
            writer.WriteBytes(fingerprintBytes[..prefixLen]);

            var aliasBytes = Encoding.UTF8.GetBytes(self.Alias);
            var maxAlias = Math.Min(aliasBytes.Length, 127);
            writer.WriteByte((byte)maxAlias);
            writer.WriteBytes(aliasBytes[..maxAlias]);

            var manufacturerData = new BluetoothLEManufacturerData(ManufacturerId, writer.DetachBuffer());
            _publisher.Advertisement.ManufacturerData.Add(manufacturerData);
            _publisher.Start();
        }
        catch
        {
        }
    }

    private void StartWatcher()
    {
        try
        {
            _watcher = new BluetoothLEAdvertisementWatcher();
            _watcher.ScanningMode = BluetoothLEScanningMode.Active;
            _watcher.Received += OnAdvertisementReceived;
            _watcher.Start();
        }
        catch
        {
        }
    }

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        try
        {
            var manufacturerSection = args.Advertisement.ManufacturerData
                .FirstOrDefault(m => m.CompanyId == ManufacturerId);
            if (manufacturerSection is null) return;

            var reader = DataReader.FromBuffer(manufacturerSection.Data);
            if (reader.UnconsumedBufferLength < 5) return;

            var version = reader.ReadByte();
            if (version != ProtocolVersion) return;

            var port = reader.ReadUInt16();
            var prefixLen = reader.ReadByte();
            if (reader.UnconsumedBufferLength < prefixLen + 1) return;

            var fingerprintBytes = new byte[prefixLen];
            reader.ReadBytes(fingerprintBytes);
            var fingerprintPrefix = Convert.ToHexString(fingerprintBytes).ToLowerInvariant();

            if (_selfFingerprint.StartsWith(fingerprintPrefix)) return;

            var aliasLen = reader.ReadByte();
            var aliasBytes = new byte[aliasLen];
            if (reader.UnconsumedBufferLength < aliasLen) return;
            reader.ReadBytes(aliasBytes);
            var alias = Encoding.UTF8.GetString(aliasBytes);

            var btAddress = args.BluetoothAddress;
            var btAddressStr = btAddress.ToString("X12");

            var now = DateTime.UtcNow;
            var isNew = false;
            _knownDevices.AddOrUpdate(btAddress,
                _ => new KnownBtDevice(now, alias, port, fingerprintPrefix),
                (_, existing) =>
                {
                    if (existing.Alias != alias || existing.Port != port)
                        isNew = true;
                    return existing with { LastSeen = now };
                });
            isNew = isNew || !_knownDevices.TryGetValue(btAddress, out _);

            var info = new DeviceInfo
            {
                Alias = alias,
                Version = "2.0",
                DeviceType = Core.Models.DeviceType.Desktop,
                Fingerprint = fingerprintPrefix,
                Port = port,
                Protocol = Models.ProtocolType.Https,
                Download = true,
                IpAddress = "",
                BluetoothAddress = btAddressStr,
                LastSeen = now,
            };

            DeviceSeen?.Invoke(this, info);
            if (isNew)
                DeviceFound?.Invoke(this, info);
        }
        catch
        {
        }
    }

    private void CleanupStaleDevices(object? state)
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-60);
        var stale = _knownDevices
            .Where(kv => kv.Value.LastSeen < cutoff)
            .Select(kv => kv.Key.ToString("X12"))
            .ToList();

        foreach (var addr in stale)
        {
            if (_knownDevices.TryRemove(ulong.Parse(addr, System.Globalization.NumberStyles.HexNumber), out _))
                DeviceLost?.Invoke(this, addr);
        }
    }

    public void Stop()
    {
        _watcher?.Stop();
        _publisher?.Stop();
        _cleanupTimer?.Dispose();
    }

    public void Dispose()
    {
        Stop();
    }
}
