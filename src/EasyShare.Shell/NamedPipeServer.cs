using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace EasyShare.Shell;

public static class NamedPipeServer
{
    private static string PipeName => $"EasyShareIPC_{Process.GetCurrentProcess().SessionId}";

    private static PipeSecurity CreatePipeSecurity()
    {
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Deny));
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().Name,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            "SYSTEM",
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return pipeSecurity;
    }

    public static async Task StartServerAsync(Action<string[]> onFilesReceived, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var server = NamedPipeServerStreamAcl.Create(
                PipeName, PipeDirection.In, -1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0,
                CreatePipeSecurity(), HandleInheritability.None, PipeAccessRights.ReadData);

            await server.WaitForConnectionAsync(ct);

            using var reader = new StreamReader(server);
            var raw = await reader.ReadToEndAsync(ct);
            var paths = raw.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);

            if (paths.Length > 0)
                onFilesReceived(paths);
        }
    }

    public static async Task SendFilesAsync(string[] filePaths)
    {
        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
        await client.ConnectAsync(5000);
        await using var writer = new StreamWriter(client) { AutoFlush = true };
        await writer.WriteAsync(string.Join('\n', filePaths));
        client.WaitForPipeDrain();
    }
}
