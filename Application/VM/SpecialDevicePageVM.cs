using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Win32;
using VIBN_Tools.Core.ViCo;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.SpecialDevices;
using VIBN_Tools.Tia.Client;
using VIBN_Tools.Tia.Contracts;
using VIBN_Tools.Settings;
using VIBN_Tools.Core.Collections;
using static VIBN_Tools.SpecialDevices.DeviceCatalog;

namespace VIBN_Tools.Application.VM;

/// <summary>
/// Coordinates manual Special Device creation and the TIA-backed hardware
/// staging workflow. TIA data is read through the isolated bridge; actual FEE
/// creation starts only after the user has reviewed the generated queue.
/// </summary>
public sealed class SpecialDevicePageVM : MvvmBase, IAsyncDisposable
{
    private readonly ITiaBridgeClient _tiaClient;
    private readonly ITiaHardwareMappingStore _hardwareMappingStore;
    private readonly IApplicationLog _log;
    private CancellationTokenSource? _tiaOperationCancellation;
    private bool _isBusyTia;
    private bool _isDisconnectingTia;
    private bool _isTiaAttached;
    private bool _isBusyCreateDevices;
    private DeviceManufacturer? _selectedManufacturer;
    private object? _selectedDevice;
    private RobotType? _selectedRobotType;
    private string _devicePrefix = string.Empty;
    private int _deviceAddressInput;
    private int _deviceAddressOutput;
    private bool _isEnabledDevicePrefix = true;
    private bool _isEnabledDeviceAddress = true;
    private bool _showRobotType;
    private string? _selectedTiaVersion;
    private TiaPlcInfo? _selectedTiaPlc;
    private int _selectedDeviceIndex = -1;
    private string _statusText = "SpecialDevices2FEE ist bereit.";
    private int _deviceGenerationProgress;
    private string _deviceGenerationProgressText = string.Empty;
    private readonly List<FeeSpecialDeviceSnapshot> _loadedReverseSnapshots = [];
    private string _hardwareComparisonText = "Noch kein FEE2-JSON zum Vergleich geladen.";

    public SpecialDevicePageVM(
        ITiaBridgeClient tiaClient,
        IReadOnlyList<string> installedTiaVersions,
        ITiaHardwareMappingStore hardwareMappingStore,
        IApplicationLog? log = null)
    {
        _tiaClient = tiaClient ?? throw new ArgumentNullException(nameof(tiaClient));
        _hardwareMappingStore = hardwareMappingStore ?? throw new ArgumentNullException(nameof(hardwareMappingStore));
        _log = log ?? NullApplicationLog.Instance;

        foreach (var version in installedTiaVersions)
            InstalledTiaVersions.Add(version);
        SelectedTiaVersion = InstalledTiaVersions.FirstOrDefault();

        AddSpecialDeviceCommand = GetCommandBinding(AddSpecialDevice);
        ConnectTiaCommand = GetCommandBindingAsync(ConnectTiaAsync);
        DisconnectTiaCommand = GetCommandBindingAsync(DisconnectTiaAsync);
        SelectTiaPlcCommand = GetCommandBindingAsync(SelectTiaPlcAsync);
        ReadTiaHardwareCommand = GetCommandBindingAsync(ReadTiaHardwareAsync);
        SaveTiaHardwareMappingCommand = GetCommandBindingAsync(SaveTiaHardwareMappingAsync);
        AddSelectedHardwareDevicesCommand = GetCommandBinding(AddSelectedHardwareDevices);
        DeleteSelectedDevicesCommand = GetCommandBinding(DeleteSelectedDevice);
        DeleteAllDevicesCommand = GetCommandBinding(DeleteAllDevices);
        LoadReverseSnapshotCommand = GetCommandBinding(LoadReverseSnapshot);
        CompareReverseWithTiaCommand = GetCommandBinding(CompareReverseWithTia);
        CreateSpecialDevicesCommand = GetCommandBindingAsync(CreateSpecialDevicesAsync);
        if (Connection is not null)
            Connection.PropertyChanged += OnFeeConnectionPropertyChanged;
    }

