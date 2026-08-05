using System.Windows;

namespace EasyShare.App;

public partial class App : Application
{
    private System.Threading.Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new System.Threading.Mutex(true, @"Local\EasyShare.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("EasyShare läuft bereits.", "EasyShare", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

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

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        if (MainWindow is MainWindow mainWindow)
            mainWindow.Cleanup();
    }
}
