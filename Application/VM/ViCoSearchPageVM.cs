using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Collections;
using System.Windows.Input;
using VIBN_Tools.Core.ViCo;
using VIBN_Tools.GlobalClasses;

namespace VIBN_Tools.Application.VM;

/// <summary>
/// Coordinates unified workstation search, cache refresh, online availability
/// and the actions that open a selected workstation/project.
/// </summary>
public sealed class ViCoSearchPageVM : MvvmBase, IDisposable
{
    private readonly IViCoWorkstationCatalog _catalog;
    private readonly IViCoWorkstationSearch _search;
    private readonly Func<CancellationToken, Task<IViCoRelatedPathResolver>> _pathResolverFactory;
    private readonly INetworkAvailabilityService _network;
    private readonly IRemoteDesktopService _remoteDesktop;
    private readonly IRemoteSessionService _remoteSessions;
    private readonly IExternalPathLauncher _launcher;
    private readonly IViCoOnlineRefreshService _onlineRefresh;
    private readonly IViCoWorkstationConfigurationService _configurationService;
    private readonly IViCoAutoRefreshSettingsStore _autoRefreshSettingsStore;
    private readonly ViCoWorkspaceContext _workspaceContext;
    private readonly Action<IEnumerable<ViCoWorkstation>> _synchronizeWorkstations;
    private readonly IApplicationLog _log;
    private IReadOnlyList<ViCoWorkstation> _allWorkstations = Array.Empty<ViCoWorkstation>();
    private IViCoRelatedPathResolver? _pathResolver;
    private CancellationTokenSource? _availabilityCancellation;
    private CancellationTokenSource? _searchDebounceCancellation;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ConcurrentDictionary<string, (bool IsOnline, DateTimeOffset CheckedAt)> _availabilityCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (ViCoRemoteSessionInfo Info, DateTimeOffset CheckedAt)> _remoteSessionCache =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;
    private bool _isSavingConfiguration;
    private DateTimeOffset? _nextAutoRefreshAt;
    private bool? _lastObservedOnlineConfiguration;
    private IReadOnlyList<ViCoWorkstationRowVM> _selectedWorkstations = Array.Empty<ViCoWorkstationRowVM>();
    private bool _columnPreferencesLoaded;

    public const string KanbanizeBoardUrl = "https://grobgroup.kanbanize.com/ctrl_board/1541";

    public ViCoSearchPageVM(
        IViCoWorkstationCatalog catalog,
        IViCoWorkstationSearch search,
        Func<CancellationToken, Task<IViCoRelatedPathResolver>> pathResolverFactory,
        INetworkAvailabilityService network,
        IRemoteDesktopService remoteDesktop,
        IRemoteSessionService remoteSessions,
        IExternalPathLauncher launcher,
        IViCoOnlineRefreshService onlineRefresh,
        IViCoWorkstationConfigurationService configurationService,
        IViCoAutoRefreshSettingsStore autoRefreshSettingsStore,
        ViCoWorkspaceContext workspaceContext,
        Action<IEnumerable<ViCoWorkstation>> synchronizeWorkstations,
        IApplicationLog? log = null)
    {
        _catalog = catalog;
        _search = search;
        _pathResolverFactory = pathResolverFactory;
        _network = network;
        _remoteDesktop = remoteDesktop;
        _remoteSessions = remoteSessions;
        _launcher = launcher;
        _onlineRefresh = onlineRefresh;
        _configurationService = configurationService;
        _autoRefreshSettingsStore = autoRefreshSettingsStore;
        _workspaceContext = workspaceContext;
        _synchronizeWorkstations = synchronizeWorkstations;
        _log = log ?? NullApplicationLog.Instance;

        RefreshCommand = GetCommandBindingAsync(RefreshFromBestAvailableSourceAsync);
        ConnectRemoteCommand = GetCommandBinding(ConnectRemote);
        ConnectRemoteWithPromptCommand = GetCommandBinding(ConnectRemoteWithPrompt);
        SaveConfigurationCommand = GetCommandBindingAsync(SaveConfigurationAsync);
        CreateConfigurationCommand = GetCommandBindingAsync(CreateConfigurationAsync);
        SaveAutoRefreshIntervalCommand = GetCommandBindingAsync(SaveAutoRefreshIntervalAsync);
        SaveDisplayPreferencesCommand = GetCommandBindingAsync(SaveDisplayPreferencesAsync);
        OpenPcProjectsCommand = GetCommandBinding(() => OpenRelated(ViCoRelatedPathKind.WorkstationProjects));
        OpenSimulationCommand = GetCommandBinding(() => OpenRelated(ViCoRelatedPathKind.Simulation));
        OpenCommissioningCommand = GetCommandBinding(() => OpenRelated(ViCoRelatedPathKind.Commissioning));
        OpenPlanningCommand = GetCommandBinding(() => OpenRelated(ViCoRelatedPathKind.Planning));
        ContextConnectRemoteCommand = GetCommandBinding(parameter => ExecuteForRow(parameter, ConnectRemote));
        ContextConnectRemoteWithPromptCommand = GetCommandBinding(parameter => ExecuteForRow(parameter, ConnectRemoteWithPrompt));
        ContextOpenPcProjectsCommand = GetCommandBinding(parameter => ExecuteForRow(parameter, () => OpenRelated(ViCoRelatedPathKind.WorkstationProjects)));
        ContextOpenSimulationCommand = GetCommandBinding(parameter => ExecuteForRow(parameter, () => OpenRelated(ViCoRelatedPathKind.Simulation)));
        ContextOpenCommissioningCommand = GetCommandBinding(parameter => ExecuteForRow(parameter, () => OpenRelated(ViCoRelatedPathKind.Commissioning)));
        ContextOpenPlanningCommand = GetCommandBinding(parameter => ExecuteForRow(parameter, () => OpenRelated(ViCoRelatedPathKind.Planning)));
        OpenKanbanizeCardCommand = GetCommandBinding(OpenKanbanizeCard);

        OccupancyColumn = AddColumn("occupancy", "Belegung", true);
        PcColumn = AddColumn("pc", "PC", true);
        OnlineColumn = AddColumn("online", "Online", true);
        PlanningColumn = AddColumn("planning", "Planung", true);
        WorkingColumn = AddColumn("working", "In Arbeit", true);
        PlanningStartColumn = AddColumn("planningStart", "Startdatum Planung", true);
        PlanningEndColumn = AddColumn("planningEnd", "Enddatum Planung", true);
        WorkingStartColumn = AddColumn("workingStart", "Startdatum In Arbeit", true);
        WorkingEndColumn = AddColumn("workingEnd", "Enddatum In Arbeit", true);
        CompletedColumn = AddColumn("completed", "Abgeschlossene Projekte", true);
        SoftwareColumn = AddColumn("software", "Software", true);
        UserColumn = AddColumn("user", "Benutzer", true);
        LocationColumn = AddColumn("location", "Standort", true);
        OtherColumn = AddColumn("other", "Sonstiges", false);
        ProjectIpColumn = AddColumn("projectIp", "Projekt-IP", false);
    }