    public ObservableCollection<object> DeviceTypes { get; } = new();

    public ObservableCollection<SpecialDevice> SpecialDevices { get; } = new();

    public ObservableCollection<string> InstalledTiaVersions { get; } = new();

    public ObservableCollection<TiaPlcInfo> TiaPlcs { get; } = new();

    public ObservableCollection<TiaHardwareDeviceRowVM> TiaHardwareRows { get; } = new();

    public IReadOnlyList<SpecialDeviceLogicOption> HardwareLogicOptions => SpecialDeviceLogicOption.Selectable;

    public IEnumerable<DeviceManufacturer> Manufacturers => Enum.GetValues<DeviceManufacturer>();

    public IEnumerable<RobotType> RobotTypes => Enum.GetValues<RobotType>();

    public ICommand AddSpecialDeviceCommand { get; }

    public ICommand ConnectTiaCommand { get; }

    public ICommand DisconnectTiaCommand { get; }

    public ICommand SelectTiaPlcCommand { get; }

    public ICommand ReadTiaHardwareCommand { get; }

    public ICommand SaveTiaHardwareMappingCommand { get; }

    public ICommand AddSelectedHardwareDevicesCommand { get; }

    public ICommand DeleteSelectedDevicesCommand { get; }

    public ICommand DeleteAllDevicesCommand { get; }

    public ICommand CreateSpecialDevicesCommand { get; }

    public ICommand LoadReverseSnapshotCommand { get; }

    public ICommand CompareReverseWithTiaCommand { get; }

    public string HardwareComparisonText
    {
        get => _hardwareComparisonText;
        private set
        {
            _hardwareComparisonText = value;
            OnPropertyChanged();
        }
    }

    public FeeConnectionService? Connection => Services.Connection;

    public DeviceManufacturer? SelectedManufacturer
    {
        get => _selectedManufacturer;
        set
        {
            if (_selectedManufacturer == value)
                return;
            _selectedManufacturer = value;
            OnPropertyChanged();
            LoadDeviceTypesForManufacturer();
        }
    }

