using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Data;
using System.Xml.Linq;
using Microsoft.Win32;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.Core.Collections;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.SharedWpf.Commands;
using VIBN_Tools.Settings;

namespace VIBN_Tools.Application.VM;

public sealed class Fee2ContainerPageVM : MvvmBase
{
    private const string LogArea = "FEE2Container";
    private readonly Fee2ContainerService _service;
    private readonly FeeConnectionService _connection;
    private Fee2ContainerRootSelectionVM? _selectedRoot;
    private CancellationTokenSource? _operationCancellation;
    private bool _isBusy;
    private int _progressValue;
    private string _statusText = "FEE verbinden und Hauptknoten einlesen.";
    private Fee2ContainerFoundContainerVM? _selectedFoundContainer;
    private Fee2ContainerFoundSignalVM? _selectedFoundSignal;
    private Fee2ContainerFoundContainerVM? _containerRevealTarget;
    private Fee2ContainerFoundSignalVM? _signalRevealTarget;
    private string _containerSearchText = string.Empty;
    private string _signalSearchText = string.Empty;
    private string _objectSearchText = string.Empty;

    public Fee2ContainerPageVM()
        : this(new Fee2ContainerService(), Services.Connection ?? new FeeConnectionService()) { }

    internal Fee2ContainerPageVM(Fee2ContainerService service, FeeConnectionService connection)
    {
        _service = service;
        _connection = connection;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => CanRefresh);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => CanExport);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        RemoveContainerCommand = new RelayCommand<Fee2ContainerFoundContainerVM>(
            RemoveContainer,
            item => item is not null && !IsBusy);
        RemoveSignalCommand = new RelayCommand<Fee2ContainerFoundSignalVM>(
            RemoveSignal,
            item => item is not null && !IsBusy);
        AddObjectAsContainerCommand = new RelayCommand<Fee2ContainerUnmappedObjectVM>(
            AddObjectAsContainer,
            item => item is { CanAdd: true } && !IsBusy);
        FoundContainersView = CollectionViewSource.GetDefaultView(FoundContainers);
        FoundSignalsView = CollectionViewSource.GetDefaultView(FoundSignals);
        NonContainerObjectsView = CollectionViewSource.GetDefaultView(NonContainerObjects);
        FoundContainersView.Filter = item => item is Fee2ContainerFoundContainerVM container &&
            Matches(ContainerSearchText, container.Component, container.Type, container.OriginalSignalCount.ToString());
        FoundSignalsView.Filter = item => item is Fee2ContainerFoundSignalVM signal &&
            Matches(SignalSearchText, signal.Container, signal.ContainerType, signal.Signal, signal.Slot,
                signal.Address, signal.DataType, signal.SignalId, signal.AssignmentState, signal.Note);
        NonContainerObjectsView.Filter = item => item is Fee2ContainerUnmappedObjectVM feeObject &&
            Matches(ObjectSearchText, feeObject.Name, feeObject.FeeType, feeObject.Reason,
                feeObject.TargetComponent, feeObject.TargetContainerType);
        _connection.PropertyChanged += OnConnectionPropertyChanged;
    }

    public ObservableCollection<Fee2ContainerRootSelectionVM> Roots { get; } = new();
    public ObservableCollection<string> Issues { get; } = new();
    public ObservableCollection<Fee2ContainerFoundContainerVM> FoundContainers { get; } = new();
    public ObservableCollection<Fee2ContainerFoundSignalVM> FoundSignals { get; } = new();
    public ObservableCollection<Fee2ContainerUnmappedObjectVM> NonContainerObjects { get; } = new();
    public ICollectionView FoundContainersView { get; }
    public ICollectionView FoundSignalsView { get; }
    public ICollectionView NonContainerObjectsView { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RemoveContainerCommand { get; }
    public ICommand RemoveSignalCommand { get; }
    public ICommand AddObjectAsContainerCommand { get; }
    public FeeConnectionService Connection => _connection;

    public IReadOnlyList<string> SupportedContainerTypes =>
        FeeContainerLiveReconstructor.SupportedContainerTypes;

    public string ContainerSearchText
    {
        get => _containerSearchText;
        set
        {
            if (_containerSearchText == value)
                return;
            _containerSearchText = value ?? string.Empty;
            OnPropertyChanged();
            FoundContainersView.Refresh();
        }
    }

    public string SignalSearchText
    {
        get => _signalSearchText;
        set
        {
            if (_signalSearchText == value)
                return;
            _signalSearchText = value ?? string.Empty;
            OnPropertyChanged();
            FoundSignalsView.Refresh();
        }
    }

    public string ObjectSearchText
    {
        get => _objectSearchText;
        set
        {
            if (_objectSearchText == value)
                return;
            _objectSearchText = value ?? string.Empty;
            OnPropertyChanged();
            NonContainerObjectsView.Refresh();
        }
    }

    public Fee2ContainerFoundContainerVM? ContainerRevealTarget
    {
        get => _containerRevealTarget;
        private set
        {
            _containerRevealTarget = value;
            OnPropertyChanged();
        }
    }

    public Fee2ContainerFoundSignalVM? SignalRevealTarget
    {
        get => _signalRevealTarget;
        private set
        {
            _signalRevealTarget = value;
            OnPropertyChanged();
        }
    }

    public Fee2ContainerFoundContainerVM? SelectedFoundContainer
    {
        get => _selectedFoundContainer;
        set
        {
            if (ReferenceEquals(_selectedFoundContainer, value))
                return;
            _selectedFoundContainer = value;
            OnPropertyChanged();
            if (value is null)
                return;

            if (_selectedFoundSignal is not null)
            {
                _selectedFoundSignal = null;
                OnPropertyChanged(nameof(SelectedFoundSignal));
            }
            foreach (var container in FoundContainers)
                container.IsRelatedToSelection = false;
            foreach (var signal in FoundSignals)
                signal.IsRelatedToSelection = string.Equals(
                    signal.ContainerId,
                    value.Id,
                    StringComparison.Ordinal);
            SignalRevealTarget = FoundSignalsView.Cast<Fee2ContainerFoundSignalVM>()
                .FirstOrDefault(signal => string.Equals(signal.ContainerId, value.Id, StringComparison.Ordinal));
        }
    }

    public Fee2ContainerFoundSignalVM? SelectedFoundSignal
    {
        get => _selectedFoundSignal;
        set
        {
            if (ReferenceEquals(_selectedFoundSignal, value))
                return;
            _selectedFoundSignal = value;
            OnPropertyChanged();
            if (value is null)
                return;

            if (_selectedFoundContainer is not null)
            {
                _selectedFoundContainer = null;
                OnPropertyChanged(nameof(SelectedFoundContainer));
            }
            foreach (var signal in FoundSignals)
                signal.IsRelatedToSelection = false;
            foreach (var container in FoundContainers)
                container.IsRelatedToSelection = string.Equals(
                    container.Id,
                    value.ContainerId,
                    StringComparison.Ordinal);
            ContainerRevealTarget = FoundContainersView.Cast<Fee2ContainerFoundContainerVM>()
                .FirstOrDefault(container => string.Equals(container.Id, value.ContainerId, StringComparison.Ordinal));
        }
    }

    public Fee2ContainerRootSelectionVM? SelectedRoot
    {
        get => _selectedRoot;
        set
        {
            if (ReferenceEquals(_selectedRoot, value)) return;
            _selectedRoot = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectionSummary));
            RefreshSelectionDetails();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanRefresh));
            OnPropertyChanged(nameof(CanExport));
            OnPropertyChanged(nameof(RefreshUnavailableReason));
            OnPropertyChanged(nameof(ExportUnavailableReason));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public int ProgressValue
    {
        get => _progressValue;
        private set { _progressValue = Math.Clamp(value, 0, 100); OnPropertyChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    public bool CanRefresh => !IsBusy && Connection.CanUseFeeFeatures;
    public bool CanExport => !IsBusy && Roots.Any(root => root.IsSelected);
    public string RefreshUnavailableReason => Connection.CanUseFeeFeatures
        ? (IsBusy ? "Ein FEE2Container-Vorgang läuft bereits." : string.Empty)
        : Connection.UnavailableReason;
    public string ExportUnavailableReason => !Roots.Any(root => root.IsSelected)
        ? "Mindestens einen BasicFrame über die Checkbox auswählen."
        : IsBusy ? "Ein FEE2Container-Vorgang läuft bereits." : string.Empty;

    public string SelectionSummary => SelectedRoot is null
        ? "Kein Root für die Detailansicht ausgewählt."
        : $"{SelectedRoot.Root.SourceKind}; {SelectedRoot.Root.ContainerCount} Container, " +
          $"{SelectedRoot.Root.SignalCount} Signale; " +
          (SelectedRoot.Root.HasProvenance
              ? $"{SelectedRoot.Root.UpdatedSignalCount} aktuell, {SelectedRoot.Root.MissingSignalCount} fehlend; " +
                $"{SelectedRoot.Root.UpdatedSlotCount} Slotrouten, {SelectedRoot.Root.UnresolvedSlotCount} ungeklärt; " +
                $"Quellfingerprint {Shorten(SelectedRoot.Root.Provenance?.SourceFingerprint)}"
              : $"{SelectedRoot.Root.InspectedObjectCount} Objekte geprüft, " +
                $"{SelectedRoot.Root.IgnoredObjectCount} nicht containerrelevant, " +
                $"{SelectedRoot.Root.ReconstructionIssues?.Count ?? 0} Prüfhinweis(e)");

    private async Task RefreshAsync()
    {
        if (!CanRefresh) { StatusText = RefreshUnavailableReason; return; }
        BeginOperation();
        try
        {
            var progress = new Progress<Fee2ContainerProgress>(update =>
            {
                ProgressValue = update.Percent;
                StatusText = update.Message;
            });
            var result = await _service.DiscoverAsync(
                _operationCancellation!.Token,
                reconstructLegacyRoots: true,
                progress);
            Roots.Clear();
            foreach (var root in result.Roots)
            {
                var selection = new Fee2ContainerRootSelectionVM(root);
                selection.PropertyChanged += OnRootSelectionChanged;
                Roots.Add(selection);
            }
            Issues.Clear();
            foreach (var issue in result.Issues)
                Issues.Add($"{issue.RootName}: {issue.Message}".TrimStart(':', ' '));
            SelectedRoot = Roots.FirstOrDefault();
            if (SelectedRoot is not null) SelectedRoot.IsSelected = true;
            StatusText = result.Roots.Count == 0
                ? "Keine obersten BasicFrames im geöffneten FEE-Projekt gefunden."
                : $"{result.Roots.Count} oberste BasicFrame(s) gefunden; " +
                  $"{result.IgnoredWithoutProvenance} ohne Provenienz live rekonstruiert; " +
                  $"{result.Issues.Count} Hinweis(e).";
            ApplicationLogService.Instance.Information(LogArea, StatusText);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Einlesen der FEE-Roots wurde abgebrochen.";
            ApplicationLogService.Instance.Information(LogArea, StatusText);
        }
        catch (Exception exception)
        {
            StatusText = $"FEE-Roots konnten nicht gelesen werden: {exception.Message}";
            ApplicationLogService.Instance.Error(LogArea, StatusText, exception);
        }
        finally { EndOperation(); }
    }

    private async Task ExportAsync()
    {
        var selectedRoots = Roots.Where(root => root.IsSelected).Select(root => root.CreateEditedRoot()).ToArray();
        if (!CanExport || selectedRoots.Length == 0) { StatusText = ExportUnavailableReason; return; }
        var sourceName = selectedRoots.Length == 1 ? selectedRoots[0].Name : $"{selectedRoots.Length}-FEE-Roots";
        var dialog = new SaveFileDialog
        {
            Title = "ContainerFile aus ausgewählten FEE-Hauptknoten exportieren",
            Filter = "Container XML (*.xml)|*.xml|Alle Dateien (*.*)|*.*",
            FileName = $"{ExportFileNamePolicy.Create(sourceName, "FEE2Container")}.container.xml",
            AddExtension = true,
            DefaultExt = ".xml",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true) return;

        BeginOperation();
        try
        {
            StatusText = "Ausgewählte FEE-Teilbäume, Logiken, Slots und Signale werden zusammengeführt …";
            var export = await _service.CreateCombinedExportAsync(selectedRoots, _operationCancellation!.Token);
            Issues.Clear();
            foreach (var issue in export.Issues)
                Issues.Add($"{(issue.ObjectGuid is Guid guid ? $"{guid:D}: " : string.Empty)}{issue.Message}");
            if (export.Snapshot.ContainerCount == 0)
            {
                StatusText = "In den ausgewählten Hauptknoten wurden keine sicher unterstützten Container erkannt. Es wurde keine Datei geschrieben.";
                ApplicationLogService.Instance.Warning(LogArea, StatusText);
                return;
            }
            _operationCancellation.Token.ThrowIfCancellationRequested();
            FeeContainerProvenanceCodec.SaveAtomically(export.Snapshot, dialog.FileName);
            StatusText = $"ContainerFile aus {selectedRoots.Length} Root(s) exportiert: " +
                         $"{export.Snapshot.ContainerCount} Container, {export.Snapshot.SignalCount} Signale, " +
                         $"{export.Issues.Count} Prüfhinweis(e). Datei: {dialog.FileName}";
            ApplicationLogService.Instance.Information(LogArea, StatusText);
        }
        catch (OperationCanceledException)
        {
            StatusText = "ContainerFile-Export wurde abgebrochen; es wurde keine Datei geschrieben.";
            ApplicationLogService.Instance.Information(LogArea, StatusText);
        }
        catch (Exception exception)
        {
            StatusText = $"ContainerFile konnte nicht exportiert werden: {exception.Message}";
            ApplicationLogService.Instance.Error(LogArea, StatusText, exception);
        }
        finally { EndOperation(); }
    }

    private void BeginOperation()
    {
        _operationCancellation = new CancellationTokenSource();
        ProgressValue = 0;
        IsBusy = true;
    }

    private void EndOperation()
    {
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        IsBusy = false;
    }

    private void Cancel()
    {
        _operationCancellation?.Cancel();
        StatusText = "Abbruch wurde angefordert; der laufende FEE-Batch wird noch sauber beendet …";
    }

    private void OnRootSelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(Fee2ContainerRootSelectionVM.IsSelected)) return;
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(ExportUnavailableReason));
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not nameof(FeeConnectionService.CanUseFeeFeatures) and
            not nameof(FeeConnectionService.UnavailableReason)) return;
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(RefreshUnavailableReason));
        CommandManager.InvalidateRequerySuggested();
    }

    private static string Shorten(string? value) => string.IsNullOrWhiteSpace(value)
        ? "nicht vorhanden" : value[..Math.Min(12, value.Length)];

    private void RefreshSelectionDetails()
    {
        FoundContainers.Clear();
        FoundSignals.Clear();
        NonContainerObjects.Clear();
        SelectedFoundContainer = null;
        SelectedFoundSignal = null;
        ContainerRevealTarget = null;
        SignalRevealTarget = null;
        if (SelectedRoot?.Editor is not { } editor)
            return;
        foreach (var container in editor.Containers)
            FoundContainers.Add(container);
        foreach (var signal in editor.Signals)
            FoundSignals.Add(signal);
        foreach (var item in editor.NonContainerObjects)
            NonContainerObjects.Add(item);
        SelectedFoundContainer = FoundContainers.FirstOrDefault(item => item.IsIncluded);
    }

    private static bool Matches(string query, params string?[] values)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        return values.Any(value => value?.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) == true);
    }

    private void RemoveContainer(Fee2ContainerFoundContainerVM? container)
    {
        if (container is null)
            return;
        container.IsIncluded = false;
        StatusText = $"Container '{container.Component}' ist für den Export deaktiviert. Die Änderung kann über die Checkbox rückgängig gemacht werden.";
    }

    private void RemoveSignal(Fee2ContainerFoundSignalVM? signal)
    {
        if (signal is null)
            return;
        signal.IsIncluded = false;
        StatusText = $"Signal '{signal.Signal}' ist für den Export deaktiviert.";
    }

    private void AddObjectAsContainer(Fee2ContainerUnmappedObjectVM? item)
    {
        if (item is null || SelectedRoot?.Editor is not { } editor || !item.CanAdd)
            return;
        var container = editor.AddObjectAsContainer(item);
        if (!FoundContainers.Contains(container))
            FoundContainers.Add(container);
        item.AddedAsContainer = true;
        editor.NonContainerObjects.Remove(item);
        NonContainerObjects.Remove(item);
        SelectedFoundContainer = container;
        StatusText = $"FEE-Objekt '{item.Name}' wurde als prüfbarer Container '{item.TargetContainerType}' ergänzt. Slot- und Signalzuordnungen müssen manuell vervollständigt werden.";
    }
}

