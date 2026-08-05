using System.Security.Cryptography;
using EasyShare.Core.Models;

namespace EasyShare.Core.Sessions;

public class SessionManager
{
    public TransferSession CreateSendSession(DeviceInfo target, List<string> filePaths)
    {
        var session = new TransferSession
        {
            SessionId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
            TargetDevice = target,
            Direction = TransferDirection.Sending,
            Status = TransferStatus.Pending,
            StartedAt = DateTime.UtcNow,
        };

        foreach (var path in filePaths)
        {
            var fileInfo = new FileInfo(path);
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            session.Files.Add(new FileEntry
            {
                Id = id,
                FileName = fileInfo.Name,
                Size = fileInfo.Length,
            });
        }

        return session;
    }
}
