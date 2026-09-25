using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.Core.ViCo;
using VIBN_Tools.Tia.Client;
using VIBN_Tools.Tia.Contracts;
using VIBN_Tools.Core.Collections;
using VIBN_Tools.Core.Diagnostics;
using VIBN_Tools.Quality;

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
        ConfigureAxesAndCreateFilesCommand = GetCommandBindingAsync(ConfigureAxesAndCreateFilesAsync);
        BrowseAxisArtifactCommand = GetCommandBinding(BrowseAxisArtifact);
        ToggleAxisConfigurationInfoCommand = GetCommandBinding(ToggleAxisConfigurationInfo);
        SaveCommand = GetCommandBindingAsync(SaveAsync);
        BrowseImportCommand = GetCommandBinding(BrowseImport);
        BrowseExportCommand = GetCommandBinding(BrowseExport);
        ImportLibraryCommand = GetCommandBindingAsync(ImportLibraryAsync);
        ExportLibraryCommand = GetCommandBindingAsync(ExportLibraryAsync);
        ToggleLibraryOperationInfoCommand = GetCommandBinding(ToggleLibraryOperationInfo);
        BrowseAxisExchangeCommand = GetCommandBinding(BrowseAxisExchange);
        ExportAxisConfigurationsCommand = GetCommandBindingAsync(ExportAxisConfigurationsAsync);
        ImportAxisConfigurationsCommand = GetCommandBindingAsync(ImportAxisConfigurationsAsync);
        ExportAxisInterfaceCommand = GetCommandBindingAsync(ExportAxisInterfaceAsync);
        ToggleAxisExchangeInfoCommand = GetCommandBinding(() =>
            IsAxisExchangeInfoVisible = !IsAxisExchangeInfoVisible);
        CompileQualityGateCommand = GetCommandBindingAsync(CompileQualityGateAsync);
    }

    public ObservableCollection<string> InstalledVersions { get; } = new();

    public ObservableCollection<TiaPlcInfo> Plcs { get; } = new();

    public ObservableCollection<TiaProgramItemInfo> ProgramItems { get; } = new();

    public ObservableCollection<TiaAxisSelectionRowVM> FoundAxes { get; } = new();

    public ObservableCollection<TiaAxisSelectionRowVM> ConfiguredAxes { get; } = new();

    public ObservableCollection<TiaCompileMessageRowVM> CompileMessages { get; } = new();

    public ObservableCollection<TiaAxisSelectionRowVM> Axes => FoundAxes;

    public ICommand ConnectCommand { get; }

    public ICommand SelectPlcCommand { get; }

    public ICommand LoadBlocksCommand { get; }

    public ICommand LoadDataTypesCommand { get; }

    public ICommand LoadAxesCommand { get; }

    public ICommand SelectAllAxesCommand { get; }

    public ICommand SelectNoAxesCommand { get; }

    public ICommand ConfigureAxesCommand { get; }

    public ICommand ConfigureAxesAndCreateFilesCommand { get; }

    public ICommand BrowseAxisArtifactCommand { get; }

    public ICommand ToggleAxisConfigurationInfoCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand BrowseImportCommand { get; }

    public ICommand BrowseExportCommand { get; }

    public ICommand ImportLibraryCommand { get; }

    public ICommand ExportLibraryCommand { get; }

    public ICommand ToggleLibraryOperationInfoCommand { get; }

    public ICommand BrowseAxisExchangeCommand { get; }

    public ICommand ExportAxisConfigurationsCommand { get; }

    public ICommand ImportAxisConfigurationsCommand { get; }

    public ICommand ExportAxisInterfaceCommand { get; }

    public ICommand ToggleAxisExchangeInfoCommand { get; }

    public ICommand CompileQualityGateCommand { get; }

    public string CompileQualityGateInfo =>
        "Kompiliert ausschließlich die ausgewählte PLC über TIA Openness und liest Fehler, Warnungen und " +
        "Meldungspfade aus. Das Projekt wird dabei nicht gespeichert. Ein erfolgreiches Ergebnis wird als " +
        "Nachweis für das zentrale Quality Gate hinterlegt; Safety- oder Know-how-geschützte Inhalte können " +
        "weiterhin eine Anmeldung direkt in TIA erfordern. Das ist ein statischer Build-Test, kein Laufzeit- oder " +
        "HMI-Funktionstest. Ein automatischer Ablauf wie 'HMI-Taste -> PLC-Ausgang -> Simulationsrückmeldung' " +
        "benötigt zusätzlich eine verbundene WinCC Runtime, PLCSIM Advanced oder eine Test-PLC und einen " +
        "Simulationsadapter mit sicherer Rücksetzung der geschriebenen Werte.";

    public string AxisConfigurationInfo =>
        "Auswahl konfigurieren ändert ausschließlich die markierten Technologieachsen. " +
        "Ein separates X/Y/Z-Kennzeichen beziehungsweise Namen wie AxisX/AchseX werden als linear erkannt; andere Namen als rotatorisch. " +
        "Gesetzt werden: _Properties.MotionType, Modulo.Enable=0, Actor.DataAdaption=0, " +
        "Sensor[1].DataAdaption=0, Sensor[1].MountingMode, Simulation.Mode=1, " +
        "Sensor[1].Type=2, TorqueLimiting.PositionBasedMonitorings=0, " +
        "FollowingError.EnableMonitoring=0 und PositionControl.EnableDSC=0. " +
        "Die Konfiguration speichert nicht automatisch. AxisDB/AxisFC werden wie im Avalonia-Werkzeug unter " +
        "<Ablagewurzel>/_Programm/Axis erzeugt. Bereits konfigurierte Achsen können rechts markiert und ohne " +
        "erneute Parameteränderung in diese Dateien aufgenommen werden.";

    public string ProjectSaveInfo =>
        "Gesamtes TIA-Projekt speichern ruft Project.Save() auf. Dadurch werden alle aktuell " +
        "offenen, noch nicht gespeicherten Projektänderungen persistiert – auch Änderungen, die " +
        "außerhalb dieses Tools vorgenommen wurden. Der Schritt ist nur erforderlich, wenn die " +
        "Änderungen dauerhaft erhalten bleiben sollen.";

    public string LibraryOperationInfo =>
        "Voraussetzung: TIA Portal mit geöffnetem Projekt starten, hier die passende Version verbinden, " +
        "die gewünschte PLC wählen und 'PLC auswählen' drücken.\n\n" +
        "Neues Kundenprojekt: Die ViCo-Bibliothek zuerst manuell in TIA einfügen und kundenspezifische " +
        "Punkte wie RFID und Safetybrücken bearbeiten. Danach wird sie mit 'Bibliothek exportieren' auf " +
        "dem Projektlaufwerk dieses Kundenprojekts abgelegt.\n\n" +
        "Export: 'TIA-Bibliotheksordner' muss exakt den Ordnernamen bezeichnen, der im TIA-Baustein- " +
        "und Datentypbaum exportiert werden soll. Die XML-Dateien werden unter " +
        "<Exportordner>/<Name>_<TIA-Version>/_Programm und _Datatype geschrieben; vorhandene gleichnamige " +
        "Exportdateien werden ersetzt. Das TIA-Projekt wird beim Export nicht verändert oder gespeichert.\n\n" +
        "Folgeprojekt desselben Kunden: Den zuvor exportierten Ordner auswählen und importieren. Fehlende " +
        "TIA-Ordner werden angelegt, gleichnamige Bausteine und Datentypen werden überschrieben; am Ende " +
        "wird das gesamte TIA-Projekt automatisch gespeichert.";

    public string AxisExchangeInfo =>
        "TO-Konfiguration exportieren schreibt für jede Technologieachse alle lesbaren Parameter nach " +
        "<Ordner>/ToConfig/<Achse>/<Achse>.txt. Importieren setzt nur Parameter gleichnamiger Achsen; " +
        "nicht gefundene Achsen und nicht setzbare Parameter werden protokolliert. Der Import speichert das " +
        "TIA-Projekt nicht automatisch. Achsen-Schnittstelle erzeugt AxisValueTags.xlsx im gewählten Ordner " +
        "und verändert das TIA-Projekt nicht. Voraussetzung für alle drei Aktionen sind eine verbundene " +
        "TIA-Version, eine ausgewählte PLC und ein beschreibbarer Austauschordner. Für den Import muss " +
        "zuvor ein passender TO-Export im Austauschordner liegen.";

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

    private bool _isAxisExchangeInfoVisible;
    public bool IsAxisExchangeInfoVisible
    {
        get => _isAxisExchangeInfoVisible;
        private set
        {
            if (_isAxisExchangeInfoVisible == value)
                return;
            _isAxisExchangeInfoVisible = value;
            OnPropertyChanged();
        }
    }

    private string _axisExchangePath = string.Empty;
    public string AxisExchangePath
    {
        get => _axisExchangePath;
        set
        {
            _axisExchangePath = value;
            OnPropertyChanged();
        }
    }

    private string _axisArtifactPath = string.Empty;
    public string AxisArtifactPath
    {
        get => _axisArtifactPath;
        set
        {
            _axisArtifactPath = value;
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
            CompileMessages.Clear();
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
            FoundAxes.Clear();
            ConfiguredAxes.Clear();
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
        var selectedIds = FoundAxes.Where(axis => axis.IsSelected).Select(axis => axis.Id).ToArray();
        if (selectedIds.Length == 0)
        {
            StatusText = "Keine Achse ausgewählt. Es wurden keine TIA-Parameter geändert.";
            _log.Warning("TIA Portal", StatusText);
            return;
        }

        await RunBusyAsync("Achsen werden für die Simulation konfiguriert …", async () =>
        {
            var configured = await ConfigureSelectedAxesCoreAsync(selectedIds);

            var successfulParameters = configured.Sum(axis => axis.ParameterResults.Count(result => result.Success));
            var failedParameters = configured.Sum(axis => axis.ParameterResults.Count(result => !result.Success));
            StatusText = $"{configured.Count} Achse(n) verarbeitet: {successfulParameters} Parameter gesetzt, {failedParameters} fehlgeschlagen.";
            _log.Information("TIA Portal", StatusText);
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
            var configuredIds = ConfiguredAxes.Select(axis => axis.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            FoundAxes.ReplaceWith(axes
                .Where(axis => !configuredIds.Contains(axis.Id))
                .Select(axis => new TiaAxisSelectionRowVM(axis)));
            StatusText = $"{FoundAxes.Count} nicht konfigurierte und {ConfiguredAxes.Count} konfigurierte " +
                         "Achse(n) angezeigt. Das TIA-Projekt wurde nicht verändert.";
        });
    }

    private void SetAllAxesSelected(bool selected)
    {
        foreach (var axis in FoundAxes)
            axis.IsSelected = selected;
        foreach (var axis in ConfiguredAxes)
            axis.IsSelected = selected;
        StatusText = selected
            ? $"Alle {FoundAxes.Count + ConfiguredAxes.Count} Achse(n) ausgewählt."
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

    private async Task CompileQualityGateAsync()
    {
        if (SelectedPlc is null)
        {
            StatusText = "Bitte zuerst TIA verbinden und die gewünschte PLC auswählen.";
            return;
        }

        await RunBusyAsync("Ausgewählte PLC wird kompiliert …", async () =>
        {
            CompileMessages.Clear();
            OperationProgress = 10;
            TiaCompileResult result;
            try
            {
                result = await _client.CompileSelectedPlcAsync();
            }
            catch (Exception exception)
            {
                QualityEvidenceStore.Instance.Upsert(new QualityEvidence(
                    "TIA Compile",
                    SelectedPlc.Name,
                    QualityStatus.Failed,
                    $"Compile-Aufruf fehlgeschlagen: {exception.Message}",
                    DateTimeOffset.UtcNow,
                    [new QualityFinding("TIA Compile", "TIA_COMPILE_FAILED", QualityStatus.Failed, exception.Message)]));
                throw;
            }
            OperationProgress = 90;
            foreach (var message in FlattenCompileMessages(result.Messages))
                CompileMessages.Add(message);

            var status = result.Success ? QualityStatus.Passed : QualityStatus.Failed;
            var findings = CompileMessages
                .Where(message => message.IsRelevant)
                .Select(message => new QualityFinding(
                    "TIA Compile",
                    "TIA_COMPILE_MESSAGE",
                    message.IsError ? QualityStatus.Failed : QualityStatus.Warning,
                    $"{message.Path}: {message.Description}"))
                .ToArray();
            QualityEvidenceStore.Instance.Upsert(new QualityEvidence(
                "TIA Compile",
                $"{result.TargetType}:{result.TargetName}",
                status,
                $"{result.ErrorCount} Fehler, {result.WarningCount} Warnungen, {result.DurationMilliseconds} ms",
                DateTimeOffset.UtcNow,
                findings));

            OperationProgress = 100;
            var completedAt = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");
            StatusText = result.Success
                ? $"TIA-Compile erfolgreich: {result.TargetName}, {result.WarningCount} Warnung(en), {result.DurationMilliseconds} ms. Stand {completedAt}; Projekt nicht gespeichert."
                : $"TIA-Compile fehlgeschlagen: {result.ErrorCount} Fehler, {result.WarningCount} Warnungen. Stand {completedAt}; Details stehen unten und im Log.";
            if (result.Success)
                _log.Information("TIA Quality Gate", StatusText);
            else
                _log.Warning("TIA Quality Gate", StatusText);
        });
    }

    private static IEnumerable<TiaCompileMessageRowVM> FlattenCompileMessages(
        IEnumerable<TiaCompileMessage> messages,
        int depth = 0)
    {
        foreach (var message in messages)
        {
            yield return new TiaCompileMessageRowVM(message, depth);
            foreach (var child in FlattenCompileMessages(message.Children, depth + 1))
                yield return child;
        }
    }

    private void BrowseAxisExchange()
    {
        var selected = _folderSelection.SelectFolder("Ordner für Achsen-Austausch auswählen", AxisExchangePath);
        if (selected is not null)
            AxisExchangePath = selected;
    }

    private void BrowseAxisArtifact()
    {
        var selected = _folderSelection.SelectFolder(
            "Ablagewurzel für AxisDB/AxisFC auswählen",
            AxisArtifactPath);
        if (selected is not null)
            AxisArtifactPath = selected;
    }

    private async Task ExportAxisConfigurationsAsync()
    {
        if (!CanUseAxisExchange())
            return;
        await RunBusyAsync("TO-Konfigurationen werden exportiert …", async () =>
        {
            var result = await _client.ExportAxisConfigurationsAsync(AxisExchangePath);
            StatusText = $"{result.AxisCount} Achse(n) mit {result.ParameterCount} Parameter(n) in {result.FileCount} Datei(en) exportiert.";
            LogTransferWarnings("TIA TO-Export", result.Warnings);
        });
    }

    private async Task ConfigureAxesAndCreateFilesAsync()
    {
        var selectedFoundIds = FoundAxes
            .Where(axis => axis.IsSelected)
            .Select(axis => axis.Id)
            .ToArray();
        var selectedConfiguredNames = ConfiguredAxes
            .Where(axis => axis.IsSelected)
            .Select(axis => axis.Name)
            .ToArray();
        if (selectedFoundIds.Length == 0 && selectedConfiguredNames.Length == 0)
        {
            StatusText = "Keine gefundene oder bereits konfigurierte Achse ausgewählt.";
            return;
        }
        if (string.IsNullOrWhiteSpace(AxisArtifactPath))
        {
            StatusText = "Bitte die Ablagewurzel für AxisDB/AxisFC im Bereich Simulationsachsen auswählen.";
            return;
        }
        if (string.IsNullOrWhiteSpace(SelectedVersion))
        {
            StatusText = "Bitte zuerst eine TIA-Version auswählen.";
            return;
        }

        await RunBusyAsync("Achsen werden konfiguriert und AxisDB/AxisFC erzeugt …", async () =>
        {
            OperationProgress = 5;
            var configured = selectedFoundIds.Length == 0
                ? []
                : await ConfigureSelectedAxesCoreAsync(selectedFoundIds, 5, 75);
            OperationProgress = 75;
            var successful = configured
                .Where(axis => axis.ParameterResults.Count > 0 &&
                               axis.ParameterResults.All(parameter => parameter.Success))
                .Select(axis => axis.Name)
                .Concat(selectedConfiguredNames)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (successful.Length == 0)
                throw new InvalidOperationException(
                    "Keine ausgewählte Achse ist vollständig konfiguriert; AxisDB/AxisFC wurden nicht erzeugt.");

            var artifacts = await _libraryService.CreateAxisArtifactsAsync(
                AxisArtifactPath,
                successful,
                SelectedVersion);
            OperationProgress = 100;
            StatusText = $"AxisDB.xml und AxisFC.xml für {artifacts.AxisCount} Achse(n) unter " +
                         $"'{artifacts.OutputFolder}' erzeugt. Noch nicht importiert oder gespeichert.";
            _log.Information("TIA Achsenkonfiguration", StatusText);
        });
    }

    private async Task<IReadOnlyList<TiaAxisInfo>> ConfigureSelectedAxesCoreAsync(
        IReadOnlyCollection<string> selectedIds,
        int progressStart = 0,
        int progressEnd = 100)
    {
        var configured = new List<TiaAxisInfo>();
        var index = 0;
        OperationProgress = progressStart;
        foreach (var selectedId in selectedIds)
        {
            StatusText = $"Achse {index + 1}/{selectedIds.Count} wird konfiguriert: {selectedId}";
            var axisResults = await _client.ConfigureAxesAsync([selectedId]);
            foreach (var result in axisResults)
            {
                configured.Add(result);
                ApplyAxisConfigurationResult(result);
                LogAxisConfigurationResult(result);
            }
            index++;
            OperationProgress = progressStart +
                                (int)Math.Round((progressEnd - progressStart) * index / (double)selectedIds.Count);
        }
        return configured;
    }

    private void ApplyAxisConfigurationResult(TiaAxisInfo result)
    {
        var row = FoundAxes.FirstOrDefault(axis =>
            string.Equals(axis.Id, result.Id, StringComparison.OrdinalIgnoreCase));
        if (row is null)
            return;

        row.ApplyConfigurationResult(result);
        if (result.ParameterResults.Count > 0 && result.ParameterResults.All(parameter => parameter.Success))
        {
            FoundAxes.Remove(row);
            ConfiguredAxes.Add(row);
        }
    }

    private void LogAxisConfigurationResult(TiaAxisInfo axis)
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

    private async Task ImportAxisConfigurationsAsync()
    {
        if (!CanUseAxisExchange())
            return;
        await RunBusyAsync("TO-Konfigurationen werden importiert …", async () =>
        {
            var result = await _client.ImportAxisConfigurationsAsync(AxisExchangePath);
            StatusText = $"{result.AxisCount} Achse(n) aktualisiert; {result.ParameterCount} Parameter gesetzt. Projekt noch nicht gespeichert.";
            LogTransferWarnings("TIA TO-Import", result.Warnings);
        });
    }

    private async Task ExportAxisInterfaceAsync()
    {
        if (!CanUseAxisExchange())
            return;
        await RunBusyAsync("Achsen-Schnittstelle wird erzeugt …", async () =>
        {
            var result = await _client.ExportAxisInterfaceWorkbookAsync(
                Path.Combine(AxisExchangePath, "AxisValueTags.xlsx"));
            StatusText = $"Achsen-Schnittstelle für {result.AxisCount} Achse(n) geschrieben: {result.FilePath}";
        });
    }

    private bool CanUseAxisExchange()
    {
        if (SelectedPlc is null)
        {
            StatusText = "Bitte zuerst TIA verbinden und die gewünschte PLC auswählen.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(AxisExchangePath))
        {
            StatusText = "Bitte zuerst einen Ordner für den Achsen-Austausch auswählen.";
            return false;
        }
        return true;
    }

    private void LogTransferWarnings(string area, IReadOnlyList<string> warnings)
    {
        foreach (var warning in warnings)
            _log.Warning(area, warning);
        if (warnings.Count > 0)
            StatusText += $" {warnings.Count} Warnung(en) stehen im Log.";
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
                false,
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
        using var measurement = PerformanceMeasurementService.Instance.Start("TIA Portal", status);
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            measurement.MarkFailed();
            StatusText = "TIA-Vorgang wurde abgebrochen.";
            _log.Warning("TIA Portal", StatusText);
        }
        catch (Exception exception)
        {
            measurement.MarkFailed();
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

public sealed record TiaCompileMessageRowVM(
    string Path,
    string State,
    string Description,
    int ErrorCount,
    int WarningCount,
    int Depth)
{
    public TiaCompileMessageRowVM(TiaCompileMessage message, int depth)
        : this(message.Path, message.State, message.Description, message.ErrorCount, message.WarningCount, depth)
    {
    }

    public bool IsError => ErrorCount > 0 ||
                           State.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                           State.Contains("Failed", StringComparison.OrdinalIgnoreCase);

    public bool IsRelevant => IsError || WarningCount > 0 ||
                              State.Contains("Warning", StringComparison.OrdinalIgnoreCase);
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