    public object? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (ReferenceEquals(_selectedDevice, value))
                return;
            _selectedDevice = value;
            OnPropertyChanged();
            var isSimMode = value is GrobDeviceTypes.SimModeSiemens;
            IsEnabledDevicePrefix = !isSimMode;
            IsEnabledDeviceAddress = !isSimMode;
            if (isSimMode)
                DevicePrefix = "SimMode";
            ShowRobotType = value is not null && RobotTypeDevices.Contains(value);
        }
    }

    public RobotType? SelectedRobotType
    {
        get => _selectedRobotType;
        set
        {
            _selectedRobotType = value;
            OnPropertyChanged();
        }
    }

    public bool ShowRobotType
    {
        get => _showRobotType;
        private set
        {
            _showRobotType = value;
            OnPropertyChanged();
        }
    }

    public string DevicePrefix
    {
        get => _devicePrefix;
        set
        {
            _devicePrefix = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    public bool IsEnabledDevicePrefix
    {
        get => _isEnabledDevicePrefix;
        private set
        {
            _isEnabledDevicePrefix = value;
            OnPropertyChanged();
        }
    }

    public int DeviceAddressInput
    {
        get => _deviceAddressInput;
        set
        {
            _deviceAddressInput = value;
            OnPropertyChanged();
        }
    }

    public int DeviceAddressOutput
    {
        get => _deviceAddressOutput;
        set
        {
            _deviceAddressOutput = value;
            OnPropertyChanged();
        }
    }

    /// <remarks>
    /// This preserves the established manual-address convention used by the
    /// original Special Device implementation.
    /// </remarks>
    public SpecialDeviceAddresses DeviceAddresses =>
        new(DeviceAddressInput, DeviceAddressOutput);

    public bool IsEnabledDeviceAddress
    {
        get => _isEnabledDeviceAddress;
        private set
        {
            _isEnabledDeviceAddress = value;
            OnPropertyChanged();
        }
    }

    public string? SelectedTiaVersion
    {
        get => _selectedTiaVersion;
        set
        {
            if (string.Equals(_selectedTiaVersion, value, StringComparison.Ordinal))
                return;
            _selectedTiaVersion = value;
            OnPropertyChanged();
            NotifyTiaCommandState();
        }
    }

    public TiaPlcInfo? SelectedTiaPlc
    {
        get => _selectedTiaPlc;
        set
        {
            if (ReferenceEquals(_selectedTiaPlc, value))
                return;
            _selectedTiaPlc = value;
            OnPropertyChanged();
            NotifyTiaCommandState();
        }
    }

    public bool IsBusyTia
    {
        get => _isBusyTia;
        private set
        {
            _isBusyTia = value;
            OnPropertyChanged();
            NotifyTiaCommandState();
        }
    }

    public bool CanConnectTia =>
        !IsBusyTia && !_isDisconnectingTia && !_isTiaAttached &&
        !string.IsNullOrWhiteSpace(SelectedTiaVersion);

    public bool CanDisconnectTia => IsBusyTia || _isTiaAttached || _tiaClient.IsConnected;

    public bool CanSelectTiaPlc => !IsBusyTia && _isTiaAttached && SelectedTiaPlc is not null;

    public bool CanReadTiaHardware => CanSelectTiaPlc;

    public string ConnectTiaUnavailableReason => CanConnectTia
        ? "Verbindet die Anwendung mit dem geöffneten TIA-Projekt der gewählten Version."
        : IsBusyTia
            ? "Ein TIA-Vorgang läuft bereits."
            : _isDisconnectingTia
                ? "Die bestehende TIA-Verbindung wird gerade getrennt."
                : _isTiaAttached || _tiaClient.IsConnected
                    ? "Die Anwendung ist bereits mit TIA verbunden."
                    : "Zuerst eine installierte TIA-Version auswählen.";

    public string DisconnectTiaUnavailableReason => CanDisconnectTia
        ? "Bricht einen laufenden Lesevorgang ab und trennt ausschließlich die VIBN-TIA-Bridge."
        : "Es besteht keine TIA-Verbindung und kein TIA-Vorgang läuft.";

    public string SelectTiaPlcUnavailableReason => CanSelectTiaPlc
        ? "Übernimmt die ausgewählte PLC als Quelle für die Hardwarediagnose."
        : IsBusyTia
            ? "Ein TIA-Vorgang läuft bereits."
            : !_isTiaAttached
                ? "Zuerst mit einem geöffneten TIA-Projekt verbinden."
                : "Zuerst eine PLC auswählen.";

    public string ReadTiaHardwareUnavailableReason => CanReadTiaHardware
        ? "Liest die Hardware der ausgewählten PLC ohne das TIA-Projekt zu verändern."
        : SelectTiaPlcUnavailableReason;

    public bool IsBusyCreateDevices
    {
        get => _isBusyCreateDevices;
        private set
        {
            _isBusyCreateDevices = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanModifyDeviceQueue));
            OnPropertyChanged(nameof(CanCreateInFee));
            OnPropertyChanged(nameof(DeviceQueueUnavailableReason));
            OnPropertyChanged(nameof(CreateInFeeUnavailableReason));
        }
    }

    public bool CanModifyDeviceQueue => !IsBusyCreateDevices;

    public bool CanCreateInFee => CanModifyDeviceQueue && Connection?.CanUseFeeFeatures == true;

    public string DeviceQueueUnavailableReason => CanModifyDeviceQueue
        ? "Bearbeitet die Special-Device-Warteschlange."
        : "Die Warteschlange ist während der laufenden FEE-Erzeugung gesperrt.";

    public string CreateInFeeUnavailableReason => CanCreateInFee
        ? "Erzeugt alle Geräte aus der Warteschlange in FEE."
        : !CanModifyDeviceQueue
            ? "Eine Special-Device-Erzeugung läuft bereits."
            : Connection?.UnavailableReason ?? "Keine Verbindung zu FEE vorhanden.";

    public int SelectedDeviceIndex
    {
        get => _selectedDeviceIndex;
        set
        {
            _selectedDeviceIndex = value;
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

    public async ValueTask DisposeAsync()
    {
        if (Connection is not null)
            Connection.PropertyChanged -= OnFeeConnectionPropertyChanged;
        _tiaOperationCancellation?.Cancel();
        _tiaOperationCancellation?.Dispose();
        _tiaOperationCancellation = null;
        await _tiaClient.DisposeAsync();
    }

    private void OnFeeConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(FeeConnectionService.IsConnected) or
            nameof(FeeConnectionService.CanUseFeeFeatures))
        {
            OnPropertyChanged(nameof(CanCreateInFee));
            OnPropertyChanged(nameof(CreateInFeeUnavailableReason));
        }
    }

    private void AddSpecialDevice()
    {
        if (SelectedManufacturer is null || SelectedDevice is not Enum deviceType)
        {
            StatusText = "Für ein manuelles Gerät Hersteller und Gerätetyp auswählen.";
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(DevicePrefix))
            {
                StatusText = "Für das manuelle Gerät ist ein Präfix erforderlich.";
                return;
            }
            if (ShowRobotType && SelectedRobotType is null)
            {
                StatusText = "Für diese Logik muss ein Robotertyp ausgewählt werden.";
                return;
            }
            if (SpecialDevices.Any(device =>
                    string.Equals(device.DevicePrefix, DevicePrefix.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                StatusText = $"Das Präfix '{DevicePrefix.Trim()}' ist bereits in der Warteschlange. Bitte ein eindeutiges Präfix verwenden.";
                return;
            }

            var device = DeviceFactory.Create(
                SelectedManufacturer.Value,
                deviceType,
                DevicePrefix.Trim(),
                DeviceAddresses,
                SelectedRobotType);
            SpecialDevices.Add(device);
            StatusText = $"{DeviceCatalog.GetDisplayName(deviceType)} wurde mit der Logik '{device.DeviceLogicObject.LogicDefinitionName}' zur Warteschlange hinzugefügt.";
        }
        catch (Exception exception)
        {
            StatusText = "Manuelles Special Device konnte nicht vorbereitet werden.";
            _log.Error("SpecialDevices2FEE", StatusText, exception);
        }
    }

    private async Task ConnectTiaAsync()
    {
        if (IsBusyTia || string.IsNullOrWhiteSpace(SelectedTiaVersion))
            return;

        await RunTiaBusyAsync("Verbindung zu TIA Portal wird hergestellt …", async cancellationToken =>
        {
            await _tiaClient.ConnectAsync(cancellationToken);
            if (!await _tiaClient.PingAsync(cancellationToken))
                throw new InvalidOperationException("TIA Bridge antwortet nicht.");
            await _tiaClient.SelectVersionAsync(SelectedTiaVersion, cancellationToken);
            await _tiaClient.AttachAsync(cancellationToken);
            TiaPlcs.ReplaceWith(await _tiaClient.ListPlcsAsync(cancellationToken));
            SelectedTiaPlc = TiaPlcs.FirstOrDefault();
            TiaHardwareRows.Clear();
            _isTiaAttached = true;
            NotifyTiaCommandState();
            StatusText = $"Mit TIA Portal {SelectedTiaVersion} verbunden; {TiaPlcs.Count} PLC(s) gefunden.";
        });
    }

    private async Task DisconnectTiaAsync()
    {
        if (_isDisconnectingTia)
            return;

        _isDisconnectingTia = true;
        _tiaOperationCancellation?.Cancel();
        NotifyTiaCommandState();
        try
        {
            await _tiaClient.DisconnectAsync();
            ResetTiaUi();
            StatusText = "TIA-Verbindungsaufbau abgebrochen und Session getrennt.";
            _log.Information("TIA Hardware", StatusText);
        }
        catch (Exception exception)
        {
            ResetTiaUi();
            StatusText = $"TIA-Session wurde lokal zurückgesetzt; Bridge-Abschluss fehlgeschlagen: {exception.Message}";
            _log.Error("TIA Hardware", StatusText, exception);
        }
        finally
        {
            _isDisconnectingTia = false;
            NotifyTiaCommandState();
        }
    }

    private async Task SelectTiaPlcAsync()
    {
        if (SelectedTiaPlc is null)
            return;

        await RunTiaBusyAsync("PLC wird ausgewählt …", async cancellationToken =>
        {
            await _tiaClient.SelectPlcAsync(SelectedTiaPlc.Index, cancellationToken);
            TiaHardwareRows.Clear();
            StatusText = $"PLC '{SelectedTiaPlc.Name}' ist ausgewählt.";
        });
    }

    private async Task ReadTiaHardwareAsync()
    {
        if (SelectedTiaPlc is null)
        {
            StatusText = "Zuerst mit TIA verbinden und eine PLC auswählen.";
            return;
        }

        await RunTiaBusyAsync("TIA-Hardwarekonfiguration wird gelesen …", async cancellationToken =>
        {
            await _tiaClient.SelectPlcAsync(SelectedTiaPlc.Index, cancellationToken);
            var modules = await _tiaClient.ListHardwareAsync(cancellationToken);
            var savedMappings = await _hardwareMappingStore.LoadAsync(cancellationToken);
            var candidates = modules
                .Select(module => new TiaHardwareDeviceRowVM(module))
                .Where(row => row.InputByte.HasValue || row.OutputByte.HasValue || row.SelectedLogic is not null)
                .ToArray();
            var restored = 0;
            foreach (var candidate in candidates)
            {
                if ((savedMappings.TryGetValue(candidate.MappingKey, out var mapping) ||
                     (candidate.Module.AddressSetIndex == 0 &&
                      savedMappings.TryGetValue(candidate.LegacyMappingKey, out mapping))) &&
                    candidate.ApplyMapping(mapping))
                {
                    restored++;
                }
            }
            TiaHardwareRows.ReplaceWith(candidates);
            var addressed = candidates.Count(row => row.InputByte.HasValue || row.OutputByte.HasValue);
            var deviceCount = candidates
                .Select(row => row.DeviceGroupName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            StatusText = $"{modules.Count} Hardwareelement(e) traversiert; " +
                         $"{candidates.Length} relevante Modulzeile(n) in {deviceCount} Gerät(en), " +
                         $"davon {addressed} mit E-/A-Adresse und {restored} gespeicherte Zuordnung(en). " +
                         "Logik und Byteadressen prüfen, Zuordnung speichern und dann übernehmen.";
            CompareReverseWithTia();
        });
    }

    private async Task SaveTiaHardwareMappingAsync()
    {
        if (TiaHardwareRows.Count == 0)
        {
            StatusText = "Es sind keine TIA-Hardwarezuordnungen zum Speichern vorhanden.";
            return;
        }

        try
        {
            await _hardwareMappingStore.SaveAsync(
                TiaHardwareRows.Select(row => row.ToMapping()).ToArray());
            foreach (var row in TiaHardwareRows)
                row.MarkConfigurationSaved();
            StatusText = $"{TiaHardwareRows.Count} TIA-Hardwarezuordnung(en) wurden lokal gespeichert.";
            _log.Information("TIA Hardware", StatusText);
        }
        catch (Exception exception)
        {
            StatusText = $"TIA-Hardwarezuordnungen konnten nicht gespeichert werden: {exception.Message}";
            _log.Error("TIA Hardware", StatusText, exception);
        }
    }

    private void AddSelectedHardwareDevices()
    {
        var errors = new List<string>();
        var added = 0;
        var skippedWithoutLogic = 0;
        foreach (var row in TiaHardwareRows.Where(row => row.Include && !row.IsAdded))
        {
            if (row.SelectedLogic is null || row.SelectedLogic.IsEmpty)
            {
                skippedWithoutLogic++;
                continue;
            }

            if (!row.TryCreate(out var device, out var error))
            {
                if (error.Length > 0)
                    errors.Add(error);
                continue;
            }

            if (device is null)
                continue;
            var alreadyQueued = SpecialDevices.Any(existing =>
                string.Equals(existing.DevicePrefix, device.DevicePrefix, StringComparison.OrdinalIgnoreCase) &&
                Equals(existing.DeviceType, device.DeviceType) &&
                existing.DeviceAddresses == device.DeviceAddresses);
            if (alreadyQueued)
            {
                errors.Add($"{row.ModuleName}: Dieses Gerät ist bereits in der Warteschlange.");
                continue;
            }

            SpecialDevices.Add(device);
            row.IsAdded = true;
            added++;
        }

        var skipMessage = skippedWithoutLogic == 0
            ? string.Empty
            : $" {skippedWithoutLogic} ausgewählte Zeile(n) ohne Logik wurden bewusst übersprungen.";
        StatusText = errors.Count == 0
            ? $"{added} TIA-Hardwareelement(e) wurden in die Warteschlange übernommen.{skipMessage}"
            : $"{added} Gerät(e) übernommen; {errors.Count} Zuordnung(en) prüfen: {string.Join(" ", errors.Take(3))}{skipMessage}";
        if (errors.Count > 0)
            _log.Warning("SpecialDevices2FEE", StatusText);
    }

    private void DeleteSelectedDevice()
    {
        if (SelectedDeviceIndex is >= 0 and < int.MaxValue && SelectedDeviceIndex < SpecialDevices.Count)
            SpecialDevices.RemoveAt(SelectedDeviceIndex);
    }

    private void DeleteAllDevices()
    {
        SpecialDevices.Clear();
        foreach (var row in TiaHardwareRows)
            row.IsAdded = false;
        StatusText = "Die Special-Device-Warteschlange wurde geleert.";
    }

    public int DeviceGenerationProgress
    {
        get => _deviceGenerationProgress;
        private set
        {
            _deviceGenerationProgress = Math.Clamp(value, 0, 100);
            OnPropertyChanged();
        }
    }

    public string DeviceGenerationProgressText
    {
        get => _deviceGenerationProgressText;
        private set
        {
            _deviceGenerationProgressText = value;
            OnPropertyChanged();
        }
    }

    private void LoadReverseSnapshot()
    {
        var dialog = new OpenFileDialog
        {
            Title = "FEE2SpecialDevices-Export laden",
            Filter = "VIBN Special Device (*.specialdevice.json)|*.specialdevice.json|JSON (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
            return;
        if (!FeeSpecialDeviceProvenanceCodec.TryLoadFile(dialog.FileName, out var snapshot, out var readError))
        {
            StatusText = readError;
            _log.Warning("SpecialDevices2FEE", StatusText);
            return;
        }

        var import = SpecialDeviceSnapshotImporter.Create(snapshot!);
        if (!import.Success || import.Device is null)
        {
            StatusText = $"FEE2SpecialDevices-Export wurde nicht übernommen: {import.Error}";
            _log.Warning("SpecialDevices2FEE", StatusText);
            return;
        }
        if (SpecialDevices.Any(device =>
                string.Equals(device.DevicePrefix, import.Device.DevicePrefix, StringComparison.OrdinalIgnoreCase) &&
                device.DeviceManufacturer == import.Device.DeviceManufacturer))
        {
            StatusText = $"Gerät '{import.Device.DevicePrefix}' ist bereits in der Warteschlange.";
            _log.Warning("SpecialDevices2FEE", StatusText);
            return;
        }

        SpecialDevices.Add(import.Device);
        _loadedReverseSnapshots.Add(snapshot!);
        CompareReverseWithTia();
        StatusText = import.Warnings.Count == 0
            ? $"{import.Device.DevicePrefix} wurde aus dem FEE2SpecialDevices-Export in die Warteschlange übernommen."
            : $"{import.Device.DevicePrefix} wurde übernommen. Prüfung erforderlich: {string.Join(" ", import.Warnings)}";
        if (import.Warnings.Count == 0)
            _log.Information("SpecialDevices2FEE", StatusText);
        else
            _log.Warning("SpecialDevices2FEE", StatusText);
    }

    private void CompareReverseWithTia()
    {
        if (_loadedReverseSnapshots.Count == 0)
        {
            HardwareComparisonText = "Noch kein FEE2-JSON zum Vergleich geladen.";
            return;
        }
        if (TiaHardwareRows.Count == 0)
        {
            HardwareComparisonText = $"{_loadedReverseSnapshots.Count} FEE2-JSON-Gerät(e) geladen; zuerst TIA-Hardware auslesen.";
            return;
        }

        var lines = _loadedReverseSnapshots.Select(snapshot =>
        {
            var matches = TiaHardwareRows.Where(row =>
                    string.Equals(row.Prefix, snapshot.Prefix, StringComparison.OrdinalIgnoreCase) ||
                    (row.InputByte == snapshot.InputByte && row.OutputByte == snapshot.OutputByte))
                .ToArray();
            if (matches.Length == 0)
                return $"{snapshot.Prefix}: kein TIA-Treffer (JSON E{snapshot.InputByte}/A{snapshot.OutputByte}).";
            var exact = matches.FirstOrDefault(row =>
                string.Equals(row.Prefix, snapshot.Prefix, StringComparison.OrdinalIgnoreCase) &&
                row.InputByte == snapshot.InputByte && row.OutputByte == snapshot.OutputByte);
            if (exact is not null)
                return $"{snapshot.Prefix}: stimmt mit TIA '{exact.ModuleName}' überein (E{snapshot.InputByte}/A{snapshot.OutputByte}).";
            return $"{snapshot.Prefix}: {matches.Length} möglicher TIA-Treffer, Präfix oder Byteadressen weichen ab – prüfen.";
        });
        HardwareComparisonText = string.Join(Environment.NewLine, lines);
    }

    private async Task CreateSpecialDevicesAsync()
    {
        if (!Connection.CanUseFeeFeatures)
        {
            StatusText = FeeConnectionService.MissingConnectionMessage;
            _log.Warning("SpecialDevices2FEE", StatusText);
            return;
        }

        if (IsBusyCreateDevices || SpecialDevices.Count == 0)
            return;

        IsBusyCreateDevices = true;
        var created = new List<SpecialDevice>();
        var alreadyPresent = new List<SpecialDevice>();
        var failures = new List<string>();
        try
        {
            // FEE object creation is intentionally serialized. The underlying
            // SDK keeps a shared connection and is more reliable than an
            // unbounded parallel write burst.
            var pendingDevices = SpecialDevices.ToArray();
            DeviceGenerationProgress = 0;
            for (var index = 0; index < pendingDevices.Length; index++)
            {
                var device = pendingDevices[index];
                DeviceGenerationProgressText =
                    $"Gerät {index + 1} von {pendingDevices.Length}: {device.DevicePrefix} wird geprüft …";
                try
                {
                    if (await device.ExistsInFeeAsync())
                    {
                        alreadyPresent.Add(device);
                        _log.Information(
                            "SpecialDevices2FEE",
                            $"{device.DevicePrefix}: gleichnamiger Geräte-BasicFrame ist bereits vorhanden; Warteschlangeneintrag wird entfernt.");
                    }
                    else if (await device.CreateAsync())
                    {
                        created.Add(device);
                        if (!string.IsNullOrWhiteSpace(device.LastCreationWarning))
                        {
                            _log.Warning("SpecialDevices2FEE", $"{device.DevicePrefix}: {device.LastCreationWarning}");
                        }
                    }
                    else
                        failures.Add($"{device.DevicePrefix}: FEE hat keine erfolgreiche Erstellung bestätigt.");
                }
                catch (Exception exception)
                {
                    failures.Add($"{device.DevicePrefix}: {exception.Message}");
                    _log.Error("SpecialDevices2FEE", $"Gerät {device.DevicePrefix} konnte nicht erzeugt werden.", exception);
                }

                DeviceGenerationProgress = (index + 1) * 100 / pendingDevices.Length;

                // A failed attempt can already have created partial FEE
                // objects. Stop here so later queue entries are not attempted
                // against an uncertain shared SDK state.
                if (failures.Count > 0)
                    break;
            }

            foreach (var device in created.Concat(alreadyPresent))
                SpecialDevices.Remove(device);
            var provenanceWarnings = created.Count(device => !string.IsNullOrWhiteSpace(device.LastCreationWarning));
            StatusText = failures.Count == 0
                ? $"{created.Count} Special Device(s) wurden erstellt; {alreadyPresent.Count} bereits vorhandene aus der Warteschlange entfernt." +
                  (provenanceWarnings > 0 ? $" {provenanceWarnings} Provenienzhinweis(e) stehen im Protokoll." : string.Empty)
                : $"{created.Count} Gerät(e) erstellt, {alreadyPresent.Count} bereits vorhanden; {failures.Count} Gerät(e) bleiben zur Prüfung in der Warteschlange.";
            DeviceGenerationProgressText = failures.Count == 0
                ? "FEE-Erzeugung abgeschlossen."
                : "FEE-Erzeugung mit Fehler beendet; Details stehen im Status und Protokoll.";
        }
        finally
        {
            IsBusyCreateDevices = false;
        }
    }

    private async Task RunTiaBusyAsync(
        string initialStatus,
        Func<CancellationToken, Task> action)
    {
        if (IsBusyTia)
            return;

        var cancellation = new CancellationTokenSource();
        _tiaOperationCancellation = cancellation;
        IsBusyTia = true;
        StatusText = initialStatus;
        try
        {
            await action(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (!_isDisconnectingTia)
            {
                StatusText = "TIA-Hardwarevorgang wurde abgebrochen.";
                _log.Information("TIA Hardware", StatusText);
            }
        }
        catch (Exception exception)
        {
            StatusText = $"TIA-Hardwarevorgang fehlgeschlagen: {exception.Message}";
            _log.Error("TIA Hardware", StatusText, exception);
        }
        finally
        {
            if (ReferenceEquals(_tiaOperationCancellation, cancellation))
                _tiaOperationCancellation = null;
            cancellation.Dispose();
            IsBusyTia = false;
        }
    }

    private void ResetTiaUi()
    {
        _isTiaAttached = false;
        SelectedTiaPlc = null;
        TiaPlcs.Clear();
        TiaHardwareRows.Clear();
    }

    private void NotifyTiaCommandState()
    {
        OnPropertyChanged(nameof(CanConnectTia));
        OnPropertyChanged(nameof(CanDisconnectTia));
        OnPropertyChanged(nameof(CanSelectTiaPlc));
        OnPropertyChanged(nameof(CanReadTiaHardware));
        OnPropertyChanged(nameof(ConnectTiaUnavailableReason));
        OnPropertyChanged(nameof(DisconnectTiaUnavailableReason));
        OnPropertyChanged(nameof(SelectTiaPlcUnavailableReason));
        OnPropertyChanged(nameof(ReadTiaHardwareUnavailableReason));
    }

    private void LoadDeviceTypesForManufacturer()
    {
        DeviceTypes.Clear();
        SelectedDevice = null;
        if (SelectedManufacturer is null)
            return;

        foreach (var deviceType in Enum.GetValues(DeviceCatalog.DeviceTypeEnums[SelectedManufacturer.Value]))
            DeviceTypes.Add(deviceType!);
    }

    private static readonly HashSet<object> RobotTypeDevices = new()
    {
        AtlasCopcoDeviceTypes.Sys6000_Glueing_BMW,
        AtlasCopcoDeviceTypes.Sys6000_Glueing_VASS
    };
}
