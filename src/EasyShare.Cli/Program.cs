using System.Diagnostics;
using EasyShare.Shell;

namespace EasyShare.Cli;

static class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var command = args[0].ToLowerInvariant();
        var rest = args[1..];

        return command switch
        {
            "share" => await HandleShare(rest),
            "install" => HandleInstall(),
            "uninstall" => HandleUninstall(),
            _ => PrintUsage()
        };
    }

    static async Task<int> HandleShare(string[] args)
    {
        var targetDevice = ParseTargetFlag(args, out var paths);
        if (paths.Length == 0)
        {
            await Console.Error.WriteLineAsync("Keine Dateien angegeben.");
            return 1;
        }

        foreach (var path in paths)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                await Console.Error.WriteLineAsync($"Datei nicht gefunden: {path}");
                return 1;
            }
        }

        try
        {
            await NamedPipeServer.SendFilesAsync(paths);
            return 0;
        }
        catch (TimeoutException)
        {
            await Console.Error.WriteLineAsync("Keine laufende EasyShare-Instanz gefunden. Starte App...");
        }
        catch (IOException)
        {
            await Console.Error.WriteLineAsync("Keine laufende EasyShare-Instanz gefunden. Starte App...");
        }

        try
        {
            var appExe = GetAppExePath();
            if (!File.Exists(appExe))
            {
                await Console.Error.WriteLineAsync($"EasyShare.App nicht gefunden: {appExe}");
                return 2;
            }

            var startArgs = string.Join(" ", paths.Select(p => $"\"{p}\""));
            Process.Start(new ProcessStartInfo
            {
                FileName = appExe,
                Arguments = startArgs,
                UseShellExecute = false
            });
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Fehler beim Starten der App: {ex.Message}");
            return 2;
        }
    }

    static string ParseTargetFlag(string[] args, out string[] remainingPaths)
    {
        var list = new List<string>(args);
        string? target = null;

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == "--to" && i + 1 < list.Count)
            {
                target = list[i + 1];
                list.RemoveAt(i + 1);
                list.RemoveAt(i);
                i--;
            }
        }

        remainingPaths = [.. list];
        return target ?? string.Empty;
    }

    static string GetAppExePath()
    {
        var dir = AppContext.BaseDirectory;
        var appDir = Path.Combine(dir, "..", "..", "..", "..", "EasyShare.App", "bin");
        if (Directory.Exists(appDir))
        {
            var exe = Directory.GetFiles(appDir, "EasyShare.App.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();
            if (exe != null)
                return exe;
        }

        return Path.Combine(dir, "EasyShare.App.exe");
    }

    static int HandleInstall()
    {
        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath))
        {
            Console.Error.WriteLine("Pfad zur ausf\u00fchrbaren Datei konnte nicht ermittelt werden.");
            return 1;
        }

        ShellIntegration.Register(exePath);
        Console.WriteLine("Explorer-Kontextmen\u00fc registriert.");
        return 0;
    }

    static int HandleUninstall()
    {
        ShellIntegration.Unregister();
        Console.WriteLine("Explorer-Kontextmen\u00fc entfernt.");
        return 0;
    }

    static int PrintUsage()
    {
        Console.Error.WriteLine("EasyShare CLI");
        Console.Error.WriteLine("  easyshare share <Dateien...> [--to <Ger\u00e4t>]");
        Console.Error.WriteLine("  easyshare install");
        Console.Error.WriteLine("  easyshare uninstall");
        return 1;
    }
}
