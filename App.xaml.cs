using System.Windows;

using System.Diagnostics;

namespace VIBN_Tools
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private static readonly Stopwatch StartupStopwatch = Stopwatch.StartNew();

        internal static TimeSpan StartupElapsed => StartupStopwatch.Elapsed;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            Application.Behaviors.WindowWorkAreaBehavior.Register();

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
                    "WPF-Ansicht",
                    "Eine ungültige Layoutgröße oder veraltete virtuelle Listenanforderung wurde abgefangen; die Anwendung bleibt geöffnet.",
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
                // A cooperative page cancellation cannot interrupt a native
                // FEE SDK call. Closing the application therefore also tears
                // down the shared SDK session, bounded so shutdown itself does
                // not remain blocked indefinitely.
                if (GlobalClasses.Services.ApiInstance is not null)
                {
                    var feeDisconnect = Task.Run(() => GlobalClasses.Services.ApiInstance.Disconnect());
                    if (!feeDisconnect.Wait(TimeSpan.FromSeconds(2)))
                    {
                        Application.ApplicationLogService.Instance.Warning(
                            "Anwendungsende",
                            "Die FEE-Verbindung antwortete beim Beenden nicht innerhalb von zwei Sekunden; der Prozess beendet die verbleibende SDK-Arbeit.");
                    }
                }

            }
            catch (Exception exception)
            {
                Application.ApplicationLogService.Instance.Error(
                    "Anwendungsende",
                    "Die FEE-Verbindung konnte beim Beenden nicht sauber getrennt werden.",
                    exception);
            }

            try
            {
                // WPF does not await async Exit handlers. Perform the bounded
                // cleanup before the host exits so no tool-owned TIA bridge is
                // left behind in the background. This still runs if FEE cleanup failed.
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
