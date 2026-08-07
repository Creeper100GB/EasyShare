using System.Net;
using Windows.Devices.WiFiDirect;
using Windows.Networking;

namespace EasyShare.App.Services;

public class WiFiDirectService : IDisposable
{
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
        catch
        {
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
