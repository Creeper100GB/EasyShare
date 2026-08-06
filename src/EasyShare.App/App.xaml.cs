using System.IO;
using System.Windows;
using EasyShare.App.Localization;
using EasyShare.Shell;

namespace EasyShare.App;

public partial class App : Application
{
    private System.Threading.Mutex? _singleInstanceMutex;
    public static string[] ShareArgs { get; private set; } = Array.Empty<string>();

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new System.Threading.Mutex(true, @"Local\EasyShare.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            if (e.Args.Length > 0)
            {
                try { NamedPipeServer.SendFilesAsync(e.Args).Wait(5000); }
                catch { }
            }
            else
            {
                MessageBox.Show(Loc.Tr("App.AlreadyRunning"), "EasyShare", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);

        if (e.Args.Length > 0)
            ShareArgs = e.Args;

        DispatcherUnhandledException += (s, ex) =>
        {
            System.Console.Error.WriteLine($"[EasyShare] Unbehandelte Ausnahme: {ex.Exception}");
            ex.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            System.Console.Error.WriteLine($"[EasyShare] Schwerwiegende Ausnahme: {e.ExceptionObject}");
        };
    }

    public static void SetShareArgs(string[] args) => ShareArgs = args;

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        if (MainWindow is MainWindow mainWindow)
            mainWindow.Cleanup();
    }
}
