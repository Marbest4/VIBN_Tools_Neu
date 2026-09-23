using System.Windows;
using System.Windows.Threading;

namespace VIBN_Tools.IbnRemote;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
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