public sealed class Fee2ContainerRootSelectionVM : NotifyBase
{
    private bool _isSelected;
    public Fee2ContainerRootSelectionVM(Fee2ContainerRoot root)
    {
        Root = root;
        Editor = new Fee2ContainerRootEditor(root);
    }
    public Fee2ContainerRoot Root { get; }
    public Fee2ContainerRootEditor Editor { get; }
    public bool IsSelected { get => _isSelected; set => SetPropertyChange(ref _isSelected, value); }
    public Guid Guid => Root.Guid;
    public string Name => Root.Name;
    public string SourceKind => Root.SourceKind;
    public int ContainerCount => Root.ContainerCount;
    public int SignalCount => Root.SignalCount;
    public int UpdatedSignalCount => Root.UpdatedSignalCount;
    public int MissingSignalCount => Root.MissingSignalCount;
    public int UpdatedSlotCount => Root.UpdatedSlotCount;
    public int UnresolvedSlotCount => Root.UnresolvedSlotCount;
    public FeeContainerProvenanceSnapshot? Provenance => Root.Provenance;
    public Fee2ContainerRoot CreateEditedRoot() => Root with { Provenance = Editor.CreateSnapshot() };
}

public sealed class Fee2ContainerRootEditor
{
    private readonly Fee2ContainerRoot _root;

