using System.Collections.ObjectModel;
using System.Windows.Input;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.Core.ViCo;
using VIBN_Tools.Tia.Client;
using VIBN_Tools.Tia.Contracts;
using VIBN_Tools.Core.Collections;

namespace VIBN_Tools.Application.VM;

/// <summary>
/// UI coordinator for TIA Portal operations. Every Siemens Openness call is
/// delegated through the isolated named-pipe bridge so an Openness failure does
/// not terminate the WPF host process.
/// </summary>
public sealed class TiaPortalPageVM : MvvmBase, IAsyncDisposable
{
    private readonly ITiaBridgeClient _client;
    private readonly ITiaLibraryService _libraryService;
    private readonly IFolderSelectionService _folderSelection;
    private readonly IApplicationLog _log;
    private bool _isBusy;
    private string? _selectedVersion;
    private TiaPlcInfo? _selectedPlc;
    private string _statusText;

    public TiaPortalPageVM(
        ITiaBridgeClient client,
        ITiaLibraryService libraryService,
        IFolderSelectionService folderSelection,
        IReadOnlyList<string> installedVersions,
        IApplicationLog? log = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _folderSelection = folderSelection ?? throw new ArgumentNullException(nameof(folderSelection));
        _log = log ?? NullApplicationLog.Instance;

        foreach (var version in installedVersions)
            InstalledVersions.Add(version);

        _selectedVersion = InstalledVersions.FirstOrDefault();
        _statusText = InstalledVersions.Count == 0
            ? "Keine unterstützte TIA-Portal-Installation gefunden."
            : "TIA Bridge ist bereit.";

        ConnectCommand = GetCommandBindingAsync(ConnectAsync);
        SelectPlcCommand = GetCommandBindingAsync(SelectPlcAsync);
        LoadBlocksCommand = GetCommandBindingAsync(LoadBlocksAsync);
        LoadDataTypesCommand = GetCommandBindingAsync(LoadDataTypesAsync);
        LoadAxesCommand = GetCommandBindingAsync(LoadAxesAsync);
        SelectAllAxesCommand = GetCommandBinding(() => SetAllAxesSelected(true));
        SelectNoAxesCommand = GetCommandBinding(() => SetAllAxesSelected(false));
        ConfigureAxesCommand = GetCommandBindingAsync(ConfigureAxesAsync);
        ToggleAxisConfigurationInfoCommand = GetCommandBinding(ToggleAxisConfigurationInfo);
        SaveCommand = GetCommandBindingAsync(SaveAsync);
        BrowseImportCommand = GetCommandBinding(BrowseImport);
        BrowseExportCommand = GetCommandBinding(BrowseExport);
        ImportLibraryCommand = GetCommandBindingAsync(ImportLibraryAsync);
        ExportLibraryCommand = GetCommandBindingAsync(ExportLibraryAsync);
        ToggleLibraryOperationInfoCommand = GetCommandBinding(ToggleLibraryOperationInfo);
    }

    public ObservableCollection<string> InstalledVersions { get; } = new();

    public ObservableCollection<TiaPlcInfo> Plcs { get; } = new();

    public ObservableCollection<TiaProgramItemInfo> ProgramItems { get; } = new();

    public ObservableCollection<TiaAxisSelectionRowVM> Axes { get; } = new();

    public ICommand ConnectCommand { get; }

    public ICommand SelectPlcCommand { get; }

    public ICommand LoadBlocksCommand { get; }

    public ICommand LoadDataTypesCommand { get; }

    public ICommand LoadAxesCommand { get; }

    public ICommand SelectAllAxesCommand { get; }

    public ICommand SelectNoAxesCommand { get; }

    public ICommand ConfigureAxesCommand { get; }

    public ICommand ToggleAxisConfigurationInfoCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand BrowseImportCommand { get; }

    public ICommand BrowseExportCommand { get; }

    public ICommand ImportLibraryCommand { get; }

    public ICommand ExportLibraryCommand { get; }

    public ICommand ToggleLibraryOperationInfoCommand { get; }

    public string AxisConfigurationInfo =>
        "Auswahl konfigurieren ändert ausschließlich die markierten Technologieachsen. " +
        "Ein separates X/Y/Z-Kennzeichen beziehungsweise Namen wie AxisX/AchseX werden als linear erkannt; andere Namen als rotatorisch. " +
        "Gesetzt werden: _Properties.MotionType, Modulo.Enable=0, Actor.DataAdaption=0, " +
        "Sensor[1].DataAdaption=0, Sensor[1].MountingMode, Simulation.Mode=1, " +
        "Sensor[1].Type=2, TorqueLimiting.PositionBasedMonitorings=0, " +
        "FollowingError.EnableMonitoring=0 und PositionControl.EnableDSC=0. " +
        "Die Konfiguration speichert nicht automatisch.";

