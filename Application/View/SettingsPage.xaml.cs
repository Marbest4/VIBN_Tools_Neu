using VIBN_Tools.Application.VM;
using System.Windows;
using System.Windows.Threading;
using static VIBN_Tools.GlobalClasses.Services;

namespace VIBN_Tools.Application.View
{
    /// <summary>
    /// Interaction logic for SettingsPage.xaml
    /// </summary>
    public partial class SettingsPage
    {
        private readonly SettingsPageVM _viewModel;

        public SettingsPage()
        {
            InitializeComponent();
            _viewModel = new SettingsPageVM(
                ProjectSettings,
                Connection,
                ViCoFeatureBootstrapper.WorkstationDirectory,
                credentialConfiguration: ViCoFeatureBootstrapper.CredentialConfigurationService,
                log: ApplicationLogService.Instance);
            DataContext = _viewModel;
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await _viewModel.InitializeDeferredAsync();
        }
    }
}