    public Fee2ContainerRootEditor(Fee2ContainerRoot root)
    {
        _root = root;
        var document = root.Provenance?.ContainerDocument;
        if (document is not null)
        {
            var bindings = root.Provenance!.SignalBindings
                .ToDictionary(item => (item.ContainerIndex, item.EntryIndex), item => item.VariableGuid);
            foreach (var (container, containerIndex) in document.Descendants("Container").Select((item, index) => (item, index)))
            {
                var id = container.Attribute("id")?.Value ?? $"container-{containerIndex}";
                var component = container.Element("Component")?.Value ?? string.Empty;
                var type = container.Element("Type")?.Value ?? string.Empty;
                var entries = container.Descendants("Entry").ToArray();
                Containers.Add(new Fee2ContainerFoundContainerVM(id, component, type, entries.Length));
                foreach (var (entry, entryIndex) in entries.Select((item, index) => (item, index)))
                {
                    bindings.TryGetValue((containerIndex, entryIndex), out var variableGuid);
                    Signals.Add(new Fee2ContainerFoundSignalVM(
                        id,
                        component,
                        type,
                        entry.Element("Signal")?.Value ?? string.Empty,
                        entry.Element("Slot")?.Value ?? string.Empty,
                        entry.Element("Address")?.Value ?? string.Empty,
                        entry.Element("DataType")?.Value ?? string.Empty,
                        entry.Element("ID")?.Value ?? string.Empty,
                        entry.Element("Note")?.Value ?? string.Empty,
                        variableGuid == Guid.Empty ? null : variableGuid));
                }
            }
        }
        foreach (var item in root.NonContainerObjects ?? [])
            NonContainerObjects.Add(new Fee2ContainerUnmappedObjectVM(item));
    }