    public string ProjectSaveInfo =>
        "Gesamtes TIA-Projekt speichern ruft Project.Save() auf. Dadurch werden alle aktuell " +
        "offenen, noch nicht gespeicherten Projektänderungen persistiert – auch Änderungen, die " +
        "außerhalb dieses Tools vorgenommen wurden. Der Schritt ist nur erforderlich, wenn die " +
        "Änderungen dauerhaft erhalten bleiben sollen.";

    public string LibraryOperationInfo =>
        "Voraussetzung: TIA Portal mit geöffnetem Projekt starten, hier die passende Version verbinden, " +
        "die gewünschte PLC wählen und 'PLC auswählen' drücken.\n\n" +
        "Import: Der Importordner muss _Programm und/oder _Datatype mit TIA-XML-Dateien enthalten. " +
        "Fehlende TIA-Ordner werden angelegt, gleichnamige Bausteine und Datentypen werden überschrieben. " +
        "Ist die Achsenoption aktiv, werden alle gefundenen Achsen konfiguriert und AxisDB.xml sowie " +
        "AxisFC.xml im lokalen Importordner erzeugt und mitimportiert. Am Ende wird das gesamte TIA-Projekt automatisch gespeichert.\n\n" +
        "Export: 'TIA-Bibliotheksordner' muss exakt den Ordnernamen bezeichnen, der im TIA-Baustein- " +
        "und Datentypbaum exportiert werden soll. Die XML-Dateien werden unter " +
        "<Exportordner>/<Name>_<TIA-Version>/_Programm und _Datatype geschrieben; vorhandene gleichnamige " +
        "Exportdateien werden ersetzt. Das TIA-Projekt wird beim Export nicht verändert oder gespeichert.";

    private bool _isAxisConfigurationInfoVisible;
    public bool IsAxisConfigurationInfoVisible
    {
        get => _isAxisConfigurationInfoVisible;
        private set
        {
            if (_isAxisConfigurationInfoVisible == value)
                return;
            _isAxisConfigurationInfoVisible = value;
            OnPropertyChanged();
        }
    }

    private bool _isLibraryOperationInfoVisible;
    public bool IsLibraryOperationInfoVisible
    {
        get => _isLibraryOperationInfoVisible;
        private set
        {
            if (_isLibraryOperationInfoVisible == value)
                return;
            _isLibraryOperationInfoVisible = value;
            OnPropertyChanged();
        }
    }

    private string _libraryPath = string.Empty;
    public string LibraryPath
    {
        get => _libraryPath;
        set
        {
            _libraryPath = value;
            OnPropertyChanged();
        }
    }

    private string _exportPath = string.Empty;
    public string ExportPath
    {
        get => _exportPath;
        set
        {
            _exportPath = value;
            OnPropertyChanged();
        }
    }

    private string _libraryName = "VICOBIB";
    public string LibraryName
    {
        get => _libraryName;
        set
        {
            _libraryName = value;
            OnPropertyChanged();
        }
    }

    private bool _configureAxesDuringImport;
    public bool ConfigureAxesDuringImport
    {
        get => _configureAxesDuringImport;
        set
        {
            _configureAxesDuringImport = value;
            OnPropertyChanged();
        }
    }

    private int _operationProgress;
    public int OperationProgress
    {
        get => _operationProgress;
        private set
        {
            _operationProgress = value;
            OnPropertyChanged();
        }
    }

    public string? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (string.Equals(_selectedVersion, value, StringComparison.Ordinal))
                return;

