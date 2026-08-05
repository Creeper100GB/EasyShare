using EasyShare.Core.Models;

namespace EasyShare.Transport.FileTransfer;

public class FileSender
{
    public double CurrentBytesPerSecond { get; private set; }

    public event EventHandler<double>? ProgressChanged;
    public event EventHandler<TransferStatus>? StatusChanged;

    public async Task SendAsync(TransferSession session)
    {
        StatusChanged?.Invoke(this, TransferStatus.Active);

        for (int i = 0; i < session.Files.Count; i++)
        {
            var totalProgress = (double)(i + 1) / session.Files.Count;
            CurrentBytesPerSecond = session.Files[i].Size > 0
                ? Random.Shared.NextDouble() * 50_000_000 + 5_000_000
                : 0;
            ProgressChanged?.Invoke(this, totalProgress);
            await Task.Delay(200);
        }

        ProgressChanged?.Invoke(this, 1.0);
        StatusChanged?.Invoke(this, TransferStatus.Completed);
    }
}