    public ObservableCollection<Fee2ContainerFoundContainerVM> Containers { get; } = new();
    public ObservableCollection<Fee2ContainerFoundSignalVM> Signals { get; } = new();
    public ObservableCollection<Fee2ContainerUnmappedObjectVM> NonContainerObjects { get; } = new();

    public Fee2ContainerFoundContainerVM AddObjectAsContainer(Fee2ContainerUnmappedObjectVM item)
    {
        var id = $"manual:{item.Guid:D}";
        var existing = Containers.FirstOrDefault(container =>
            string.Equals(container.Id, id, StringComparison.Ordinal));
        if (existing is not null)
        {
            existing.IsIncluded = true;
            existing.Component = item.TargetComponent;
            existing.Type = item.TargetContainerType;
            return existing;
        }
        var added = new Fee2ContainerFoundContainerVM(
            id,
            item.TargetComponent,
            item.TargetContainerType,
            0);
        Containers.Add(added);
        return added;
    }

    public FeeContainerProvenanceSnapshot CreateSnapshot()
    {
        var containerElements = new List<XElement>();
        var bindings = new List<FeeContainerSignalBinding>();
        var signalCount = 0;
        foreach (var container in Containers.Where(item => item.IsIncluded))
        {
            var dataList = new XElement("DataList");
            var containerIndex = containerElements.Count;
            var includedSignals = Signals.Where(item => item.IsIncluded &&
                string.Equals(item.ContainerId, container.Id, StringComparison.Ordinal)).ToArray();
            foreach (var signal in includedSignals)
            {
                var entryIndex = dataList.Elements("Entry").Count();
                dataList.Add(new XElement("Entry",
                    new XElement("ID", signal.SignalId),
                    new XElement("Address", signal.Address),
                    new XElement("DataType", signal.DataType),
                    new XElement("Signal", signal.Signal),
                    new XElement("Slot", signal.Slot),
                    new XElement("Note", signal.Note)));
                if (signal.VariableGuid is Guid variableGuid)
                    bindings.Add(new FeeContainerSignalBinding(containerIndex, entryIndex, variableGuid));
                if (!string.IsNullOrWhiteSpace(signal.Signal))
                    signalCount++;
            }
            if (!dataList.Elements("Entry").Any())
            {
                dataList.Add(new XElement("Entry",
                    new XElement("ID", $"FEE-UNASSIGNED-{container.Id}"),
                    new XElement("Address", string.Empty),
                    new XElement("DataType", string.Empty),
                    new XElement("Signal", string.Empty),
                    new XElement("Slot", string.Empty),
                    new XElement("Note", "PRÜFEN: Manuell in FEE2Container ergänzt oder ohne aktive Signalzuordnung.")));
            }
            containerElements.Add(new XElement("Container",
                new XAttribute("id", container.Id),
                new XElement("Component", container.Component),
                new XElement("Type", container.Type),
                dataList));
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("CAAMergeResult",
                new XAttribute("version", "1.0.0.0"),
                new XAttribute("createdAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
                new XAttribute("autoCreateFile", string.Empty),
                new XAttribute("zuli", string.Empty),
                new XElement("ContainerList", containerElements)));
        return new FeeContainerProvenanceSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal),
            document,
            bindings,
            containerElements.Count,
            signalCount,
            _root.Provenance?.SourceFingerprint ?? string.Empty);
    }
}

