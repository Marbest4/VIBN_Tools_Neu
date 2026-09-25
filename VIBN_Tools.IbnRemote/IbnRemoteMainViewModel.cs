using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using VIBN_Tools.Core.ViCo;
using VIBN_Tools.IbnRemote.Infrastructure;
using VIBN_Tools.Infrastructure.ViCo;
using VIBN_Tools.SharedWpf.Commands;

namespace VIBN_Tools.IbnRemote;

/// <summary>
/// Read-only IBN workflow. It intentionally exposes no board write, FEE, TIA,
/// file-copy or generation service.
/// </summary>
public sealed class IbnRemoteMainViewModel : NotifyObject, IDisposable
{
    private const int MaximumParallelAvailabilityChecks = 12;
    private readonly HttpClient _httpClient = new();
    private readonly ViCoPathsOptions _options = ViCoPathsOptions.CreateDefault();
    private readonly INetworkAvailabilityService _network = new NetworkAvailabilityService();
    private readonly IRemoteSessionService _remoteSessions = new WindowsRemoteSessionService();
    private readonly IRemoteDesktopService _remoteDesktop;
    private readonly IUserCredentialConfigurationService _credentialConfiguration;
    private readonly IbnRemoteFileLog _log = IbnRemoteFileLog.Instance;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _inWorkFilter;
    private IReadOnlyList<ViCoWorkstation> _allWorkstations = [];
    private readonly Dictionary<string, IbnRemoteWorkstationRow> _rowsByPc =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;
    private bool _isBusy;
    private string _statusText = "Arbeitsplatzdaten werden beim Start geladen.";
    private string _sourceStatus = "Quelle wird geprüft …";
    private IbnRemoteWorkstationRow? _selectedWorkstation;

    public IbnRemoteMainViewModel(
        IUserCredentialConfigurationService? credentialConfiguration = null,
        string? inWorkFilter = null)
    {
        _inWorkFilter = inWorkFilter?.Trim() ?? IbnRemoteDeploymentConfiguration.InWorkFilter;
        _credentialConfiguration = credentialConfiguration ??
            new IbnRemoteEmbeddedCredentialConfigurationService();
        _remoteDesktop = new WindowsRemoteDesktopService(
            _options.WorkingDirectory,
            new WindowsTemporaryRemoteCredentialStore(_credentialConfiguration.GetRemoteDesktopPassword));
        RefreshCommand = new AsyncRelayCommand(
            () => RefreshAsync(allowSharedCacheFallback: false),
            () => !IsBusy);
        ConnectAutomaticCommand = new RelayCommand<IbnRemoteWorkstationRow>(
            Connect,
            row => row?.CanConnect == true && !IsBusy);
    }

    public ObservableCollection<IbnRemoteWorkstationRow> Results { get; } = [];

    public ICommand RefreshCommand { get; }

    public ICommand ConnectAutomaticCommand { get; }

    public IbnRemoteWorkstationRow? SelectedWorkstation
    {
        get => _selectedWorkstation;
        set => SetProperty(ref _selectedWorkstation, value);
    }

    public string FilterDescription => string.IsNullOrWhiteSpace(_inWorkFilter)
        ? "Kein Publish-Filter gesetzt: alle Rechner mit In-Arbeit-Projekten werden angezeigt."
        : $"Fester In-Arbeit-Filter: {_inWorkFilter}";

    public bool UseMonitor1 { get; set; } = true;

    public bool UseMonitor2 { get; set; }

    public bool UseMonitor3 { get; set; }

    public bool UseMonitor4 { get; set; }

    public bool HasMonitor2 => _remoteDesktop.MonitorCount >= 2;

    public bool HasMonitor3 => _remoteDesktop.MonitorCount >= 3;

