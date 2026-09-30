using System.Windows;

namespace VIBN_Tools
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += OnDispatcherUnhandledException;

            GlobalClasses.Services.Initialize();

        }

        private static void OnDispatcherUnhandledException(
            object sender,
            System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
        {
            if (Application.Behaviors.WpfVirtualizationExceptionPolicy.IsRecoverable(args.Exception))
            {
                Application.ApplicationLogService.Instance.Warning(
                    "FEE2Container",
                    "Eine veraltete virtuelle Tabellenanforderung wurde nach einem Ansichtswechsel verworfen.",
                    args.Exception.Message);
                args.Handled = true;
                return;
            }

            Application.ApplicationLogService.Instance.Error(
                "Unbehandelter UI-Fehler",
                "Die WPF-Oberfläche hat eine unbehandelte Ausnahme ausgelöst.",
                args.Exception);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                // WPF does not await async Exit handlers. Perform the bounded
                // cleanup before the host exits so no tool-owned TIA bridge is
                // left behind in the background.
                Task.Run(Application.ViCoFeatureBootstrapper.ShutdownAsync)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception)
            {
                Application.ApplicationLogService.Instance.Error(
                    "Anwendungsende",
                    "Hintergrunddienste konnten nicht vollständig beendet werden.",
                    exception);
            }
            finally
            {
                base.OnExit(e);
            }
        }
    }

}