public sealed class Fee2ContainerFoundContainerVM : NotifyBase
{
    private string _component;
    private string _type;
    private bool _isIncluded = true;
    private bool _isRelatedToSelection;

    public Fee2ContainerFoundContainerVM(string id, string component, string type, int originalSignalCount)
    {
        Id = id;
        _component = component;
        _type = type;
        OriginalSignalCount = originalSignalCount;
    }

    public string Id { get; }
    public string Component { get => _component; set => SetPropertyChange(ref _component, value); }
    public string Type { get => _type; set => SetPropertyChange(ref _type, value); }
    public int OriginalSignalCount { get; }
    public bool IsIncluded { get => _isIncluded; set => SetPropertyChange(ref _isIncluded, value); }
    public bool IsRelatedToSelection
    {
        get => _isRelatedToSelection;
        set => SetPropertyChange(ref _isRelatedToSelection, value);
    }
}

public sealed class Fee2ContainerFoundSignalVM : NotifyBase
{
    private string _container;
    private string _containerType;
    private string _signal;
    private string _slot;
    private string _address;
    private string _dataType;
    private string _signalId;
    private string _note;
    private bool _isIncluded = true;
    private bool _isRelatedToSelection;

    public Fee2ContainerFoundSignalVM(
        string containerId, string container, string containerType, string signal, string slot,
        string address, string dataType, string signalId, string note, Guid? variableGuid)
    {
        ContainerId = containerId;
        _container = container;
        _containerType = containerType;
        _signal = signal;
        _slot = slot;
        _address = address;
        _dataType = dataType;
        _signalId = signalId;
        _note = note;
        VariableGuid = variableGuid;
    }

