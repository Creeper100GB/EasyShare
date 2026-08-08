using System.Diagnostics;
using EasyShare.Core.Logging;
using Serilog;

namespace EasyShare.Core.Services;

public static class FirewallHelper
{
    private static readonly Serilog.ILogger Log = EasyLogger.Log.ForContext("SourceContext", "FirewallHelper");
    public static bool RuleExists()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "advfirewall firewall show rule name=EasyShare",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            var output = proc?.StandardOutput?.ReadToEnd() ?? "";
            proc?.WaitForExit();
            return output.Contains("EasyShare");
        }
        catch
        {
            return false;
        }
    }

    public static void EnsureRules()
    {
        if (RuleExists()) return;

        try
        {
            var exePath = Environment.ProcessPath ?? "";
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"netsh advfirewall firewall add rule name=EasyShare dir=in action=allow program='{exePath}' enable=yes protocol=TCP localport=53317 profile=private,domain; netsh advfirewall firewall add rule name=EasyShare-UDP dir=in action=allow program='{exePath}' enable=yes protocol=UDP localport=53317 profile=private,domain\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            proc?.WaitForExit();
        }
        catch
        {
            Log.Warning("Firewall-Regeln konnten nicht erstellt werden (UAC abgelehnt).");
        }
    }
}