    public bool HasMonitor4 => _remoteDesktop.MonitorCount >= 4;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string SourceStatus
    {
        get => _sourceStatus;
        private set => SetProperty(ref _sourceStatus, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
                return;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
            return;
        _initialized = true;
        await RefreshAsync(allowSharedCacheFallback: true);
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _httpClient.Dispose();
    }

    private async Task RefreshAsync(bool allowSharedCacheFallback)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        StatusText = "Arbeitsplatzdaten werden aktualisiert …";
        try
        {
            var snapshot = await LoadBestAvailableSnapshotAsync(
                allowSharedCacheFallback,
                _lifetime.Token);
            _allWorkstations = snapshot.Workstations;
            var selectedWorkstations = IbnRemoteWorkstationSelectionPolicy.Select(_allWorkstations, _inWorkFilter);
            SynchronizeRows(selectedWorkstations);
            ApplyFilter();
            StatusText = snapshot.Warnings.Count == 0
                ? $"{Results.Count} von {_allWorkstations.Count} Arbeitsplätzen entsprechen dem In-Arbeit-Filter; Onlinezustände werden geprüft."
                : $"{Results.Count} von {_allWorkstations.Count} Arbeitsplätzen gefiltert; {snapshot.Warnings.Count} Quellenhinweis(e).";
            foreach (var warning in snapshot.Warnings)
                _log.Warning("Datenquelle", "Arbeitsplatzdaten wurden mit Hinweis geladen.", warning);

            await RefreshAvailabilityAsync(_lifetime.Token);
            StatusText = $"{Results.Count} passende Arbeitsplätze; " +
                         $"{_rowsByPc.Values.Count(row => row.IsOnline)} online. " +
                         $"Stand: {DateTime.Now:dd.MM.yyyy HH:mm:ss}.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Aktualisierung wurde beendet.";
        }
        catch (Exception exception)
        {
            StatusText = allowSharedCacheFallback
                ? "Arbeitsplatzdaten konnten nicht geladen werden. Details stehen im IBN-Protokoll."
                : $"Live-Aktualisierung fehlgeschlagen; die bisherige Anzeige bleibt erhalten: {exception.Message}";
            _log.Error("Datenquelle", StatusText, exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<ViCoWorkstationSnapshot> LoadBestAvailableSnapshotAsync(
        bool allowSharedCacheFallback,
        CancellationToken cancellationToken)
    {
        var apiKey = _credentialConfiguration.GetKanbanizeApiKey();
        var localCache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GROB",
            "VIBN_Tools_IBN",
            "Cache");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                var refresh = new KanbanizeRefreshService(_httpClient, apiKey, localCache);
                await refresh.RefreshAsync(cancellationToken);
                var onlineSnapshot = await new LegacyWorkstationCatalog(localCache).LoadAsync(cancellationToken);
                if (onlineSnapshot.Workstations.Count > 0)
                {
                    SourceStatus = $"Kanbanize live (read-only), {DateTime.Now:dd.MM.yyyy HH:mm:ss}";
                    return onlineSnapshot;
                }

                throw new InvalidDataException(
                    "Kanbanize lieferte nach der Aktualisierung keine verwendbaren Arbeitsplatzkarten.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (!allowSharedCacheFallback)
                {
                    SourceStatus = "Kanbanize-Liveaktualisierung fehlgeschlagen";
                    throw new InvalidOperationException(
                        "Das Board konnte nicht live aktualisiert werden. API-Key, Boardzugriff und Netzwerk prüfen.",
                        exception);
                }
                _log.Warning(
                    "Kanbanize",
                    "Online-Aktualisierung fehlgeschlagen; gemeinsamer Lesecache wird verwendet.",
                    exception.Message);
            }
        }

        if (!allowSharedCacheFallback)
        {
            SourceStatus = "Kanbanize nicht konfiguriert";
            throw new InvalidOperationException(
                "Für die Live-Aktualisierung ist ein Kanbanize-API-Key erforderlich.");
        }

        SourceStatus = string.IsNullOrWhiteSpace(apiKey)
            ? "Gemeinsamer Cache (kein API-Key)"
            : "Gemeinsamer Cache (Onlinefehler)";
        return await new LegacyWorkstationCatalog(_options.ServerCacheRoot).LoadAsync(cancellationToken);
    }

    private void SynchronizeRows(IEnumerable<ViCoWorkstation> workstations)
    {
        var desired = workstations
            .Where(workstation => !string.IsNullOrWhiteSpace(workstation.PcName))
            .GroupBy(workstation => workstation.PcName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var desiredNames = desired
            .Select(workstation => workstation.PcName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _rowsByPc.Keys.Where(name => !desiredNames.Contains(name)).ToArray())
            _rowsByPc.Remove(stale);
        foreach (var workstation in desired)
        {
            if (_rowsByPc.TryGetValue(workstation.PcName, out var existing))
                existing.UpdateModel(workstation);
            else
                _rowsByPc[workstation.PcName] = new IbnRemoteWorkstationRow(workstation);
        }
    }

    private void ApplyFilter()
    {
        var selectedPc = SelectedWorkstation?.PcName;
        var filtered = IbnRemoteWorkstationSelectionPolicy.Select(_allWorkstations, _inWorkFilter);
        Results.Clear();
        foreach (var workstation in filtered)
        {
            if (_rowsByPc.TryGetValue(workstation.PcName, out var row))
                Results.Add(row);
        }
        SelectedWorkstation = Results.FirstOrDefault(row =>
            string.Equals(row.PcName, selectedPc, StringComparison.OrdinalIgnoreCase)) ??
            Results.FirstOrDefault();
    }

    private async Task RefreshAvailabilityAsync(CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(MaximumParallelAvailabilityChecks);
        await Task.WhenAll(_rowsByPc.Values.Select(async row =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var online = await _network.PingAsync(row.PcName, cancellationToken);
                row.SetOnline(online);
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                if (!online)
                    return;

                var session = await _remoteSessions.GetSessionInfoAsync(row.PcName, cancellationToken);
                row.SetRemoteSession(session);
            }
            finally
            {
                gate.Release();
            }
        }));
    }