    public string ContainerId { get; }
    public string Container { get => _container; set => SetPropertyChange(ref _container, value); }
    public string ContainerType { get => _containerType; set => SetPropertyChange(ref _containerType, value); }
    public string Signal { get => _signal; set { if (SetPropertyChange(ref _signal, value)) NotifyAssignment(); } }
    public string Slot { get => _slot; set { if (SetPropertyChange(ref _slot, value)) NotifyAssignment(); } }
    public string Address { get => _address; set => SetPropertyChange(ref _address, value); }
    public string DataType { get => _dataType; set => SetPropertyChange(ref _dataType, value); }
    public string SignalId { get => _signalId; set => SetPropertyChange(ref _signalId, value); }
    public string Note { get => _note; set => SetPropertyChange(ref _note, value); }
    public Guid? VariableGuid { get; }
    public bool IsIncluded { get => _isIncluded; set { if (SetPropertyChange(ref _isIncluded, value)) NotifyAssignment(); } }
    public bool IsRelatedToSelection
    {
        get => _isRelatedToSelection;
        set => SetPropertyChange(ref _isRelatedToSelection, value);
    }
    public bool IsAssigned => IsIncluded && !string.IsNullOrWhiteSpace(Signal) && !string.IsNullOrWhiteSpace(Slot);
    public string AssignmentState => IsAssigned ? "Zugeordnet" : IsIncluded ? "Zuordnung unvollständig" : "Vom Export ausgeschlossen";

