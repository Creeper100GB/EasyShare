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

        var folderPaths = filePaths.Where(Directory.Exists).ToList();
        session.ContainsFolders = folderPaths.Count > 0;
        if (filePaths.Count == 1 && folderPaths.Count == 1)
        {
            session.ZipName = Path.GetFileName(folderPaths[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        foreach (var path in filePaths)
        {
            if (Directory.Exists(path))
            {
                var singleFolder = folderPaths.Count == 1 && filePaths.Count == 1;
                if (singleFolder)
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        var relPath = Path.GetRelativePath(path, file).Replace('\\', '/');
                        session.Files.Add(new FileEntry
                        {
                            Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)),
                            FileName = relPath,
                            Size = new FileInfo(file).Length,
                            LocalFilePath = file,
                        });
                    }
                }
                else
                {
                    var folderName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        var relPath = Path.GetRelativePath(path, file).Replace('\\', '/');
                        session.Files.Add(new FileEntry
                        {
                            Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)),
                            FileName = $"{folderName}/{relPath}",
                            Size = new FileInfo(file).Length,
                            LocalFilePath = file,
                        });
                    }
                }
                continue;
            }

            var fileInfo = new FileInfo(path);
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            session.Files.Add(new FileEntry
            {
                Id = id,
                FileName = fileInfo.Name,
                Size = fileInfo.Length,
                LocalFilePath = path,
            });
        }

        return session;
    }
}