    private void Connect(IbnRemoteWorkstationRow? row)
    {
        if (row?.CanConnect != true)
            return;

        var monitors = new[] { UseMonitor1, UseMonitor2, UseMonitor3, UseMonitor4 }
            .Select((selected, index) => (selected, index))
            .Where(item => item.selected)
            .Select(item => item.index)
            .ToArray();
        try
        {
            if (string.IsNullOrWhiteSpace(row.UserName))
                throw new InvalidOperationException("Die KONFIGURATION-Karte enthält keinen Remote-Benutzer.");
            _remoteDesktop.Connect(row.PcName, row.UserName, monitors);
            StatusText = $"Remote Desktop zu {row.PcName} wird als {row.UserName} gestartet.";
            _log.Information("Remote Desktop", StatusText);
        }
        catch (Exception exception)
        {
            StatusText = $"Remote Desktop zu {row.PcName} konnte nicht gestartet werden: {exception.Message}";
            _log.Error("Remote Desktop", StatusText, exception);
        }
    }

}

public sealed class IbnRemoteWorkstationRow : NotifyObject
{
    private ViCoWorkstation _model;
    private bool _isOnline;
    private string _onlineStatus = "Wird geprüft …";
    private string _onlineBackground = "#FFF3F5F7";
    private string _remoteSession = "Wird geprüft …";
    private string _lastLogon = "Wird geprüft …";

    public IbnRemoteWorkstationRow(ViCoWorkstation model) => _model = model;

    public string PcName => _model.PcName;
    public string WorkingProjects => string.Join(" | ", _model.WorkingProjects);
    public string WorkingEnd => string.Join(" | ", _model.WorkingProjectCards
        .Where(card => card.Deadline is not null)
        .Select(card => card.Deadline!.Value.LocalDateTime.ToString("dd.MM.yyyy"))
        .Distinct(StringComparer.Ordinal));
    public string Software => _model.WorkstationConfiguration.Software.Value;
    public string Location => _model.WorkstationConfiguration.Location.Value;
    public string ProjectIp => _model.WorkstationConfiguration.ProjectIp.Value;
    public string Other => _model.WorkstationConfiguration.Other.Value;
    public string UserName => _model.UserName;
    public string Occupancy => _model.Status;
    public string OccupancyBackground => Occupancy == "Frei" ? "#FFC6EFCE" : "#FFFFC7CE";
    public bool IsOnline => _isOnline;
    public bool CanConnect => IsOnline;
    public string OnlineStatus => _onlineStatus;
    public string OnlineBackground => _onlineBackground;
    public string RemoteSession => _remoteSession;
    public string LastLogon => _lastLogon;

    public void UpdateModel(ViCoWorkstation model)
    {
        _model = model;
        OnPropertyChanged(string.Empty);
    }

    public void SetOnline(bool online)
    {
        _isOnline = online;
        _onlineStatus = online ? "Online" : "Offline";
        _onlineBackground = online ? "#FFC6EFCE" : "#FFFFC7CE";
        if (!online)
        {
            _remoteSession = "Offline";
            _lastLogon = "—";
        }
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(OnlineStatus));
        OnPropertyChanged(nameof(OnlineBackground));
        OnPropertyChanged(nameof(RemoteSession));
        OnPropertyChanged(nameof(LastLogon));
    }

    public void SetRemoteSession(ViCoRemoteSessionInfo information)
    {
        if (!information.IsAvailable)
        {
            _remoteSession = "Nicht abrufbar";
            _lastLogon = "Nicht abrufbar";
        }
        else
        {
            _remoteSession = string.IsNullOrWhiteSpace(information.ActiveUser)
                ? "Keine aktive Sitzung"
                : $"Aktiv: {information.ActiveUser}";
            _lastLogon = information.LastLogonAt is null
                ? "Keine Anmeldung gefunden"
                : $"{information.LastLogonUser} – {information.LastLogonAt.Value.LocalDateTime:dd.MM.yyyy HH:mm}";
        }
        OnPropertyChanged(nameof(RemoteSession));
        OnPropertyChanged(nameof(LastLogon));
    }
}

public abstract class NotifyObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
