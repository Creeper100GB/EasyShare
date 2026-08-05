using System.Windows;

namespace EasyShare.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
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