    private void NotifyAssignment()
    {
        OnPropertyChanged(nameof(IsAssigned));
        OnPropertyChanged(nameof(AssignmentState));
    }
}

public sealed class Fee2ContainerUnmappedObjectVM : NotifyBase
{
    private string _targetContainerType;
    private string _targetComponent;
    private bool _addedAsContainer;

    public Fee2ContainerUnmappedObjectVM(FeeContainerUnmappedObject model)
    {
        Model = model;
        _targetContainerType = FeeContainerLiveReconstructor.SupportedContainerTypes.FirstOrDefault() ?? string.Empty;
        _targetComponent = model.Name;
    }

    public FeeContainerUnmappedObject Model { get; }
    public Guid Guid => Model.Guid;
    public string Name => Model.Name;
    public string FeeType => Model.FeeType;
    public string Reason => Model.Reason;
    public string TargetContainerType
    {
        get => _targetContainerType;
        set { if (SetPropertyChange(ref _targetContainerType, value)) OnPropertyChanged(nameof(CanAdd)); }
    }
    public string TargetComponent
    {
        get => _targetComponent;
        set { if (SetPropertyChange(ref _targetComponent, value)) OnPropertyChanged(nameof(CanAdd)); }
    }
    public bool AddedAsContainer { get => _addedAsContainer; set => SetPropertyChange(ref _addedAsContainer, value); }
    public bool CanAdd => !string.IsNullOrWhiteSpace(TargetContainerType) && !string.IsNullOrWhiteSpace(TargetComponent);
}
