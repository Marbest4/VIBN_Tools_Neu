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

            DispatcherUnhandledException += (_, args) =>
                Application.ApplicationLogService.Instance.Error(
                    "Unbehandelter UI-Fehler",
                    "Die WPF-Oberfläche hat eine unbehandelte Ausnahme ausgelöst.",
                    args.Exception);

            GlobalClasses.Services.Initialize();

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
