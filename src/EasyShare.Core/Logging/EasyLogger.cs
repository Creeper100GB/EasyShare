using Serilog;
using Serilog.Events;

namespace EasyShare.Core.Logging;

public static class EasyLogger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyShare", "logs");

    private static bool _initialized;

    public static Serilog.ILogger Log => Serilog.Log.Logger;

    public static void Init()
    {
        if (_initialized) return;
        _initialized = true;

        Directory.CreateDirectory(LogDir);

        Serilog.Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(LogDir, "easyshare-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
                fileSizeLimitBytes: 50 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true)
            .CreateLogger();

        Serilog.Log.Information("EasyShare Logger initialisiert. Log-Verzeichnis: {LogDir}", LogDir);
    }

    public static void Close()
    {
        Serilog.Log.CloseAndFlush();
        _initialized = false;
    }

    public static string LogDirectory => LogDir;
}
