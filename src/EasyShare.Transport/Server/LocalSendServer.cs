using System.Security.Cryptography.X509Certificates;
using EasyShare.Core.Models;

namespace EasyShare.Transport.Server;

public class UploadRequestEventArgs : EventArgs
{
    public string SessionId { get; set; } = string.Empty;
    public DeviceInfo Sender { get; set; } = new();
    public List<FileEntry> Files { get; set; } = new();
    public string Fingerprint { get; set; } = string.Empty;
}

public class LocalSendServer
{
    public event EventHandler<UploadRequestEventArgs>? UploadRequested;
    private readonly X509Certificate2 _certificate;
    private CancellationTokenSource? _cts;

    public LocalSendServer(X509Certificate2 certificate)
    {
        _certificate = certificate;
    }

    public void Start(int port)
    {
        _cts = new CancellationTokenSource();
        Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
    }
}
