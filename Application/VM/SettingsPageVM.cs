using System.Collections.ObjectModel;
using System.Net.Http;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Newtonsoft.Json;
using NPOI.POIFS.Crypt;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;
using VIBN_Tools.KanbanizeService;
using VIBN_Tools.Settings;
using VIBN_Tools.Core.ViCo;
using VIBN_Tools.Infrastructure.ViCo;
using static VIBN_Tools.Settings.ProjectSettings;

namespace VIBN_Tools.Application.VM
{
    /// <summary>Coordinates project settings and delegates FEE connection handling to the vendor SDK.</summary>
    public class SettingsPageVM : MvvmBase
    {

        //===========================================================================================================================
        // B I N D I N G S   -   P R O J E C T   S E T T I N G S
        //===========================================================================================================================

        // Array with TemplateType values
        public Array TemplateTypes => Enum.GetValues(typeof(TemplateType));

        private readonly ProjectSettings _projectSettings;
        private readonly FeeConnectionService _connectionService;
        private readonly FeeObjectService _feeObjectService;
        private readonly IWorkstationDirectory _workstations;
        private readonly INetworkAvailabilityService _availability;
        private readonly IApplicationLog _log;
        private readonly IUserCredentialConfigurationService _credentialConfiguration;
        private readonly IAutomationInstallationDiscovery _automationInstallationDiscovery;
        private CancellationTokenSource? _serverFilterCancellation;
        private int _serverRefreshVersion;

        public TemplateType SelectedTemplate
        {
            get => _projectSettings.SelectedTemplate;
            set
            {
                _projectSettings.SelectedTemplate = value;
                OnPropertyChanged();
            }
        }






        //===========================================================================================================================
        // B I N D I N G S   -   R E M O T E   &   C O N N E C T I O N
        //===========================================================================================================================

        // Checkbox for using localhost
        private bool _checkboxUseLocalhost;
        public bool CheckboxUseLocalhost
        {
            get { return _checkboxUseLocalhost; }
            set
            {
                if (_checkboxUseLocalhost == value) return;

                _checkboxUseLocalhost = value;
                OnPropertyChanged();

                if (_isServerChangeActive) return;

                if (value)
                {
                    _isServerChangeActive = true;
                    SelectedServer = "localhost";
                    _isServerChangeActive = false;
                }
            }
        }


        private string _selectedServer = string.Empty;
        public string SelectedServer
        {
            get { return _selectedServer; }
            set
            {
                if (_selectedServer == value) return;

                _selectedServer = value;
                OnPropertyChanged();

                // The editable ComboBox owns one text value. Keeping a second
                // SelectedItem binding caused WPF to clear partially typed PC
                // numbers whenever the filtered collection changed.
                if (!string.Equals(_serverFilter, value, StringComparison.Ordinal))
                {
                    _serverFilter = value ?? string.Empty;
                    OnPropertyChanged(nameof(ServerFilter));
                    _ = RefreshOnlineServersAsync();
                }

                if (_isServerChangeActive) return;

                if (!string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    _isServerChangeActive = true;
                    CheckboxUseLocalhost = false;
                    _isServerChangeActive = false;
                }

                if (string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase) ||
                    _workstations.PcNames.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    _ = CheckServerAsync(value);
                }
            }
        }

        private bool _isServerChangeActive = false;

        /// <summary>Only reachable workstation names are exposed to the FEE selector.</summary>
        public ObservableCollection<string> ServerNames { get; } = new();

        private string _serverFilter = string.Empty;
        public string ServerFilter
        {
            get => _serverFilter;
            set
            {
                if (string.Equals(_serverFilter, value, StringComparison.Ordinal))
                    return;
                _serverFilter = value ?? string.Empty;
                OnPropertyChanged();

                if (!string.Equals(_selectedServer, _serverFilter, StringComparison.Ordinal))
                {
                    _selectedServer = _serverFilter;
                    OnPropertyChanged(nameof(SelectedServer));
                }
                _ = RefreshOnlineServersAsync();
            }
        }

