using System.Windows;
using System.Windows.Threading;

namespace VIBN_Tools.IbnRemote;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
        if (e.Args.Any(argument => string.Equals(argument, "--configure-stdin", StringComparison.Ordinal)))
        {
            try
            {
                IbnRemoteDeploymentConfiguration.ConfigureFromStandardInput();
                Shutdown(0);
            }
            catch (Exception exception)
            {
                IbnRemoteFileLog.Instance.Error(
                    "Konfiguration",
                    "Die Publish-Konfiguration konnte nicht geschützt gespeichert werden.",
                    exception);
                Shutdown(-2);
            }
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs args)
    {
        IbnRemoteFileLog.Instance.Error(
            "Anwendung",
            "Nicht behandelter UI-Fehler; die IBN-Anwendung wird beendet.",
            args.Exception);
        args.Handled = true;
        Current.Shutdown(-1);
    }
}