    public ObservableCollection<ViCoWorkstationRowVM> Results { get; } = new();
    public ObservableCollection<string> Projects { get; } = new();
    public ObservableCollection<ViCoConfigurationFieldVM> ConfigurationFields { get; } = new();
    public ObservableCollection<ViCoColumnOptionVM> ColumnOptions { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand ConnectRemoteCommand { get; }
    public ICommand ConnectRemoteWithPromptCommand { get; }
    public ICommand SaveConfigurationCommand { get; }
    public ICommand CreateConfigurationCommand { get; }
    public ICommand SaveAutoRefreshIntervalCommand { get; }
    public ICommand SaveDisplayPreferencesCommand { get; }
    public ICommand OpenPcProjectsCommand { get; }
    public ICommand OpenSimulationCommand { get; }
    public ICommand OpenCommissioningCommand { get; }
    public ICommand OpenPlanningCommand { get; }
    public ICommand ContextConnectRemoteCommand { get; }
    public ICommand ContextConnectRemoteWithPromptCommand { get; }
    public ICommand ContextOpenPcProjectsCommand { get; }
    public ICommand ContextOpenSimulationCommand { get; }
    public ICommand ContextOpenCommissioningCommand { get; }
    public ICommand ContextOpenPlanningCommand { get; }
    public ICommand OpenKanbanizeCardCommand { get; }
    public ViCoColumnOptionVM OccupancyColumn { get; }
    public ViCoColumnOptionVM PcColumn { get; }
    public ViCoColumnOptionVM OnlineColumn { get; }
    public ViCoColumnOptionVM PlanningColumn { get; }
    public ViCoColumnOptionVM WorkingColumn { get; }
    public ViCoColumnOptionVM PlanningStartColumn { get; }
    public ViCoColumnOptionVM PlanningEndColumn { get; }
    public ViCoColumnOptionVM WorkingStartColumn { get; }
    public ViCoColumnOptionVM WorkingEndColumn { get; }
    public ViCoColumnOptionVM CompletedColumn { get; }
    public ViCoColumnOptionVM SoftwareColumn { get; }
    public ViCoColumnOptionVM UserColumn { get; }
    public ViCoColumnOptionVM LocationColumn { get; }
    public ViCoColumnOptionVM OtherColumn { get; }
    public ViCoColumnOptionVM ProjectIpColumn { get; }
    public int MonitorCount => _remoteDesktop.MonitorCount;
    public bool HasMonitor2 => MonitorCount >= 2;
    public bool HasMonitor3 => MonitorCount >= 3;
    public bool HasMonitor4 => MonitorCount >= 4;
    public bool UseMonitor1 { get; set; } = true;
    public bool UseMonitor2 { get; set; }
    public bool UseMonitor3 { get; set; }
    public bool UseMonitor4 { get; set; }

    private int _autoRefreshIntervalMinutes = ViCoAutoRefreshSettings.Default.IntervalMinutes;
    public int AutoRefreshIntervalMinutes
    {
        get => _autoRefreshIntervalMinutes;
        set
        {
            _autoRefreshIntervalMinutes = value;
            OnPropertyChanged();
        }
    }

    private bool _showExtendedInformation;
    public bool ShowExtendedInformation
    {
        get => _showExtendedInformation;
        set
        {
            if (_showExtendedInformation == value)
                return;
            _showExtendedInformation = value;
            OnPropertyChanged();
            OtherColumn?.Apply(value);
            ProjectIpColumn?.Apply(value);
        }
    }

    private string _autoRefreshCountdown = "AutoUpdate wird initialisiert …";
    public string AutoRefreshCountdown
    {
        get => _autoRefreshCountdown;
        private set
        {
            _autoRefreshCountdown = value;
            OnPropertyChanged();
        }
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            _searchText = value;
            OnPropertyChanged();
            _ = ApplySearchDebouncedAsync();
        }
    }

    private ViCoSearchMode _searchMode = ViCoSearchMode.All;
    public ViCoSearchMode SearchMode
    {
        get => _searchMode;
        set
        {
            _searchMode = value;
            OnPropertyChanged();
            ApplySearch();
        }
    }