        public ICommand RefreshServerList => GetCommandBindingAsync(RefreshOnlineServersAsync);

        private bool _isServerReachable;
        public bool IsServerReachable
        {
            get { return _isServerReachable; }
            set
            {
                _isServerReachable = value;
                OnPropertyChanged();
            }
        }

        private string _connectionStatus = "Noch keine Verbindung aufgebaut.";
        public string ConnectionStatus
        {
            get => _connectionStatus;
            private set
            {
                _connectionStatus = value;
                OnPropertyChanged();
            }
        }

        public string UsedFeeSdkVersion { get; }

        public string InstalledFeeVersion { get; }

        public bool HasFeeVersionMismatch { get; }

        public string FeeVersionStatus { get; }

        public ObservableCollection<InstalledAutomationComponent> InstalledAutomationComponents { get; } = new();

        private string _automationDiscoveryStatus = "Installationssuche noch nicht ausgeführt.";
        public string AutomationDiscoveryStatus
        {
            get => _automationDiscoveryStatus;
            private set
            {
                _automationDiscoveryStatus = value;
                OnPropertyChanged();
            }
        }

        public ICommand RefreshAutomationInstallations =>
            GetCommandBinding(RefreshAutomationInstallationInventory);

        private string _kanbanizeApiKeyInput = string.Empty;
        public string KanbanizeApiKeyInput
        {
            get => _kanbanizeApiKeyInput;
            set
            {
                _kanbanizeApiKeyInput = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        private string _remoteDesktopPasswordInput = string.Empty;
        public string RemoteDesktopPasswordInput
        {
            get => _remoteDesktopPasswordInput;
            set
            {
                _remoteDesktopPasswordInput = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        private string _feeUsernameInput = "admin";
        public string FeeUsernameInput
        {
            get => _feeUsernameInput;
            set
            {
                _feeUsernameInput = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        private string _feePasswordInput = string.Empty;
        public string FeePasswordInput
        {
            get => _feePasswordInput;
            set
            {
                _feePasswordInput = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        private bool _hasKanbanizeApiKey;
        public bool HasKanbanizeApiKey
        {
            get => _hasKanbanizeApiKey;
            private set
            {
                _hasKanbanizeApiKey = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(KanbanizeApiKeyStatus));
            }
        }

        private bool _hasRemoteDesktopPassword;
        public bool HasRemoteDesktopPassword
        {
            get => _hasRemoteDesktopPassword;
            private set
            {
                _hasRemoteDesktopPassword = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RemoteDesktopPasswordStatus));
            }
        }

        private bool _hasFeeCredentials;
        public bool HasFeeCredentials
        {
            get => _hasFeeCredentials;
            private set
            {
                _hasFeeCredentials = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FeeCredentialsStatus));
            }
        }

        public string KanbanizeApiKeyStatus => HasKanbanizeApiKey ? "Konfiguriert" : "Nicht konfiguriert";

        public string RemoteDesktopPasswordStatus => HasRemoteDesktopPassword ? "Konfiguriert" : "Nicht konfiguriert";

        public string FeeCredentialsStatus => HasFeeCredentials ? "Konfiguriert" : "Nicht konfiguriert";

        private string _credentialStatus = "Zugangsdaten werden geschützt im lokalen Windows Credential Manager gespeichert.";
        public string CredentialStatus
        {
            get => _credentialStatus;
            private set
            {
                _credentialStatus = value;
                OnPropertyChanged();
            }
        }

        public ICommand SaveIntegrationCredentials => GetCommandBinding(SaveIntegrationCredentialsForUser);

        public ICommand DeleteKanbanizeApiKey => GetCommandBinding(DeleteKanbanizeApiKeyForUser);

        public ICommand DeleteRemoteDesktopPassword => GetCommandBinding(DeleteRemoteDesktopPasswordForUser);

        public ICommand DeleteFeeCredentials => GetCommandBinding(DeleteFeeCredentialsForUser);



        // Toggle Buttons Displays
        private bool _useDisplay1;
        public bool UseDisplay1
        {
            get { return _useDisplay1; }
            set
            {
                _useDisplay1 = value;
                OnPropertyChanged();
                UpdateUsedDisplays();
            }
        }

        private bool _useDisplay2;
        public bool UseDisplay2
        {
            get { return _useDisplay2; }
            set
            {
                _useDisplay2 = value;
                OnPropertyChanged();
                UpdateUsedDisplays();
            }
        }

        private bool _useDisplay3;
        public bool UseDisplay3
        {
            get { return _useDisplay3; }
            set
            {
                _useDisplay3 = value;
                OnPropertyChanged();
                UpdateUsedDisplays();
            }
        }

        private bool _useDisplay4;
        public bool UseDisplay4
        {
            get { return _useDisplay4; }
            set
            {
                _useDisplay4 = value;
                OnPropertyChanged();
                UpdateUsedDisplays();
            }
        }


        public int UsedDisplays { get; set; }









        //===========================================================================================================================
        // B I N D I N G S   -   S I M U L A T I O N   ( F E E )
        //===========================================================================================================================

        public ICommand ConnectToFee => GetCommandBindingAsync(Connect_ToFee);
        public ICommand DisconnectFromFee => GetCommandBindingAsync(Disconnect_FromFee);
        public FeeConnectionService Connection => Services.Connection;


        public ICommand CreateProjectBase => GetCommandBindingAsync(Create_ProjectBase);


        private string _connectedServer = "---";
        public string ConnectedServer
        {
            get { return _connectedServer; }
            set
            {
                _connectedServer = value;
                OnPropertyChanged();
            }
        }

        private string _feeObjectStatus = string.Empty;
        public string FeeObjectStatus
        {
            get { return _feeObjectStatus; }
            set
            {
                _feeObjectStatus = value;
                OnPropertyChanged();
            }
        }


        private bool _loadFeeData;
        public bool LoadFeeData
        {
            get { return _loadFeeData; }
            set 
            { 
                _loadFeeData = value; 
                OnPropertyChanged();
            }
        }







        public ICommand TestButton => GetCommandBindingAsync(Test_Button);
        private KanbanizeService.KanbanizeService _kanbanizeService = new KanbanizeService.KanbanizeService();

        public ObservableCollection<KanbanizeBoard> Boards {  get; set; } = new ObservableCollection<KanbanizeBoard>();




        private async Task Test_Button()
        {
            var startTime = DateTime.Now;

            var boards = await _kanbanizeService.LoadBoardsAsync();
            Boards.Clear();
            foreach (var item in boards)
                Boards.Add(item);

            var cardsTask = _kanbanizeService.LoadAllCardsAsync();

            var boardTasks = Boards.Select(async board =>
            {
                var workflowsTask = _kanbanizeService.LoadWorkflowsAsync(board.Id);
                var lanesTask = _kanbanizeService.LoadLanesAsync(board.Id);
                var columnsTask = _kanbanizeService.LoadColumnsAsync(board.Id);

                await Task.WhenAll(workflowsTask, lanesTask, columnsTask);

                board.Workflows = workflowsTask.Result;
                board.AllColumns = columnsTask.Result;

                var lanes = lanesTask.Result;

                foreach (var workflow in board.Workflows)
                {
                    workflow.Lanes = lanes.Where(l => l.WorkflowId == workflow.Id).OrderBy(l => l.Position).ToList();
                    workflow.Columns = board.AllColumns.Where(c => c.WorkflowId == workflow.Id).ToList();
                }
            });

            var allTasks = new List<Task>();
            allTasks.AddRange(boardTasks);
            allTasks.Add(cardsTask);

            await Task.WhenAll(allTasks);


            // Map Cards
            var allCards = cardsTask.Result;

            foreach(var board in Boards)
            {
                var boardCards = allCards.Where(c => c.BoardId == board.Id);

                foreach (var card in boardCards)
                {
                    var lane = board.Workflows.SelectMany(w => w.Lanes).FirstOrDefault(l => l.Id == card.LaneId);
                    lane?.Cards.Add(card);

                    var column = board.AllColumns.FirstOrDefault(c => c.Id == card.ColumnId);
                    card.Section = column?.Section;
                }
            }

            

            var operationTime = (DateTime.Now - startTime).TotalSeconds;

            MessageBox.Show($"Total seconds reading Kanbanize Data: {operationTime}s");

        }







        //===========================================================================================================================
        // C O N S T R U C T O R
        //===========================================================================================================================

        public SettingsPageVM(
            ProjectSettings projectSettings,
            FeeConnectionService connectionService,
            IWorkstationDirectory workstations,
            INetworkAvailabilityService? availability = null,
            IApplicationLog? log = null,
            IFeeVersionInfoProvider? feeVersionInfoProvider = null,
            IUserCredentialConfigurationService? credentialConfiguration = null,
            IAutomationInstallationDiscovery? automationInstallationDiscovery = null)
        {
            _projectSettings = projectSettings;
            _connectionService = connectionService;
            _feeObjectService = Services.FeeObjects;
            _workstations = workstations;
            _availability = availability ?? new NetworkAvailabilityService();
            _log = log ?? NullApplicationLog.Instance;
            _credentialConfiguration = credentialConfiguration ??
                new SecureUserCredentialConfigurationService();
            _automationInstallationDiscovery = automationInstallationDiscovery ??
                new AutomationInstallationDiscovery();

            var feeVersionInfo = (feeVersionInfoProvider ?? new FeeVersionInfoProvider()).Read();
            UsedFeeSdkVersion = feeVersionInfo.UsedSdkVersion;
            InstalledFeeVersion = feeVersionInfo.InstalledFeeVersion;
            HasFeeVersionMismatch = feeVersionInfo.HasVersionMismatch;
            FeeVersionStatus = feeVersionInfo.StatusMessage;
            _log.Information(
                "Project Settings",
                $"Verwendete SDK-Version: {UsedFeeSdkVersion}; installierte FEE-Version: {InstalledFeeVersion}.");
            if (HasFeeVersionMismatch)
                _log.Warning("Project Settings", FeeVersionStatus);

            RefreshAutomationInstallationInventory();

            _workstations.PcNames.CollectionChanged += (_, _) => _ = RefreshOnlineServersAsync();

            _feeObjectService.FeeObjectsUpdated += OnFeeObjectsLoaded;

            CheckboxUseLocalhost = true;

            ConnectedServer = "---";

            Connection.Connected += OnConnected;

            LoadFeeData = false;
            RefreshCredentialStatus();
            _ = RefreshOnlineServersAsync();
        }

        private void RefreshAutomationInstallationInventory()
        {
            try
            {
                var inventory = _automationInstallationDiscovery.Discover();
                InstalledAutomationComponents.Clear();
                foreach (var component in inventory.Components)
                    InstalledAutomationComponents.Add(component);
                AutomationDiscoveryStatus = inventory.Components.Count == 0
                    ? string.Join(" ", inventory.Diagnostics)
                    : $"{inventory.Components.Count} lokale Komponente(n) erkannt." +
                      (inventory.Diagnostics.Count == 0
                          ? string.Empty
                          : $" Hinweise: {string.Join(" ", inventory.Diagnostics)}");
                _log.Information("Project Settings", AutomationDiscoveryStatus);
            }
            catch (Exception exception)
            {
                AutomationDiscoveryStatus = $"Installationssuche fehlgeschlagen: {exception.Message}";
                _log.Error("Project Settings", AutomationDiscoveryStatus, exception);
            }
        }















        //===========================================================================================================================
        // M E T H O D S   ( B U T T O N S )
        //===========================================================================================================================

        private async Task Connect_ToFee(object parameter)
        {
            if (string.IsNullOrWhiteSpace(SelectedServer))
            {
                ConnectionStatus = "Bitte zuerst einen PC auswählen.";
                _log.Warning("Project Settings", ConnectionStatus);
                return;
            }

            _connectionService.LoadFeeDataOnConnect = LoadFeeData;
            var stopwatch = Stopwatch.StartNew();
            ConnectionStatus = $"Verbindung zu {SelectedServer} wird aufgebaut …";
            ConnectedServer = "---";
            _log.Information("Project Settings", ConnectionStatus);
            try
            {
                if (Services.ApiInstance is null)
                {
                    stopwatch.Stop();
                    ConnectionStatus = "Die FEE-Laufzeit ist nicht verfügbar. Bitte die zu fdc85b1 passende SDK-Installation bzw. SDK-Auswahl prüfen.";
                    _log.Warning("Project Settings", ConnectionStatus);
                    return;
                }

                if (_connectionService.IsConnected)
                {
                    Services.ApiInstance.Disconnect();
                    if (!await _connectionService.WaitForDisconnectedAsync(TimeSpan.FromSeconds(3)))
                    {
                        stopwatch.Stop();
                        ConnectionStatus = "Die bestehende FEE-Verbindung konnte nicht sauber getrennt werden.";
                        _log.Warning("Project Settings", ConnectionStatus);
                        return;
                    }
                }

                // Preserve the fdc85b1 Connect call and its vendor defaults,
                // while retaining the later optional Credential Manager
                // integration. A newly typed password applies immediately;
                // otherwise the saved pair or admin/admin is used.
                var typedPassword = FeePasswordInput;
                var feeUsername = string.IsNullOrEmpty(typedPassword)
                    ? _credentialConfiguration.GetFeeUsername()
                    : FeeUsernameInput;
                var feePassword = string.IsNullOrEmpty(typedPassword)
                    ? _credentialConfiguration.GetFeePassword()
                    : typedPassword;
                Services.ApiInstance.Connect(
                    SelectedServer,
                    string.IsNullOrWhiteSpace(feeUsername) ? "admin" : feeUsername,
                    string.IsNullOrEmpty(feePassword) ? "admin" : feePassword);
                var connected = await _connectionService.WaitForConnectedAsync(TimeSpan.FromSeconds(10));
                stopwatch.Stop();
                if (!connected)
                {
                    await DisconnectAfterFailedConnectionAsync();
                    ConnectedServer = "---";
                    ConnectionStatus = $"Verbindung zu {SelectedServer} konnte nicht bestätigt werden (Zeitüberschreitung).";
                    _log.Warning("Project Settings", ConnectionStatus);
                    return;
                }

                ConnectedServer = SelectedServer;
                ConnectionStatus = $"Mit {SelectedServer} verbunden ({stopwatch.Elapsed.TotalSeconds:F1} s).";
                _log.Information("Project Settings", ConnectionStatus);
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                await DisconnectAfterFailedConnectionAsync();
                ConnectedServer = "---";
                ConnectionStatus = $"Verbindung zu {SelectedServer} fehlgeschlagen.";
                _log.Error("Project Settings", ConnectionStatus, exception);
            }
        }




        private Task Disconnect_FromFee(object parameter)
        {
            Services.ApiInstance.Disconnect();

            ConnectedServer = "---";
            ConnectionStatus = "Verbindung getrennt.";
            _log.Information("Project Settings", ConnectionStatus);
            return Task.CompletedTask;
        }


        private async Task Create_ProjectBase(object parameter)
        {
            await CreateFeeSimulationBaseAsync();

            await ImportFeeLogicAndCabinetFilesAsync();
        }



        







        //===========================================================================================================================
        // M E T H O D S   ( H E L P E R S )
        //===========================================================================================================================



        private void UpdateUsedDisplays()
        {
            UsedDisplays = new[] { UseDisplay1, UseDisplay2, UseDisplay3, UseDisplay4 }.Count(x => x);
        }

        private void SaveIntegrationCredentialsForUser()
        {
            try
            {
                var changed = false;
                if (!string.IsNullOrWhiteSpace(KanbanizeApiKeyInput))
                {
                    _credentialConfiguration.SaveKanbanizeApiKey(KanbanizeApiKeyInput);
                    changed = true;
                }
                if (!string.IsNullOrEmpty(RemoteDesktopPasswordInput))
                {
                    _credentialConfiguration.SaveRemoteDesktopPassword(RemoteDesktopPasswordInput);
                    changed = true;
                }
                if (!string.IsNullOrEmpty(FeePasswordInput))
                {
                    _credentialConfiguration.SaveFeeCredentials(FeeUsernameInput, FeePasswordInput);
                    changed = true;
                }

                KanbanizeApiKeyInput = string.Empty;
                RemoteDesktopPasswordInput = string.Empty;
                FeePasswordInput = string.Empty;
                RefreshCredentialStatus();
                CredentialStatus = changed
                    ? "Eingegebene Zugangsdaten wurden geschützt im Windows Credential Manager gespeichert."
                    : "Keine neuen Werte eingegeben; vorhandene Konfiguration bleibt unverändert.";
                _log.Information("Zugangsdaten", CredentialStatus);
            }
            catch (Exception exception)
            {
                RefreshCredentialStatus();
                CredentialStatus = "Zugangsdaten konnten nicht gespeichert werden.";
                _log.Error("Zugangsdaten", CredentialStatus, exception);
            }
        }

        private void DeleteKanbanizeApiKeyForUser()
        {
            UpdateCredentialConfiguration(
                _credentialConfiguration.DeleteKanbanizeApiKey,
                "Kanbanize API-Key wurde für diesen Windows-Benutzer entfernt.");
        }

        private void DeleteRemoteDesktopPasswordForUser()
        {
            UpdateCredentialConfiguration(
                _credentialConfiguration.DeleteRemoteDesktopPassword,
                "Remote-Desktop-Passwort wurde für diesen Windows-Benutzer entfernt.");
        }

        private void DeleteFeeCredentialsForUser()
        {
            UpdateCredentialConfiguration(
                _credentialConfiguration.DeleteFeeCredentials,
                "FEE-Zugangsdaten wurden für diesen Windows-Benutzer entfernt.");
        }

        private void UpdateCredentialConfiguration(Action update, string successMessage)
        {
            try
            {
                update();
                RefreshCredentialStatus();
                CredentialStatus = successMessage;
                _log.Information("Zugangsdaten", successMessage);
            }
            catch (Exception exception)
            {
                RefreshCredentialStatus();
                CredentialStatus = "Zugangsdaten konnten nicht entfernt werden.";
                _log.Error("Zugangsdaten", CredentialStatus, exception);
            }
        }

        private void RefreshCredentialStatus()
        {
            var status = _credentialConfiguration.ReadStatus();
            HasKanbanizeApiKey = status.HasKanbanizeApiKey;
            HasRemoteDesktopPassword = status.HasRemoteDesktopPassword;
            HasFeeCredentials = status.HasFeeCredentials;
        }

        private async Task CheckServerAsync(string serverName)
        {
            if (string.IsNullOrWhiteSpace(serverName))
            {
                IsServerReachable = false;
                return;
            }

            var reachable = await RemoteConnection.CheckServerReachableAsync(serverName);
            if (!string.Equals(SelectedServer, serverName, StringComparison.OrdinalIgnoreCase))
                return;
            IsServerReachable = reachable;
            if (!reachable)
                _log.Warning("Project Settings", $"{serverName} antwortet nicht auf Ping. Ein Verbindungsversuch bleibt möglich.");
        }

        /// <summary>
        /// Rebuilds the FEE selector from the dynamic ViCo PC directory. The
        /// ping fan-out is bounded and debounced so a long list remains
        /// responsive while the user types a filter.
        /// </summary>
        private async Task RefreshOnlineServersAsync()
        {
            _serverFilterCancellation?.Cancel();
            var cancellation = new CancellationTokenSource();
            _serverFilterCancellation = cancellation;
            var version = Interlocked.Increment(ref _serverRefreshVersion);
            try
            {
                await Task.Delay(250, cancellation.Token);
                // "localhost" selects the local FEE endpoint, but is not a
                // useful workstation-name filter. Keep the complete online PC
                // list visible so the user can immediately choose another PC.
                var enteredText = ServerFilter.Trim();
                var filter = string.Equals(enteredText, "localhost", StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : enteredText;
                var candidates = _workstations.PcNames
                    .Prepend("localhost")
                    .Where(name => string.IsNullOrWhiteSpace(filter) ||
                        name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                using var throttle = new SemaphoreSlim(8);
                var checks = candidates.Select(async candidate =>
                {
                    await throttle.WaitAsync(cancellation.Token);
                    try
                    {
                        return (candidate, IsOnline: await _availability.PingAsync(candidate, cancellation.Token));
                    }
                    finally
                    {
                        throttle.Release();
                    }
                });
                var checksResult = await Task.WhenAll(checks);
                if (cancellation.IsCancellationRequested || version != _serverRefreshVersion)
                    return;

                var online = checksResult
                    .Where(result => result.IsOnline)
                    .Select(result => result.candidate)
                    .OrderBy(name => string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                await RunOnUiThreadAsync(() =>
                {
                    ReplaceServerNames(online);

                    if (string.IsNullOrWhiteSpace(filter) &&
                        !string.IsNullOrWhiteSpace(SelectedServer) &&
                        !string.Equals(SelectedServer, "localhost", StringComparison.OrdinalIgnoreCase) &&
                        !checksResult.Any(result =>
                            result.IsOnline &&
                            string.Equals(result.candidate, SelectedServer, StringComparison.OrdinalIgnoreCase)))
                    {
                        _isServerChangeActive = true;
                        SelectedServer = string.Empty;
                        _isServerChangeActive = false;
                        IsServerReachable = false;
                        ConnectionStatus = "Der zuvor ausgewählte PC ist offline und wurde aus der Liste entfernt.";
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // A newer filter input superseded this scan.
            }
            catch (Exception exception)
            {
                _log.Error("Project Settings", "Die Online-PC-Liste konnte nicht aktualisiert werden.", exception);
            }
            finally
            {
                if (ReferenceEquals(_serverFilterCancellation, cancellation))
                    _serverFilterCancellation = null;
                cancellation.Dispose();
            }
        }

        private void ReplaceServerNames(IEnumerable<string> names)
        {
            var desired = names.ToArray();
            for (var index = ServerNames.Count - 1; index >= 0; index--)
            {
                if (!desired.Contains(ServerNames[index], StringComparer.OrdinalIgnoreCase))
                    ServerNames.RemoveAt(index);
            }

            for (var index = 0; index < desired.Length; index++)
            {
                var currentIndex = ServerNames.IndexOf(desired[index]);
                if (currentIndex < 0)
                    ServerNames.Insert(Math.Min(index, ServerNames.Count), desired[index]);
                else if (currentIndex != index)
                    ServerNames.Move(currentIndex, index);
            }
        }

        private static async Task RunOnUiThreadAsync(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            await dispatcher.InvokeAsync(action);
        }


        private void OnFeeObjectsLoaded(object? sender, FeeObjectsUpdatedEventargs e)
        {
            FeeObjectStatus = $"Total time reading Fee data: {e.ElapsedTime.TotalSeconds.ToString("F2")}s";
        }







        //===========================================================================================================================
        // E V E N T S
        //===========================================================================================================================

        private void OnConnected()
        {
            // Connect_ToFee updates the UI only after the SDK state has been
            // confirmed. Do not overwrite that result from the timer event.
        }

        private async Task DisconnectAfterFailedConnectionAsync()
        {
            if (Services.ApiInstance is null)
                return;

            try
            {
                Services.ApiInstance.Disconnect();
                await _connectionService.WaitForDisconnectedAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Preserve the original connection failure for the log.
            }
        }

    }
}
