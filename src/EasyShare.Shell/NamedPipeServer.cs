using System.IO.Pipes;

namespace EasyShare.Shell;

public static class NamedPipeServer
{
    private const string PipeName = @"EasyShareIPC";

    public static async Task StartServerAsync(Action<string[]> onFilesReceived, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, -1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

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