    private ViCoWorkstationRowVM? _selectedWorkstation;
    public ViCoWorkstationRowVM? SelectedWorkstation
    {
        get => _selectedWorkstation;
        set
        {
            _selectedWorkstation = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedRemoteUser));
            OnPropertyChanged(nameof(HasSelectedWorkstation));
            OnPropertyChanged(nameof(CanUseSelectedWorkstationActions));
            OnPropertyChanged(nameof(CanUseRemoteActions));
            OnPropertyChanged(nameof(CanOpenPcProjects));
            OnPropertyChanged(nameof(CanOpenServerPathActions));
            OnPropertyChanged(nameof(RemoteActionUnavailableReason));
            OnPropertyChanged(nameof(PcProjectsUnavailableReason));
            OnPropertyChanged(nameof(ServerPathActionUnavailableReason));
            OnPropertyChanged(nameof(IsSelectedWorkstationOffline));
            OnPropertyChanged(nameof(CanEditConfiguration));
            OnPropertyChanged(nameof(CanCreateConfiguration));
            OnPropertyChanged(nameof(EditConfigurationUnavailableReason));
            OnPropertyChanged(nameof(CreateConfigurationUnavailableReason));
            OnPropertyChanged(nameof(HasSelectedConfigurationCard));
            OnPropertyChanged(nameof(IsSelectedConfigurationMissing));
            Projects.Clear();
            ConfigurationFields.Clear();
            if (value is not null)
            {
                foreach (var project in value.Model.Projects
                             .Select(ProjectIdentity.CleanDisplay)
                             .Where(project => project.Length > 0)
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                    Projects.Add(project);
                foreach (var configurationField in value.Model.WorkstationConfiguration.Fields)
                    ConfigurationFields.Add(new ViCoConfigurationFieldVM(configurationField));
                SelectedProject = Projects.FirstOrDefault();
            }
            else
            {
                SelectedProject = null;
            }
            UpdatePathInformation();
        }
    }

    public string SelectedRemoteUser => SelectedWorkstation?.UserName ?? string.Empty;

    public bool HasSelectedWorkstation => SelectedWorkstation is not null;

    /// <summary>Compatibility property for callers that require the workstation itself to be online.</summary>
    public bool CanUseSelectedWorkstationActions => SelectedWorkstation?.IsOnline == true;

    public bool CanUseRemoteActions => ActionRows.Count > 0 && ActionRows.All(row => row.IsOnline);

    public bool CanOpenPcProjects =>
        ActionRows.Count > 0 &&
        ActionRows.All(row => row.IsOnline) &&
        _pathResolver is not null;

    public bool CanOpenServerPathActions =>
        ActionRows.Count > 0 &&
        ActionRows.All(row => row.HasActiveProjects) &&
        _pathResolver is not null;

    public string RemoteActionUnavailableReason => ActionRows.Count == 0
        ? "Zuerst einen Arbeitsplatz auswählen."
        : ActionRows.All(row => row.IsOnline)
            ? "Remote Desktop öffnen."
            : "Remote Desktop ist deaktiviert, weil mindestens ein ausgewählter PC offline ist.";

    public string PcProjectsUnavailableReason => ActionRows.Count == 0
        ? "Zuerst einen Arbeitsplatz auswählen."
        : ActionRows.Any(row => !row.IsOnline)
            ? "Der PC-Projektordner ist deaktiviert, weil mindestens ein ausgewählter PC offline ist."
            : _pathResolver is null
                ? "Die Projektpfade wurden noch nicht geladen."
                : "Projektordner auf den ausgewählten PCs öffnen.";

    public string ServerPathActionUnavailableReason => ActionRows.Count == 0
        ? "Zuerst einen Arbeitsplatz auswählen."
        : ActionRows.Any(row => !row.HasActiveProjects)
            ? "Mindestens ein ausgewählter Arbeitsplatz hat weder ein Projekt in Planung noch in Arbeit."
        : _pathResolver is null
            ? "Die Serverpfade wurden noch nicht geladen."
            : "Die Serverpfade der ausgewählten Arbeitsplätze öffnen.";

    public bool IsSelectedWorkstationOffline =>
        SelectedWorkstation is not null && !SelectedWorkstation.IsOnline;

    public void SetSelectedWorkstations(IList selectedItems)
    {
        _selectedWorkstations = selectedItems
            .OfType<ViCoWorkstationRowVM>()
            .Distinct()
            .ToArray();
        NotifyActionAvailabilityChanged();
    }

    /// <summary>An existing configuration card can be edited; missing standard subtasks are added on save.</summary>
    public bool CanEditConfiguration =>
        _configurationService.IsConfigured &&
        SelectedWorkstation?.Model.WorkstationConfiguration.IsEditable == true;

    public bool CanCreateConfiguration =>
        _configurationService.IsConfigured &&
        SelectedWorkstation is not null &&
        !SelectedWorkstation.Model.HasConfigurationCard &&
        SelectedWorkstation.Model.KanbanizeLaneId > 0 &&
        SelectedWorkstation.Model.ConfigurationColumnId > 0;

    public string EditConfigurationUnavailableReason => CanEditConfiguration
        ? "Speichert die bearbeitbaren Felder der vorhandenen KONFIGURATION-Karte."
        : !_configurationService.IsConfigured
            ? "Kanbanize ist nicht konfiguriert; API-Schlüssel in Project Settings speichern."
            : SelectedWorkstation is null
                ? "Zuerst einen Arbeitsplatz auswählen."
                : "Für diesen Arbeitsplatz ist keine bearbeitbare KONFIGURATION-Karte vorhanden.";

    public string CreateConfigurationUnavailableReason => CanCreateConfiguration
        ? "Legt die standardisierte KONFIGURATION-Karte für den ausgewählten Arbeitsplatz an."
        : !_configurationService.IsConfigured
            ? "Kanbanize ist nicht konfiguriert; API-Schlüssel in Project Settings speichern."
            : SelectedWorkstation is null
                ? "Zuerst einen Arbeitsplatz auswählen."
                : SelectedWorkstation.Model.HasConfigurationCard
                    ? "Für diesen Arbeitsplatz ist bereits eine KONFIGURATION-Karte vorhanden."
                    : "Lane oder Zielspalte der Arbeitsplätze-Karte ist nicht eindeutig ermittelbar.";

    public bool HasSelectedConfigurationCard => SelectedWorkstation?.Model.HasConfigurationCard == true;

    public bool IsSelectedConfigurationMissing =>
        SelectedWorkstation is not null && !SelectedWorkstation.Model.HasConfigurationCard;

    private string? _selectedProject;
    public string? SelectedProject
    {
        get => _selectedProject;
        set
        {
            _selectedProject = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedProjectStart));
            OnPropertyChanged(nameof(SelectedProjectEnd));
            UpdatePathInformation();
        }
    }

    public string SelectedProjectStart => FormatProjectDate(FindSelectedProjectCard()?.StartDate);

    public string SelectedProjectEnd => FormatProjectDate(FindSelectedProjectCard()?.Deadline);

    private string _pathInformation = "PC und Projekt auswählen.";
    public string PathInformation
    {
        get => _pathInformation;
        private set
        {
            _pathInformation = value;
            OnPropertyChanged();
        }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            OnPropertyChanged();
        }
    }

    private string _statusText = "ViCo-Suche ist bereit.";
    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
            return;
        _initialized = true;
        await LoadAutoRefreshSettingsAsync();
        await RefreshCachedDataAsync();
        ScheduleNextAutoRefresh();
        _ = RunPeriodicRefreshAsync(_lifetimeCancellation.Token);
    }

    public void Dispose()
    {
        _availabilityCancellation?.Cancel();
        _availabilityCancellation?.Dispose();
        _searchDebounceCancellation?.Cancel();
        _searchDebounceCancellation?.Dispose();
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }

    /// <summary>
    /// Uses a configured online source first, then always reloads the resulting
    /// local cache. Without an API key this still provides a useful cache refresh.
    /// </summary>
    private async Task RefreshFromBestAvailableSourceAsync()
    {
        if (IsBusy)
            return;
        if (!_onlineRefresh.IsConfigured)
        {
            await RefreshCachedDataAsync();
            return;
        }

        IsBusy = true;
        StatusText = "Kanbanize-Daten werden aktualisiert …";
        var onlineUpdateSucceeded = false;
        try
        {
            await _onlineRefresh.RefreshAsync();
            onlineUpdateSucceeded = true;
            _log.Information("Kanbanize", "PC-, Projekt- und Robotikdaten wurden aktualisiert.");
        }
        catch (Exception exception)
        {
            _log.Error("Kanbanize", "Die Online-Aktualisierung ist fehlgeschlagen; der vorhandene Cache wird verwendet.", exception);
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshCachedDataAsync(onlineUpdateSucceeded
            ? null
            : "Online-Aktualisierung fehlgeschlagen; vorhandener Cache wurde geladen.");
        ScheduleNextAutoRefresh();
    }

    /// <summary>Reads the existing cache and rebuilds search/path state without a network write.</summary>
    private async Task RefreshCachedDataAsync(string? completionMessage = null)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        StatusText = "PC- und Projektdaten werden geladen …";
        try
        {
            var catalogTask = _catalog.LoadAsync();
            var resolverTask = _pathResolverFactory(CancellationToken.None);
            await Task.WhenAll(catalogTask, resolverTask);
            var snapshot = await catalogTask;
            _pathResolver = await resolverTask;
            _allWorkstations = snapshot.Workstations;
            _synchronizeWorkstations(_allWorkstations);
            OnPropertyChanged(nameof(CanOpenPcProjects));
            OnPropertyChanged(nameof(CanOpenServerPathActions));
            OnPropertyChanged(nameof(PcProjectsUnavailableReason));
            OnPropertyChanged(nameof(ServerPathActionUnavailableReason));
            ApplySearch();
            StatusText = completionMessage ?? (snapshot.Warnings.Count == 0
                ? $"{_allWorkstations.Count} Arbeitsstationen geladen. Kanbanize-Benutzer wurden synchronisiert."
                : $"{_allWorkstations.Count} Arbeitsstationen geladen; {snapshot.Warnings.Count} Datenquelle(n) nicht erreichbar.");
            _log.Information("ViCo-Suche", StatusText);
            foreach (var warning in snapshot.Warnings)
                _log.Warning("ViCo-Suche", "Eine Datenquelle konnte nicht gelesen werden.", warning);
        }
        catch (Exception exception)
        {
            StatusText = "PC- und Projektdaten konnten nicht geladen werden.";
            _log.Error("ViCo-Suche", StatusText, exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunPeriodicRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isOnlineConfigured = _onlineRefresh.IsConfigured;
                if (_lastObservedOnlineConfiguration != isOnlineConfigured)
                {
                    _lastObservedOnlineConfiguration = isOnlineConfigured;
                    OnPropertyChanged(nameof(CanEditConfiguration));
                    OnPropertyChanged(nameof(CanCreateConfiguration));
                    OnPropertyChanged(nameof(EditConfigurationUnavailableReason));
                    OnPropertyChanged(nameof(CreateConfigurationUnavailableReason));
                }

                if (!isOnlineConfigured)
                {
                    _nextAutoRefreshAt = null;
                    AutoRefreshCountdown = "AutoUpdate pausiert – Kanbanize API-Key fehlt.";
                }
                else
                {
                    _nextAutoRefreshAt ??= DateTimeOffset.Now.AddMinutes(AutoRefreshIntervalMinutes);
                    var remaining = _nextAutoRefreshAt.Value - DateTimeOffset.Now;
                    if (remaining <= TimeSpan.Zero)
                    {
                        if (IsBusy)
                        {
                            AutoRefreshCountdown = "AutoUpdate wartet auf laufenden Vorgang …";
                        }
                        else
                        {
                            AutoRefreshCountdown = "Kanbanize-AutoUpdate läuft …";
                            await RefreshFromBestAvailableSourceAsync();
                        }
                    }
                    else
                    {
                        AutoRefreshCountdown = $"Nächstes Kanbanize-AutoUpdate: {FormatRemaining(remaining)}";
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Application shutdown.
        }
    }

    private void ApplySearch()
    {
        var selected = SelectedWorkstation?.PcName;
        Results.Clear();
        foreach (var hit in _search.SearchWithMatches(_allWorkstations, SearchText, SearchMode))
            Results.Add(new ViCoWorkstationRowVM(hit.Workstation, hit.MatchedColumns));
        SelectedWorkstation = Results.FirstOrDefault(item =>
            string.Equals(item.PcName, selected, StringComparison.OrdinalIgnoreCase)) ?? Results.FirstOrDefault();
        StartAvailabilityRefresh();
    }

    private async Task ApplySearchDebouncedAsync()
    {
        _searchDebounceCancellation?.Cancel();
        _searchDebounceCancellation?.Dispose();
        _searchDebounceCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        try
        {
            await Task.Delay(300, _searchDebounceCancellation.Token);
            ApplySearch();
        }
        catch (OperationCanceledException)
        {
            // A newer search text superseded this update.
        }
    }

    private void StartAvailabilityRefresh()
    {
        _availabilityCancellation?.Cancel();
        _availabilityCancellation?.Dispose();
        _availabilityCancellation = new CancellationTokenSource();
        _ = RefreshAvailabilityAsync(Results.ToArray(), _availabilityCancellation.Token);
    }

    private async Task RefreshAvailabilityAsync(
        IReadOnlyCollection<ViCoWorkstationRowVM> rows,
        CancellationToken cancellationToken)
    {
        using var pingThrottle = new SemaphoreSlim(8);
        using var sessionThrottle = new SemaphoreSlim(4);
        var tasks = rows.Select(async row =>
        {
            try
            {
                bool isOnline;
                if (_availabilityCache.TryGetValue(row.PcName, out var cached) &&
                    DateTimeOffset.Now - cached.CheckedAt < TimeSpan.FromSeconds(30))
                {
                    isOnline = cached.IsOnline;
                }
                else
                {
                    // Do not keep one of the limited ping slots while the
                    // optional, slower RDP-session query is running. This is
                    // significant for desktop users with many workstations.
                    await pingThrottle.WaitAsync(cancellationToken);
                    try
                    {
                        isOnline = await _network.PingAsync(row.PcName, cancellationToken);
                        _availabilityCache[row.PcName] = (isOnline, DateTimeOffset.Now);
                    }
                    finally
                    {
                        pingThrottle.Release();
                    }
                }

                row.SetOnline(isOnline);
                if (isOnline)
                    await RefreshRemoteSessionAsync(row, sessionThrottle, cancellationToken);
                NotifySelectedWorkstationAvailabilityChanged(row);
            }
            catch (OperationCanceledException)
            {
                // A new search superseded this availability scan.
            }
            catch (Exception exception)
            {
                // Availability is a best-effort enhancement. A malformed
                // hostname or transient network failure must not abort the
                // refresh for the other workstations.
                row.SetOnline(false);
                row.SetRemoteSession(ViCoRemoteSessionInfo.NotAvailable);
                NotifySelectedWorkstationAvailabilityChanged(row);
                _log.Warning("Verfügbarkeit", $"Status für {row.PcName} konnte nicht ermittelt werden.", exception.Message);
            }
        });
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // A new search superseded this availability scan.
        }
    }

    private async Task RefreshRemoteSessionAsync(
        ViCoWorkstationRowVM row,
        SemaphoreSlim throttle,
        CancellationToken cancellationToken)
    {
        if (_remoteSessionCache.TryGetValue(row.PcName, out var cached) &&
            DateTimeOffset.Now - cached.CheckedAt < TimeSpan.FromMinutes(2))
        {
            row.SetRemoteSession(cached.Info);
            return;
        }

        var acquired = false;
        try
        {
            await throttle.WaitAsync(cancellationToken);
            acquired = true;
            var info = await _remoteSessions.GetSessionInfoAsync(row.PcName, cancellationToken);
            _remoteSessionCache[row.PcName] = (info, DateTimeOffset.Now);
            row.SetRemoteSession(info);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The session query is optional. It must never change the actual
            // network availability state or block all remaining PCs.
            row.SetRemoteSession(ViCoRemoteSessionInfo.NotAvailable);
            _log.Warning("Remote-Sitzung", $"Sitzungsstatus für {row.PcName} ist nicht abrufbar.", exception.Message);
        }
        finally
        {
            if (acquired)
                throttle.Release();
        }
    }

    private void NotifySelectedWorkstationAvailabilityChanged(ViCoWorkstationRowVM row)
    {
        if (ReferenceEquals(row, SelectedWorkstation) || _selectedWorkstations.Contains(row))
        {
            OnPropertyChanged(nameof(CanUseSelectedWorkstationActions));
            OnPropertyChanged(nameof(CanUseRemoteActions));
            OnPropertyChanged(nameof(CanOpenPcProjects));
            OnPropertyChanged(nameof(IsSelectedWorkstationOffline));
            OnPropertyChanged(nameof(RemoteActionUnavailableReason));
            OnPropertyChanged(nameof(PcProjectsUnavailableReason));
        }
    }

    private void ConnectRemote()
    {
        StartRemote(promptForCredentials: false);
    }

    private void ConnectRemoteWithPrompt()
    {
        StartRemote(promptForCredentials: true);
    }

    private void StartRemote(bool promptForCredentials)
    {
        var rows = ActionRows;
        if (rows.Count == 0)
            return;
        if (!rows.All(row => row.IsOnline))
        {
            StatusText = RemoteActionUnavailableReason;
            return;
        }
        var missingUsers = rows
            .Where(row => !promptForCredentials && string.IsNullOrWhiteSpace(row.UserName))
            .Select(row => row.PcName)
            .ToArray();
        if (missingUsers.Length > 0)
        {
            StatusText = $"Kein gültiger Remote-Benutzer für: {string.Join(", ", missingUsers)}.";
            _log.Warning("Remote Desktop", StatusText);
            return;
        }

        var monitors = new[] { UseMonitor1, UseMonitor2, UseMonitor3, UseMonitor4 }
            .Select((selected, index) => (selected, index))
            .Where(value => value.selected)
            .Select(value => value.index)
            .ToArray();
        var started = new List<string>();
        var failed = new List<string>();
        foreach (var row in rows)
        {
            try
            {
                if (promptForCredentials)
                    _remoteDesktop.ConnectWithCredentialPrompt(row.PcName, row.UserName, monitors);
                else
                    _remoteDesktop.Connect(row.PcName, row.UserName, monitors);
                started.Add(row.PcName);
                _log.Information("Remote Desktop", $"Verbindung zu {row.PcName} gestartet.");
            }
            catch (Exception exception)
            {
                failed.Add($"{row.PcName}: {exception.Message}");
                _log.Error("Remote Desktop", $"Verbindung zu {row.PcName} konnte nicht gestartet werden.", exception);
            }
        }

        StatusText = failed.Count == 0
            ? $"Remote Desktop für {started.Count} Arbeitsplatz/Arbeitsplätze gestartet."
            : $"{started.Count} RDP-Verbindung(en) gestartet; {failed.Count} fehlgeschlagen: {string.Join(" | ", failed)}";
    }

    private async Task SaveConfigurationAsync()
    {
        if (_isSavingConfiguration)
            return;

        if (!CanEditConfiguration || SelectedWorkstation is null)
        {
            StatusText = "Für diesen Arbeitsplatz ist keine bearbeitbare KONFIGURATION-Karte vorhanden.";
            return;
        }

        var changedFields = ConfigurationFields
            .Where(field => field.IsChanged || !field.CanSave)
            .Select(field => field.ToField())
            .ToArray();
        if (changedFields.Length == 0)
        {
            StatusText = "Keine geänderten KONFIGURATION-Werte zum Speichern vorhanden.";
            return;
        }

        _isSavingConfiguration = true;
        try
        {
            var currentConfiguration = SelectedWorkstation.Model.WorkstationConfiguration;
            await _configurationService.SaveFieldsAsync(
                currentConfiguration.CardId,
                changedFields,
                _lifetimeCancellation.Token);

            var configuration = BuildUpdatedConfiguration(currentConfiguration, ConfigurationFields);
            SelectedWorkstation.UpdateConfiguration(configuration);
            _allWorkstations = _allWorkstations
                .Select(workstation => string.Equals(
                    workstation.PcName,
                    SelectedWorkstation.PcName,
                    StringComparison.OrdinalIgnoreCase)
                    ? SelectedWorkstation.Model
                    : workstation)
                .ToArray();
            _synchronizeWorkstations(_allWorkstations);
            foreach (var field in ConfigurationFields)
                field.AcceptSavedValue();
            OnPropertyChanged(nameof(SelectedRemoteUser));
            StatusText = $"{changedFields.Length} KONFIGURATION-Wert(e) wurden in Kanbanize gespeichert.";
            _log.Information("Kanbanize", StatusText);
        }
        catch (OperationCanceledException)
        {
            // Application shutdown cancels only the pending external request.
        }
        catch (Exception exception)
        {
            StatusText = "KONFIGURATION-Werte konnten nicht gespeichert werden.";
            _log.Error("Kanbanize", StatusText, exception);
        }
        finally
        {
            _isSavingConfiguration = false;
        }
    }

    private async Task LoadAutoRefreshSettingsAsync()
    {
        try
        {
            var settings = await _autoRefreshSettingsStore.LoadAsync(_lifetimeCancellation.Token);
            AutoRefreshIntervalMinutes = ViCoAutoRefreshPolicy.Normalize(settings.IntervalMinutes);
            ApplyColumnPreferences(settings);
            _columnPreferencesLoaded = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AutoRefreshIntervalMinutes = ViCoAutoRefreshSettings.Default.IntervalMinutes;
            ApplyColumnPreferences(ViCoAutoRefreshSettings.Default);
            _columnPreferencesLoaded = true;
            _log.Warning(
                "ViCo AutoUpdate",
                "Das gespeicherte Aktualisierungsintervall konnte nicht gelesen werden; fünf Minuten werden verwendet.",
                exception.Message);
        }
    }

    private async Task SaveAutoRefreshIntervalAsync()
    {
        var normalized = ViCoAutoRefreshPolicy.Normalize(AutoRefreshIntervalMinutes);
        AutoRefreshIntervalMinutes = normalized;
        try
        {
            await _autoRefreshSettingsStore.SaveAsync(
                BuildDisplaySettings(normalized),
                _lifetimeCancellation.Token);
            ScheduleNextAutoRefresh();
            StatusText = $"Kanbanize-AutoUpdate wird alle {normalized} Minute(n) ausgeführt.";
            _log.Information("ViCo AutoUpdate", StatusText);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StatusText = "Das Kanbanize-AutoUpdate-Intervall konnte nicht gespeichert werden.";
            _log.Error("ViCo AutoUpdate", StatusText, exception);
        }
    }

    private async Task SaveDisplayPreferencesAsync()
    {
        try
        {
            await _autoRefreshSettingsStore.SaveAsync(
                BuildDisplaySettings(ViCoAutoRefreshPolicy.Normalize(AutoRefreshIntervalMinutes)),
                _lifetimeCancellation.Token);
            StatusText = $"{ColumnOptions.Count(column => column.IsVisible)} ViCo-Spalte(n) werden angezeigt.";
            _log.Information("ViCo Anzeige", StatusText);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StatusText = "Die ViCo-Anzeigeeinstellung konnte nicht gespeichert werden.";
            _log.Error("ViCo Anzeige", StatusText, exception);
        }
    }

    private void ScheduleNextAutoRefresh()
    {
        _nextAutoRefreshAt = _onlineRefresh.IsConfigured
            ? DateTimeOffset.Now.AddMinutes(ViCoAutoRefreshPolicy.Normalize(AutoRefreshIntervalMinutes))
            : null;
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        var totalHours = Math.Max(0, (int)remaining.TotalHours);
        return totalHours > 0
            ? $"{totalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{Math.Max(0, remaining.Minutes):00}:{Math.Max(0, remaining.Seconds):00}";
    }

    private async Task CreateConfigurationAsync()
    {
        if (IsBusy)
            return;
        if (!CanCreateConfiguration || SelectedWorkstation is null)
        {
            StatusText = "KONFIGURATION kann nicht angelegt werden: Lane, Zielspalte oder Kanbanize-Zugriff fehlt.";
            return;
        }

        var pcName = SelectedWorkstation.PcName;
        try
        {
            IsBusy = true;
            StatusText = "Standardisierte KONFIGURATION-Karte wird angelegt …";
            var cardId = await _configurationService.CreateStandardAsync(
                SelectedWorkstation.Model.KanbanizeLaneId,
                SelectedWorkstation.Model.ConfigurationColumnId,
                ConfigurationFields.Select(field => field.ToField()).ToArray(),
                _lifetimeCancellation.Token);
            await _onlineRefresh.RefreshAsync(_lifetimeCancellation.Token);
            IsBusy = false;
            await RefreshCachedDataAsync($"KONFIGURATION-Karte {cardId} wurde angelegt und neu geladen.");
            SelectedWorkstation = Results.FirstOrDefault(row =>
                string.Equals(row.PcName, pcName, StringComparison.OrdinalIgnoreCase));
            _log.Information("Kanbanize", $"KONFIGURATION-Karte {cardId} für {pcName} wurde angelegt.");
        }
        catch (OperationCanceledException)
        {
            // Application shutdown cancels only the pending external request.
        }
        catch (Exception exception)
        {
            StatusText = $"KONFIGURATION-Karte konnte nicht angelegt werden: {exception.Message}";
            _log.Error("Kanbanize", "KONFIGURATION-Karte konnte nicht angelegt werden.", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static ViCoWorkstationConfiguration BuildUpdatedConfiguration(
        ViCoWorkstationConfiguration current,
        IEnumerable<ViCoConfigurationFieldVM> fields)
    {
        var byKey = fields.ToDictionary(field => field.Key, field => field.ToField(), StringComparer.OrdinalIgnoreCase);
        return new ViCoWorkstationConfiguration(
            current.CardId,
            byKey["USER"],
            byKey["STANDORT"],
            byKey["SW"],
            byKey["PROJEKT-IP"],
            byKey["SONSTIGES"]);
    }

    private void OpenRelated(ViCoRelatedPathKind kind)
    {
        var rows = ActionRows;
        if (rows.Count == 0 || _pathResolver is null)
            return;
        var requiresOnlineWorkstation = kind is ViCoRelatedPathKind.WorkstationProjects or
            ViCoRelatedPathKind.WorkstationProject;
        if (requiresOnlineWorkstation && rows.Any(row => !row.IsOnline))
        {
            StatusText = PcProjectsUnavailableReason;
            return;
        }
        if (!requiresOnlineWorkstation && rows.Any(row => !row.HasActiveProjects))
        {
            StatusText = "Mindestens ein ausgewählter Arbeitsplatz hat kein Projekt in Planung oder in Arbeit.";
            return;
        }

        var opened = new List<string>();
        var missing = new List<string>();
        foreach (var row in rows)
        {
            var project = ReferenceEquals(row, SelectedWorkstation) && !string.IsNullOrWhiteSpace(SelectedProject)
                ? SelectedProject
                : row.Model.PlanningProjects.Concat(row.Model.WorkingProjects).FirstOrDefault() ?? SearchText;
            var path = _pathResolver.Resolve(row.Model, project ?? string.Empty, kind);
            if (string.IsNullOrWhiteSpace(path))
            {
                missing.Add(row.PcName);
                continue;
            }
            _launcher.Open(path);
            opened.Add(path);
            _log.Information("ViCo-Pfade", $"Geöffnet: {path}");
        }
        StatusText = missing.Count == 0
            ? $"{opened.Count} Pfad(e) geöffnet."
            : $"{opened.Count} Pfad(e) geöffnet; kein passender Pfad für {string.Join(", ", missing)}.";
    }

    private void ExecuteForRow(object parameter, Action action)
    {
        if (parameter is not ViCoWorkstationRowVM row)
            return;
        if (!_selectedWorkstations.Contains(row))
        {
            _selectedWorkstations = new[] { row };
            SelectedWorkstation = row;
        }
        action();
    }

    private void OpenKanbanizeCard(object parameter)
    {
        if (parameter is not ViCoProjectCardItemVM { CanOpenCard: true } card)
        {
            StatusText = "Für diese Cache-Karte ist keine Kanbanize-Karten-ID verfügbar.";
            return;
        }

        var url = $"{KanbanizeBoardUrl}/cards/{card.CardId}/details/";
        _launcher.Open(url);
        StatusText = $"Kanbanize-Karte {card.CardId} wurde im Browser geöffnet.";
        _log.Information("Kanbanize", StatusText);
    }

    private ViCoProjectCardInfo? FindSelectedProjectCard()
    {
        if (SelectedWorkstation is null || string.IsNullOrWhiteSpace(SelectedProject))
            return null;

        var selectedIdentity = ProjectIdentity.Normalize(SelectedProject);
        return SelectedWorkstation.Model.ProjectCardDetails.FirstOrDefault(card =>
                   ProjectIdentity.Normalize(card.Title) == selectedIdentity)
               ?? SelectedWorkstation.Model.ProjectCardDetails.FirstOrDefault(card =>
                   ProjectIdentity.Normalize(card.Title).Contains(selectedIdentity, StringComparison.Ordinal) ||
                   selectedIdentity.Contains(ProjectIdentity.Normalize(card.Title), StringComparison.Ordinal));
    }

    private static string FormatProjectDate(DateTimeOffset? value) =>
        value is null ? "nicht angegeben" : value.Value.LocalDateTime.ToString("dd.MM.yyyy");

    private void UpdatePathInformation()
    {
        if (SelectedWorkstation is null || _pathResolver is null || string.IsNullOrWhiteSpace(SelectedProject))
        {
            PathInformation = "PC und Projekt auswählen.";
            return;
        }

        var workstation = SelectedWorkstation.Model;
        var simulation = _pathResolver.Resolve(workstation, SelectedProject, ViCoRelatedPathKind.Simulation);
        var commissioning = _pathResolver.Resolve(workstation, SelectedProject, ViCoRelatedPathKind.Commissioning);
        var planning = _pathResolver.Resolve(workstation, SelectedProject, ViCoRelatedPathKind.Planning);
        var workstationProject = _pathResolver.Resolve(workstation, SelectedProject, ViCoRelatedPathKind.WorkstationProject);
        PathInformation = string.Join(Environment.NewLine, new[]
        {
            Describe("PC-Projekt", workstationProject),
            Describe("Simulation", simulation),
            Describe("PLC", commissioning),
            Describe("Planung", planning)
        });
        _workspaceContext.Update(workstation, SelectedProject, simulation, workstationProject);
    }

    private static string Describe(string label, string? path) =>
        string.IsNullOrWhiteSpace(path) ? $"{label}: nicht gefunden" : $"{label}: {path}";

    private IReadOnlyList<ViCoWorkstationRowVM> ActionRows => _selectedWorkstations.Count > 0
        ? _selectedWorkstations
        : SelectedWorkstation is null
            ? Array.Empty<ViCoWorkstationRowVM>()
            : new[] { SelectedWorkstation };

    private ViCoColumnOptionVM AddColumn(string key, string title, bool isVisible)
    {
        var column = new ViCoColumnOptionVM(key, title, isVisible, OnColumnOptionChanged);
        ColumnOptions.Add(column);
        return column;
    }

    private void OnColumnOptionChanged()
    {
        _showExtendedInformation = OtherColumn?.IsVisible == true && ProjectIpColumn?.IsVisible == true;
        OnPropertyChanged(nameof(ShowExtendedInformation));
        if (_columnPreferencesLoaded)
            _ = SaveDisplayPreferencesAsync();
    }

    private void ApplyColumnPreferences(ViCoAutoRefreshSettings settings)
    {
        if (settings.VisibleColumns is { Count: > 0 })
        {
            var visible = settings.VisibleColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Migrate the two legacy combined date columns without discarding
            // an existing user's display preference.
            if (visible.Contains("start"))
            {
                visible.Add("planningStart");
                visible.Add("workingStart");
            }
            if (visible.Contains("end"))
            {
                visible.Add("planningEnd");
                visible.Add("workingEnd");
            }
            foreach (var column in ColumnOptions)
                column.Apply(visible.Contains(column.Key));
        }
        else
        {
            OtherColumn.Apply(settings.ShowExtendedInformation);
            ProjectIpColumn.Apply(settings.ShowExtendedInformation);
        }

        _showExtendedInformation = OtherColumn.IsVisible && ProjectIpColumn.IsVisible;
        OnPropertyChanged(nameof(ShowExtendedInformation));
    }

    private ViCoAutoRefreshSettings BuildDisplaySettings(int intervalMinutes) => new(
        intervalMinutes,
        OtherColumn.IsVisible && ProjectIpColumn.IsVisible,
        ColumnOptions.Where(column => column.IsVisible).Select(column => column.Key).ToArray());

    private void NotifyActionAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanUseSelectedWorkstationActions));
        OnPropertyChanged(nameof(CanUseRemoteActions));
        OnPropertyChanged(nameof(CanOpenPcProjects));
        OnPropertyChanged(nameof(CanOpenServerPathActions));
        OnPropertyChanged(nameof(RemoteActionUnavailableReason));
        OnPropertyChanged(nameof(PcProjectsUnavailableReason));
        OnPropertyChanged(nameof(ServerPathActionUnavailableReason));
    }
}
