using System.Net;
using Windows.Devices.WiFiDirect;
using Windows.Networking;
using EasyShare.Core.Logging;
using Serilog;

namespace EasyShare.App.Services;

public class WiFiDirectService : IDisposable
{
    private static readonly ILogger Log = EasyLogger.Log.ForContext<WiFiDirectService>();

    public bool IsSupported { get; private set; }
    private WiFiDirectDevice? _connected;

    public WiFiDirectService()
    {
        try
        {
            _ = typeof(WiFiDirectDevice);  // verify the type is projected
            IsSupported = true;
        }
        catch
        {
            IsSupported = false;
        }
    }

    public async Task<(string RemoteIp, string LocalIp)?> ConnectAsync(string deviceId, CancellationToken ct = default)
    {
        if (!IsSupported) return null;
        try
        {
            Log.Information("WiFi Direct Verbindung zu {DeviceId}", deviceId);
            var device = await WiFiDirectDevice.FromIdAsync(deviceId).AsTask(ct);
            if (device is null) return null;
            _connected = device;

            var pairs = device.GetConnectionEndpointPairs();
            foreach (var pair in pairs)
            {
                var remote = pair.RemoteHostName?.RawName ?? "";
                var local = pair.LocalHostName?.RawName ?? "";
                if (IPAddress.TryParse(remote, out _) && !string.IsNullOrEmpty(local))
                    return (remote, local);
            }

            var first = pairs.FirstOrDefault();
            if (first?.RemoteHostName is null) return null;
            return (first.RemoteHostName.RawName ?? "", first.LocalHostName?.RawName ?? "");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WiFi Direct Verbindung fehlgeschlagen");
            return null;
        }
    }

    public void Disconnect()
    {
        try { _connected?.Dispose(); } catch { }
        _connected = null;
    }

    public void Dispose()
    {
        Disconnect();
    }
}