            _selectedVersion = value;
            OnPropertyChanged();
        }
    }

    public TiaPlcInfo? SelectedPlc
    {
        get => _selectedPlc;
        set
        {
            if (ReferenceEquals(_selectedPlc, value))
                return;

            _selectedPlc = value;
            OnPropertyChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private async Task ConnectAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(SelectedVersion))
            return;

        await RunBusyAsync("Verbindung zu TIA Portal wird hergestellt …", async () =>
        {
            await _client.ConnectAsync();
            if (!await _client.PingAsync())
                throw new InvalidOperationException("TIA Bridge antwortet nicht.");

            await _client.SelectVersionAsync(SelectedVersion);
            await _client.AttachAsync();

            var plcs = await _client.ListPlcsAsync();
            Plcs.ReplaceWith(plcs);
            SelectedPlc = Plcs.FirstOrDefault();
            StatusText = $"Mit TIA Portal {SelectedVersion} verbunden; {Plcs.Count} PLC(s) gefunden.";
        });
    }

    private async Task SelectPlcAsync()
    {
        if (SelectedPlc is null)
            return;

        await RunBusyAsync("PLC wird ausgewählt …", async () =>
        {
            await _client.SelectPlcAsync(SelectedPlc.Index);
            ProgramItems.Clear();
            Axes.Clear();
            StatusText = $"PLC '{SelectedPlc.Name}' ist ausgewählt.";
        });
    }

    private Task LoadBlocksAsync() => LoadTreeAsync(
        "Programmbausteine werden geladen …",
        _client.ListProgramBlocksAsync,
        "Programmbausteine");

    private Task LoadDataTypesAsync() => LoadTreeAsync(
        "Datentypen werden geladen …",
        _client.ListDataTypesAsync,
        "Datentypen");

    private async Task LoadTreeAsync(
        string busyText,
        Func<CancellationToken, Task<TiaProjectTree>> loader,
        string description)
    {
        await RunBusyAsync(busyText, async () =>
        {
            var tree = await loader(CancellationToken.None);
            ProgramItems.ReplaceWith(tree.Items);
            StatusText = $"{tree.Items.Count} {description} geladen.";
        });
    }

    private async Task ConfigureAxesAsync()
    {
        var selectedIds = Axes.Where(axis => axis.IsSelected).Select(axis => axis.Id).ToArray();
        if (selectedIds.Length == 0)
        {
            StatusText = "Keine Achse ausgewählt. Es wurden keine TIA-Parameter geändert.";
            _log.Warning("TIA Portal", StatusText);
            return;
        }

        await RunBusyAsync("Achsen werden für die Simulation konfiguriert …", async () =>
        {
            var configured = await _client.ConfigureAxesAsync(selectedIds);
            foreach (var result in configured)
                Axes.FirstOrDefault(axis => string.Equals(axis.Id, result.Id, StringComparison.OrdinalIgnoreCase))
                    ?.ApplyConfigurationResult(result);

            var successfulParameters = configured.Sum(axis => axis.ParameterResults.Count(result => result.Success));
            var failedParameters = configured.Sum(axis => axis.ParameterResults.Count(result => !result.Success));
            StatusText = $"{configured.Count} Achse(n) verarbeitet: {successfulParameters} Parameter gesetzt, {failedParameters} fehlgeschlagen.";
            _log.Information("TIA Portal", StatusText);
            foreach (var axis in configured)
            {
                var details = axis.ParameterResults.Count == 0
                    ? "keine unterstützten Parameter gefunden"
                    : string.Join(", ", axis.ParameterResults.Select(result =>
                        result.Success
                            ? $"{result.Name}={result.Value}"
                            : $"{result.Name} FEHLER: {result.Error}"));
                if (axis.ParameterResults.Count == 0 || axis.ParameterResults.Any(result => !result.Success))
                    _log.Warning("TIA Achsenkonfiguration", $"{axis.Id}: {details}");
                else
                    _log.Information("TIA Achsenkonfiguration", $"{axis.Id}: {details}");
            }
        });
    }

    private void ToggleAxisConfigurationInfo() =>
        IsAxisConfigurationInfoVisible = !IsAxisConfigurationInfoVisible;

    private void ToggleLibraryOperationInfo() =>
        IsLibraryOperationInfoVisible = !IsLibraryOperationInfoVisible;

    private async Task LoadAxesAsync()
    {
        await RunBusyAsync("Achsen werden schreibgeschützt gelesen …", async () =>
        {
            var axes = await _client.ListAxesAsync();
            Axes.ReplaceWith(axes.Select(axis => new TiaAxisSelectionRowVM(axis)));
            StatusText = $"{Axes.Count} Achse(n) gelesen. Das TIA-Projekt wurde nicht verändert.";
        });
    }

    private void SetAllAxesSelected(bool selected)
    {
        foreach (var axis in Axes)
            axis.IsSelected = selected;
        StatusText = selected
            ? $"Alle {Axes.Count} Achse(n) zur Konfiguration ausgewählt."
            : "Keine Achse ausgewählt. Die Konfiguration würde nichts ändern.";
    }

    private async Task SaveAsync()
    {
        await RunBusyAsync("TIA-Projekt wird gespeichert …", async () =>
        {
            await _client.SaveAsync();
            StatusText = "TIA-Projekt gespeichert.";
        });
    }

    private void BrowseImport()
    {
        var selected = _folderSelection.SelectFolder("ViCo-Bibliothek auswählen", LibraryPath);
        if (selected is not null)
            LibraryPath = selected;
    }

    private void BrowseExport()
    {
        var selected = _folderSelection.SelectFolder("Exportziel auswählen", ExportPath);
        if (selected is not null)
            ExportPath = selected;
    }

    private async Task ImportLibraryAsync()
    {
        if (string.IsNullOrWhiteSpace(LibraryPath))
        {
            StatusText = "Bitte zuerst den Importordner mit _Programm und/oder _Datatype auswählen.";
            return;
        }
        if (string.IsNullOrWhiteSpace(SelectedVersion) || SelectedPlc is null)
        {
            StatusText = "Bitte zuerst TIA verbinden und die gewünschte PLC auswählen.";
            return;
        }

        await RunBusyAsync("ViCo-Bibliothek wird importiert …", async () =>
        {
            OperationProgress = 0;
            var progress = new Progress<TiaLibraryProgress>(UpdateLibraryProgress);
            await _libraryService.ImportAsync(
                LibraryPath,
                ConfigureAxesDuringImport,
                SelectedVersion,
                progress);
            OperationProgress = 100;
            StatusText = "ViCo-Bibliothek wurde importiert und das TIA-Projekt gespeichert.";
        });
    }

    private async Task ExportLibraryAsync()
    {
        if (string.IsNullOrWhiteSpace(ExportPath))
        {
            StatusText = "Bitte zuerst einen Exportordner auswählen.";
            return;
        }
        if (string.IsNullOrWhiteSpace(LibraryName))
        {
            StatusText = "Bitte den exakten TIA-Bibliotheksordner angeben.";
            return;
        }
        if (string.IsNullOrWhiteSpace(SelectedVersion) || SelectedPlc is null)
        {
            StatusText = "Bitte zuerst TIA verbinden und die gewünschte PLC auswählen.";
            return;
        }

        await RunBusyAsync("ViCo-Bibliothek wird exportiert …", async () =>
        {
            OperationProgress = 0;
            var progress = new Progress<TiaLibraryProgress>(UpdateLibraryProgress);
            var path = await _libraryService.ExportAsync(
                LibraryName,
                ExportPath,
                SelectedVersion,
                progress);
            OperationProgress = 100;
            StatusText = $"ViCo-Bibliothek exportiert: {path}";
        });
    }

    private void UpdateLibraryProgress(TiaLibraryProgress progress)
    {
        OperationProgress = progress.Total == 0
            ? 0
            : (int)Math.Round(progress.Completed * 100d / progress.Total);
        StatusText = progress.Operation;
    }

    private async Task RunBusyAsync(string status, Func<Task> action)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        StatusText = status;
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            StatusText = "TIA-Vorgang wurde abgebrochen.";
            _log.Warning("TIA Portal", StatusText);
        }
        catch (Exception exception)
        {
            // Commands are invoked from async-void WPF command bindings. By
            // handling bridge failures here, the user gets a clear status and
            // a diagnostic entry instead of an unhandled runtime exception.
            StatusText = $"TIA-Vorgang fehlgeschlagen: {exception.Message}";
            _log.Error("TIA Portal", StatusText, exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

}

public sealed class TiaAxisSelectionRowVM : MvvmBase
{
    private bool _isSelected = true;
    private string _result = "Nur gelesen";

    public TiaAxisSelectionRowVM(TiaAxisInfo axis)
    {
        Axis = axis ?? throw new ArgumentNullException(nameof(axis));
    }

    public TiaAxisInfo Axis { get; private set; }

    public string Id => Axis.Id;

    public string Name => Axis.Name;

    public string TechnologyType => Axis.TechnologyType;

    public string GroupPath => Axis.GroupPath;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public string Result
    {
        get => _result;
        private set
        {
            _result = value;
            OnPropertyChanged();
        }
    }

    public void ApplyConfigurationResult(TiaAxisInfo result)
    {
        Axis = result;
        var successful = result.ParameterResults.Count(parameter => parameter.Success);
        var failed = result.ParameterResults.Count - successful;
        Result = result.ParameterResults.Count == 0
            ? "Keine unterstützten Parameter gefunden"
            : failed == 0
                ? $"{successful} Parameter gesetzt"
                : $"{successful} gesetzt, {failed} fehlgeschlagen";
        OnPropertyChanged(nameof(Axis));
    }
}
