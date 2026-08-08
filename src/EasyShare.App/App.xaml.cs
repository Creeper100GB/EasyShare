using System.IO;
using System.Windows;
using EasyShare.App.Localization;
using EasyShare.Core.Logging;
using EasyShare.Shell;
using Serilog;

namespace EasyShare.App;

public partial class App : Application
{
    private System.Threading.Mutex? _singleInstanceMutex;
    public static string[] ShareArgs { get; private set; } = Array.Empty<string>();

    protected override void OnStartup(StartupEventArgs e)
    {
        EasyLogger.Init();

        _singleInstanceMutex = new System.Threading.Mutex(true, @"Local\EasyShare.SingleInstance", out bool createdNew);

        if (!createdNew)
        {
            bool acquired;
            try { acquired = _singleInstanceMutex.WaitOne(TimeSpan.Zero); }
            catch (System.Threading.AbandonedMutexException) { acquired = true; }

            if (!acquired)
            {
                if (e.Args.Length > 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await NamedPipeServer.SendFilesAsync(e.Args); }
                        catch (Exception ex) { Log.Warning(ex, "NamedPipe SendFilesAsync fehlgeschlagen (zweiter Instanz)"); }
                    });
                }
                else
                {
                    MessageBox.Show(Loc.Tr("App.AlreadyRunning"), "EasyShare", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                Shutdown();
                return;
            }
        }

        base.OnStartup(e);

        if (e.Args.Length > 0)
            ShareArgs = e.Args;

        DispatcherUnhandledException += (s, ex) =>
        {
            Log.Fatal(ex.Exception, "Unbehandelte Ausnahme");
            try
            {
                var dialog = new Views.CrashDialog(ex.Exception);
                dialog.ShowDialog();
                if (dialog.RestartRequested)
                {
                    var exePath = Environment.ProcessPath;
                    if (exePath is not null)
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath) { UseShellExecute = true });
                    Shutdown(-1);
                    return;
                }
            }
            catch (Exception crashEx) { Log.Error(crashEx, "CrashDialog fehlgeschlagen"); }
            ex.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Schwerwiegende Ausnahme (AppDomain)");
        };
    }

    public static void SetShareArgs(string[] args) => ShareArgs = args;

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        if (MainWindow is MainWindow mainWindow)
            mainWindow.Cleanup();
    }
}
