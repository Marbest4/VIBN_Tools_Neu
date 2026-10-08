using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Win32;
using VIBN_Tools.Application.Behaviors;
using VIBN_Tools.ContainerGeneration.Models;
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
    private long _selectionRevision;
    private long _crossSelectionRevision;

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
        AssignObjectToContainerCommand = new RelayCommand<ContainerToFeeVisualDropRequest>(
            AssignObjectToContainer,
            request => request?.Target is Fee2ContainerFoundContainerVM &&
                GetDraggedObjects(request.Source).Count > 0 && !IsBusy);
        RemoveObjectAssociationCommand = new RelayCommand<FeeContainerObjectAssociation>(
            RemoveObjectAssociation,
            association => association is not null && !IsBusy);
        EditContainersCommand = new RelayCommand(EditContainers, () => SelectedRoot is not null && !IsBusy);
        ConfigureDetailViews();
        _connection.PropertyChanged += OnConnectionPropertyChanged;
    }

    private void ConfigureDetailViews()
    {
        FoundContainersView = CollectionViewSource.GetDefaultView(FoundContainers);
        FoundSignalsView = CollectionViewSource.GetDefaultView(FoundSignals);
        NonContainerObjectsView = CollectionViewSource.GetDefaultView(NonContainerObjects);
        FoundContainersView.Filter = item => item is Fee2ContainerFoundContainerVM container &&
            Matches(ContainerSearchText, container.Component, container.Type, container.AssociatedObjects,
                container.OriginalSignalCount.ToString());
        FoundSignalsView.Filter = item => item is Fee2ContainerFoundSignalVM signal &&
            Matches(SignalSearchText, signal.Container, signal.ContainerType, signal.Signal, signal.Slot,
                signal.Address, signal.DataType, signal.SignalId, signal.AssignmentState, signal.Note);
        NonContainerObjectsView.Filter = item => item is Fee2ContainerUnmappedObjectVM feeObject &&
            Matches(ObjectSearchText, feeObject.Name, feeObject.FeeType, feeObject.Reason,
                feeObject.TargetComponent, feeObject.TargetContainerType);
    }

    public ObservableCollection<Fee2ContainerRootSelectionVM> Roots { get; } = new RangeObservableCollection<Fee2ContainerRootSelectionVM>();
    public ObservableCollection<string> Issues { get; } = new RangeObservableCollection<string>();
    public ObservableCollection<Fee2ContainerFoundContainerVM> FoundContainers { get; private set; } = new RangeObservableCollection<Fee2ContainerFoundContainerVM>();
    public ObservableCollection<Fee2ContainerFoundSignalVM> FoundSignals { get; private set; } = new RangeObservableCollection<Fee2ContainerFoundSignalVM>();
    public ObservableCollection<Fee2ContainerUnmappedObjectVM> NonContainerObjects { get; private set; } = new RangeObservableCollection<Fee2ContainerUnmappedObjectVM>();
    public ICollectionView FoundContainersView { get; private set; } = null!;
    public ICollectionView FoundSignalsView { get; private set; } = null!;
    public ICollectionView NonContainerObjectsView { get; private set; } = null!;
    public ICommand RefreshCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RemoveContainerCommand { get; }
    public ICommand RemoveSignalCommand { get; }
    public ICommand AddObjectAsContainerCommand { get; }
    public ICommand AssignObjectToContainerCommand { get; }
    public ICommand RemoveObjectAssociationCommand { get; }
    public ICommand EditContainersCommand { get; }
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
            QueueCrossListSelection(value, revealTarget: true);
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
            QueueCrossListSelection(value, revealTarget: true);
        }
    }

    private void QueueCrossListSelection(object selection, bool revealTarget)
    {
        var revision = ++_crossSelectionRevision;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            ApplyCrossListSelectionSafely(selection, revealTarget, revision);
            return;
        }
        _ = dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
            ApplyCrossListSelectionSafely(selection, revealTarget, revision)));
    }

    private void ApplyCrossListSelectionSafely(object selection, bool revealTarget, long revision)
    {
        if (revision != _crossSelectionRevision)
            return;
        try
        {
            if (selection is Fee2ContainerFoundContainerVM container)
            {
                if (!ReferenceEquals(container, _selectedFoundContainer))
                    return;
                if (_selectedFoundSignal is not null)
                {
                    _selectedFoundSignal = null;
                    OnPropertyChanged(nameof(SelectedFoundSignal));
                }
                foreach (var item in FoundContainers)
                    item.IsRelatedToSelection = false;
                foreach (var signal in FoundSignals)
                    signal.IsRelatedToSelection = string.Equals(signal.ContainerId, container.Id, StringComparison.Ordinal);
                if (revealTarget)
                {
                    var target = FoundSignals
                        .FirstOrDefault(signal => string.Equals(signal.ContainerId, container.Id, StringComparison.Ordinal));
                    EnsureSignalVisible(target);
                    RequestSignalReveal(target);
                }
            }
            else if (selection is Fee2ContainerFoundSignalVM signal)
            {
                if (!ReferenceEquals(signal, _selectedFoundSignal))
                    return;
                if (_selectedFoundContainer is not null)
                {
                    _selectedFoundContainer = null;
                    OnPropertyChanged(nameof(SelectedFoundContainer));
                }
                foreach (var item in FoundSignals)
                    item.IsRelatedToSelection = false;
                foreach (var relatedContainer in FoundContainers)
                    relatedContainer.IsRelatedToSelection = string.Equals(relatedContainer.Id, signal.ContainerId, StringComparison.Ordinal);
                if (revealTarget)
                {
                    var target = FoundContainers
                        .FirstOrDefault(relatedContainer => string.Equals(relatedContainer.Id, signal.ContainerId, StringComparison.Ordinal));
                    EnsureContainerVisible(target);
                    RequestContainerReveal(target);
                }
            }
        }
        catch (ArgumentOutOfRangeException exception)
        {
            StatusText = "Eine veraltete Tabellenposition wurde verworfen; die Auswahl kann ohne Neustart wiederholt werden.";
            ApplicationLogService.Instance.Warning(LogArea, StatusText, exception.ToString());
        }
        catch (InvalidOperationException exception)
        {
            StatusText = "Die Tabellenansicht wurde während der Auswahl aktualisiert; bitte die Zeile erneut wählen.";
            ApplicationLogService.Instance.Warning(LogArea, StatusText, exception.ToString());
        }
    }

    private void EnsureContainerVisible(Fee2ContainerFoundContainerVM? target)
    {
        if (target is null || FoundContainersView.Contains(target))
            return;
        _containerSearchText = string.Empty;
        OnPropertyChanged(nameof(ContainerSearchText));
        FoundContainersView.Refresh();
    }

    private void EnsureSignalVisible(Fee2ContainerFoundSignalVM? target)
    {
        if (target is null || FoundSignalsView.Contains(target))
            return;
        _signalSearchText = string.Empty;
        OnPropertyChanged(nameof(SignalSearchText));
        FoundSignalsView.Refresh();
    }

    private void RequestContainerReveal(Fee2ContainerFoundContainerVM? target)
    {
        if (ReferenceEquals(_containerRevealTarget, target) && target is not null)
        {
            _containerRevealTarget = null;
            OnPropertyChanged(nameof(ContainerRevealTarget));
        }
        ContainerRevealTarget = target;
    }

    private void RequestSignalReveal(Fee2ContainerFoundSignalVM? target)
    {
        if (ReferenceEquals(_signalRevealTarget, target) && target is not null)
        {
            _signalRevealTarget = null;
            OnPropertyChanged(nameof(SignalRevealTarget));
        }
        SignalRevealTarget = target;
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
            QueueSelectionDetails(value);
            CommandManager.InvalidateRequerySuggested();
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

    public bool CanRefresh => !IsBusy && Connection.CanUseFeeFeatures && Connection.AreModelValidationObjectsCurrent;
    public bool CanExport => !IsBusy && Roots.Any(root => root.IsSelected);
    public string RefreshUnavailableReason => Connection.CanUseFeeFeatures
        ? (IsBusy ? "Ein FEE2Container-Vorgang läuft bereits." : Connection.ModelValidationUnavailableReason ?? string.Empty)
        : Connection.UnavailableReason;
    public string ExportUnavailableReason => !Roots.Any(root => root.IsSelected)
        ? "Mindestens einen BasicFrame über die Checkbox auswählen."
        : IsBusy ? "Ein FEE2Container-Vorgang läuft bereits." : string.Empty;

    public string SelectionSummary => SelectedRoot is null
        ? "Kein Root für die Detailansicht ausgewählt."
        : $"{SelectedRoot.Root.SourceKind}; {SelectedRoot.ContainerCount} Container, " +
          $"{SelectedRoot.SignalCount} Signale; " +
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
            SelectedRoot = null;
            foreach (var existingRoot in Roots)
                existingRoot.PropertyChanged -= OnRootSelectionChanged;
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
            not nameof(FeeConnectionService.AreModelValidationObjectsCurrent) and
            not nameof(FeeConnectionService.ModelValidationUnavailableReason) and
            not nameof(FeeConnectionService.UnavailableReason)) return;
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(RefreshUnavailableReason));
        CommandManager.InvalidateRequerySuggested();
    }

    private static string Shorten(string? value) => string.IsNullOrWhiteSpace(value)
        ? "nicht vorhanden" : value[..Math.Min(12, value.Length)];

    private void QueueSelectionDetails(Fee2ContainerRootSelectionVM? selection)
    {
        var revision = ++_selectionRevision;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            RefreshSelectionDetailsSafely(selection, revision);
            return;
        }

        _ = dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
            RefreshSelectionDetailsSafely(selection, revision)));
    }

    private void RefreshSelectionDetailsSafely(
        Fee2ContainerRootSelectionVM? selection,
        long revision)
    {
        if (revision != _selectionRevision || !ReferenceEquals(selection, SelectedRoot))
            return;

        try
        {
            RefreshSelectionDetails(selection);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            // WPF can still surface a stale virtualized row index synchronously
            // through CollectionChanged. Never let that framework race close the
            // application; a later/root selection can rebuild the detail lists.
            StatusText = "Die Detailansicht dieses FEE-Roots konnte wegen eines veralteten Tabellenindex nicht aktualisiert werden. Bitte den Root erneut auswählen.";
            ApplicationLogService.Instance.Warning(LogArea, StatusText, exception.ToString());
        }
        catch (InvalidOperationException exception)
        {
            StatusText = "Die FEE2Container-Detailansicht wurde während des Root-Wechsels erneuert. Bitte den Root erneut auswählen.";
            ApplicationLogService.Instance.Warning(LogArea, StatusText, exception.ToString());
        }
    }

    private void RefreshSelectionDetails(Fee2ContainerRootSelectionVM? selection)
    {
        _crossSelectionRevision++;
        _selectedFoundContainer = null;
        _selectedFoundSignal = null;
        OnPropertyChanged(nameof(SelectedFoundContainer));
        OnPropertyChanged(nameof(SelectedFoundSignal));
        ContainerRevealTarget = null;
        SignalRevealTarget = null;
        var editor = selection?.Editor;
        // Keep the old view and its backing data intact while WPF finishes
        // queued row-generation requests. Mutating a bound collection here can
        // invalidate a pending index in DataGrid/VirtualizingStackPanel.
        FoundContainers = new ObservableCollection<Fee2ContainerFoundContainerVM>(editor?.Containers ?? []);
        FoundSignals = new ObservableCollection<Fee2ContainerFoundSignalVM>(editor?.Signals ?? []);
        NonContainerObjects = new ObservableCollection<Fee2ContainerUnmappedObjectVM>(editor?.NonContainerObjects ?? []);
        ConfigureDetailViews();
        OnPropertyChanged(nameof(FoundContainers));
        OnPropertyChanged(nameof(FoundSignals));
        OnPropertyChanged(nameof(NonContainerObjects));
        OnPropertyChanged(nameof(FoundContainersView));
        OnPropertyChanged(nameof(FoundSignalsView));
        OnPropertyChanged(nameof(NonContainerObjectsView));
    }

    private static bool Matches(string query, params string?[] values)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        return values.Any(value => value?.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) == true);
    }

    private void EditContainers()
    {
        if (SelectedRoot is not { } root || IsBusy) return;
        var viewModel = new Fee2ContainerEditVM(root);
        var window = new VIBN_Tools.Application.View.Fee2ContainerEditWindow(viewModel);
        if (System.Windows.Application.Current?.MainWindow is { } owner) window.Owner = owner;
        if (window.ShowDialog() != true) return;
        root.ApplyEditor(viewModel.CreateResult());
        RefreshSelectionDetails(root);
        StatusText = $"Container und Grouping für '{root.Name}' übernommen. Vor Export die FEE-Objektzuordnungen und Export-Checkboxen prüfen.";
        OnPropertyChanged(nameof(SelectionSummary));
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

    private void AssignObjectToContainer(ContainerToFeeVisualDropRequest? request)
    {
        if (request?.Target is not Fee2ContainerFoundContainerVM container ||
            SelectedRoot?.Editor is not { } editor)
        {
            return;
        }

        var items = GetDraggedObjects(request.Source);
        if (items.Count == 0 || !editor.Containers.Contains(container))
            return;
        var availableGuids = editor.NonContainerObjects.Select(item => item.Guid)
            .Concat(editor.CreateObjectAssociations().Select(item => item.ObjectGuid)).ToHashSet();
        if (items.Any(item => !availableGuids.Contains(item.Guid)))
        {
            StatusText = "Die Auswahl gehört nicht mehr zum aktiven FEE-Root. Bitte erneut auswählen.";
            return;
        }
        foreach (var item in items)
        {
            editor.AssignObjectToContainer(item, container);
            var existing = editor.NonContainerObjects.FirstOrDefault(value => value.Guid == item.Guid);
            if (existing is not null)
            {
                editor.NonContainerObjects.Remove(existing);
                NonContainerObjects.Remove(existing);
            }
        }
        SelectedFoundContainer = container;
        StatusText = $"{items.Count} FEE-Objekt(e) wurden dem Container '{container.Component}' zugeordnet. " +
                     "Die Zuordnung wird im bearbeiteten FEE2Container-Arbeitsstand mitgeführt.";
    }

    private static IReadOnlyList<Fee2ContainerUnmappedObjectVM> GetDraggedObjects(object source)
    {
        object[] sources = source is object[] batch ? batch : [source];
        if (sources.Any(item => item is not (Fee2ContainerUnmappedObjectVM or FeeContainerObjectAssociation)))
            return [];
        return sources.Select(item => item is Fee2ContainerUnmappedObjectVM unmapped
                ? unmapped
                : new Fee2ContainerUnmappedObjectVM(new FeeContainerUnmappedObject(
                    ((FeeContainerObjectAssociation)item).ObjectGuid,
                    ((FeeContainerObjectAssociation)item).ObjectName,
                    ((FeeContainerObjectAssociation)item).ObjectType,
                    "Manuelle Zuordnung")))
            .DistinctBy(item => item.Guid).ToArray();
    }

    private void RemoveObjectAssociation(FeeContainerObjectAssociation? association)
    {
        if (association is null || SelectedRoot?.Editor is not { } editor)
            return;
        var restored = editor.RemoveObjectAssociation(association);
        if (restored is null)
            return;
        NonContainerObjects.Add(restored);
        StatusText = $"Zuordnung von '{association.ObjectName}' entfernt. Das FEE-Objekt steht wieder für die Zuordnung bereit.";
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
    public Fee2ContainerRootEditor Editor { get; private set; }
    public void ApplyEditor(Fee2ContainerRootEditor editor)
    {
        Editor = editor;
        OnPropertyChanged(nameof(Editor));
        OnPropertyChanged(nameof(ContainerCount));
        OnPropertyChanged(nameof(SignalCount));
    }
    public bool IsSelected { get => _isSelected; set => SetPropertyChange(ref _isSelected, value); }
    public Guid Guid => Root.Guid;
    public string Name => Root.Name;
    public string SourceKind => Root.SourceKind;
    public int ContainerCount => Editor.Containers.Count(item => item.IsIncluded);
    public int SignalCount => Editor.Signals.Count(item => item.IsIncluded && !string.IsNullOrWhiteSpace(item.Signal) && Editor.Containers.Any(container => container.Id == item.ContainerId && container.IsIncluded));
    public int UpdatedSignalCount => Root.UpdatedSignalCount;
    public int MissingSignalCount => Root.MissingSignalCount;
    public int UpdatedSlotCount => Root.UpdatedSlotCount;
    public int UnresolvedSlotCount => Root.UnresolvedSlotCount;
    public FeeContainerProvenanceSnapshot? Provenance => Root.Provenance;
    public Fee2ContainerRoot CreateEditedRoot() => Root with
    {
        Provenance = Editor.CreateSnapshot(),
        NonContainerObjects = Editor.NonContainerObjects.Select(item => item.Model).ToArray(),
        ObjectAssociations = Editor.CreateObjectAssociations(),
    };
}

public sealed class Fee2ContainerRootEditor
{
    private readonly Fee2ContainerRoot _root;
    private readonly Dictionary<Guid, XElement> _sourceObjects = [];

    public static Fee2ContainerRootEditor FromDocument(Fee2ContainerRoot source, XDocument document)
    {
        var bindings = new List<FeeContainerSignalBinding>();
        var containers = document.Descendants("Container").ToArray();
        foreach (var (container, containerIndex) in containers.Select((item, index) => (item, index)))
            foreach (var (entry, entryIndex) in (container.Element("DataList")?.Elements("Entry") ?? []).Select((item, index) => (item, index)))
                if (Guid.TryParse(entry.Attribute("feeGuid")?.Value, out var guid) && guid != Guid.Empty)
                    bindings.Add(new FeeContainerSignalBinding(containerIndex, entryIndex, guid));
        var snapshot = new FeeContainerProvenanceSnapshot(new Dictionary<string, string>(), new XDocument(document), bindings,
            containers.Length, containers.Sum(item => item.Descendants("Entry").Count(entry => !string.IsNullOrWhiteSpace(entry.Element("Signal")?.Value))),
            source.Provenance?.SourceFingerprint ?? "");
        var retainedGuids = document.Descendants("Container").SelectMany(VIBN_Tools.ContainerGeneration.Models.ContainerFileXml.Objects)
            .Select(item => Guid.TryParse(item.Element("Guid")?.Value, out var guid) ? guid : Guid.Empty).ToHashSet();
        var unmapped = (source.NonContainerObjects ?? []).Concat((source.ObjectAssociations ?? [])
            .Where(item => !retainedGuids.Contains(item.ObjectGuid))
            .Select(item => new FeeContainerUnmappedObject(item.ObjectGuid, item.ObjectName, item.ObjectType, "Zuordnung im Editor entfernt")))
            .DistinctBy(item => item.Guid).ToArray();
        return new Fee2ContainerRootEditor(source with { Provenance = snapshot, ObjectAssociations = null, NonContainerObjects = unmapped });
    }

    public Fee2ContainerRootEditor(Fee2ContainerRoot root)
    {
        _root = root;
        // Work on a copy: normalizing legacy IDs must not alter saved root
        // provenance or another edit session.
        var document = root.Provenance is { } provenance ? new XDocument(provenance.ContainerDocument) : null;
        var sourceContainers = document?.Descendants("Container").ToArray() ?? [];
        var sourceIds = sourceContainers.ToDictionary(item => item, item => item.Attribute("id")?.Value ?? "");
        var containerRows = new Dictionary<XElement, Fee2ContainerFoundContainerVM>();
        if (document is not null) ContainerFileXml.EnsureUniqueContainerIds(document);
        foreach (var element in document?.Descendants("SimObject") ?? [])
            if (Guid.TryParse(element.Element("Guid")?.Value, out var guid)) _sourceObjects.TryAdd(guid, new XElement(element));
        if (document is not null)
        {
            var bindings = root.Provenance!.SignalBindings
                .ToDictionary(item => (item.ContainerIndex, item.EntryIndex), item => item.VariableGuid);
            foreach (var (container, containerIndex) in sourceContainers.Select((item, index) => (item, index)))
            {
                var id = container.Attribute("id")!.Value;
                var component = container.Element("Component")?.Value ?? string.Empty;
                var type = CanonicalizeContainerType(container.Element("Type")?.Value);
                var entries = container.Descendants("Entry").ToArray();
                var row = new Fee2ContainerFoundContainerVM(
                    id,
                    component,
                    type,
                    entries.Length) { IsIncluded = container.Attribute("export")?.Value != "false" };
                Containers.Add(row);
                containerRows[container] = row;
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
                        variableGuid == Guid.Empty ? null : variableGuid) { IsIncluded = entry.Attribute("export")?.Value != "false" });
                }
            }
        }
        foreach (var item in root.NonContainerObjects ?? [])
            NonContainerObjects.Add(new Fee2ContainerUnmappedObjectVM(item));
        var proposals = new List<FeeContainerObjectAssociation>();
        foreach (var association in root.ObjectAssociations ?? [])
        {
            if (association.ObjectGuid == Guid.Empty) continue;
            var matches = sourceContainers.Where(item =>
                (sourceIds[item] == association.ContainerId || containerRows[item].Id == association.ContainerId) &&
                (association.IsManual || ContainerFileXml.HasMatchingObjectName(association.ObjectName, containerRows[item].Component))).ToArray();
            if (matches.Length > 1)
                matches = matches.Where(container => ContainerFileXml.Objects(container)
                    .Any(item => Guid.TryParse(item.Element("Guid")?.Value, out var guid) && guid == association.ObjectGuid)).ToArray();
            if (matches.Length == 1)
                proposals.Add(association with { ContainerId = containerRows[matches[0]].Id });
            else RestoreUnmatched(association.ObjectGuid, association.ObjectName, association.ObjectType);
        }
        foreach (var container in sourceContainers)
            foreach (var item in ContainerFileXml.Objects(container))
            {
                if (!Guid.TryParse(item.Element("Guid")?.Value, out var guid) || guid == Guid.Empty) continue;
                var name = item.Element("Name")?.Value ?? ""; var type = item.Element("FeeType")?.Value ?? "";
                if (ContainerFileXml.CanRetainObjectAssociation(item, name, container.Element("Component")?.Value ?? ""))
                    proposals.Add(new FeeContainerObjectAssociation(guid, name, type, Guid.Empty, containerRows[container].Id,
                        "Gespeicherte Zuordnung aus ContainerFile", item.Element("Role")?.Value ?? "SimObject",
                        IsManual: item.Attribute("assignment")?.Value == "Manual"));
                else RestoreUnmatched(guid, name, type);
            }
        foreach (var group in proposals.GroupBy(item => item.ObjectGuid))
        {
            if (group.Select(item => item.ContainerId).Distinct(StringComparer.Ordinal).Count() == 1)
                ObjectAssociations.Add(group.FirstOrDefault(item => item.IsManual) ?? group.First());
            else
                RestoreUnmatched(group.Key, group.First().ObjectName, group.First().ObjectType);
        }
        void RestoreUnmatched(Guid guid, string name, string type)
        {
            if (!NonContainerObjects.Any(item => item.Guid == guid))
                NonContainerObjects.Add(new Fee2ContainerUnmappedObjectVM(new FeeContainerUnmappedObject(guid, name, type,
                    "Automatische Zuordnung verworfen: Name oder Containerbezug ist nicht eindeutig; manuelle Zuordnung möglich")));
        }
        var assignedGuids = ObjectAssociations.Select(item => item.ObjectGuid).ToHashSet();
        foreach (var item in NonContainerObjects.Where(item => assignedGuids.Contains(item.Guid)).ToArray()) NonContainerObjects.Remove(item);
        RefreshObjectAssociations();
        foreach (var container in Containers)
            container.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is not (nameof(Fee2ContainerFoundContainerVM.Component) or nameof(Fee2ContainerFoundContainerVM.Type))) return;
                foreach (var signal in Signals.Where(item => item.ContainerId == container.Id))
                { signal.Container = container.Component; signal.ContainerType = container.Type; }
                for (var index = 0; index < ObjectAssociations.Count; index++)
                {
                    var association = ObjectAssociations[index];
                    if (association.ContainerId == container.Id && !ContainerFileXml.HasMatchingObjectName(association.ObjectName, container.Component))
                        ObjectAssociations[index] = association with { IsManual = true, Reason = "Manuelle Änderung des Containernamens" };
                }
                RefreshObjectAssociations();
            };
    }

    public ObservableCollection<Fee2ContainerFoundContainerVM> Containers { get; } = new();
    public ObservableCollection<Fee2ContainerFoundSignalVM> Signals { get; } = new();
    public ObservableCollection<Fee2ContainerUnmappedObjectVM> NonContainerObjects { get; } = new();
    private List<FeeContainerObjectAssociation> ObjectAssociations { get; } = [];

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
            AssignObjectToContainer(item, existing);
            return existing;
        }
        var added = new Fee2ContainerFoundContainerVM(
            id,
            item.TargetComponent,
            item.TargetContainerType,
            0);
        Containers.Add(added);
        AssignObjectToContainer(item, added);
        return added;
    }

    public void AssignObjectToContainer(
        Fee2ContainerUnmappedObjectVM item,
        Fee2ContainerFoundContainerVM container)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(container);
        ObjectAssociations.RemoveAll(association => association.ObjectGuid == item.Guid);
        ObjectAssociations.Add(new FeeContainerObjectAssociation(
            item.Guid,
            item.Name,
            item.FeeType,
            Guid.TryParse(container.Id.Replace("fee:", string.Empty, StringComparison.OrdinalIgnoreCase), out var containerGuid)
                ? containerGuid
                : Guid.Empty,
            container.Id,
            "Manuell per Drag-and-drop in FEE2Container zugeordnet", IsManual: true));
        RefreshObjectAssociations();
    }

    public static bool HasMatchingName(Fee2ContainerUnmappedObjectVM item, Fee2ContainerFoundContainerVM container) =>
        ContainerFileXml.HasMatchingObjectName(item.Name, container.Component);

    private void RefreshObjectAssociations()
    {
        foreach (var container in Containers)
            container.SetAssociatedObjects(ObjectAssociations.Where(item => item.ContainerId == container.Id));
    }

    public Fee2ContainerUnmappedObjectVM? RemoveObjectAssociation(FeeContainerObjectAssociation association)
    {
        if (!ObjectAssociations.Remove(association))
            return null;
        RefreshObjectAssociations();
        var restored = new Fee2ContainerUnmappedObjectVM(new FeeContainerUnmappedObject(
            association.ObjectGuid, association.ObjectName, association.ObjectType,
            "Containerzuordnung wurde manuell entfernt"));
        NonContainerObjects.Add(restored);
        return restored;
    }

    public IReadOnlyList<FeeContainerObjectAssociation> CreateObjectAssociations() =>
        ObjectAssociations.ToArray();

    private XElement CreateObjectElement(FeeContainerObjectAssociation association)
    {
        var element = _sourceObjects.TryGetValue(association.ObjectGuid, out var source)
            ? new XElement(source) : VIBN_Tools.ContainerGeneration.Models.ContainerFileXml.Object(
                association.ObjectGuid.ToString("D"), association.ObjectName, association.ObjectType, association.Role);
        element.SetElementValue("Name", association.ObjectName);
        element.SetElementValue("FeeType", association.ObjectType);
        element.SetElementValue("Role", association.Role);
        element.SetAttributeValue("assignment", association.IsManual ? "Manual" : "Automatic");
        return element;
    }

    public FeeContainerProvenanceSnapshot CreateSnapshot(bool includeExcluded = false)
    {
        var containerElements = new List<XElement>();
        var bindings = new List<FeeContainerSignalBinding>();
        var signalCount = 0;
        foreach (var container in Containers.Where(item => includeExcluded || item.IsIncluded))
        {
            var dataList = new XElement("DataList");
            var containerIndex = containerElements.Count;
            var includedSignals = Signals.Where(item => (includeExcluded || item.IsIncluded) &&
                string.Equals(item.ContainerId, container.Id, StringComparison.Ordinal)).ToArray();
            foreach (var signal in includedSignals)
            {
                var entryIndex = dataList.Elements("Entry").Count();
                dataList.Add(new XElement("Entry",
                    includeExcluded && !signal.IsIncluded ? new XAttribute("export", "false") : null,
                    new XElement("ID", signal.SignalId),
                    new XElement("Address", signal.Address),
                    new XElement("DataType", signal.DataType),
                    new XElement("Signal", signal.Signal),
                    new XElement("Slot", signal.Slot),
                    new XElement("Note", signal.Note),
                    signal.VariableGuid is Guid guid ? new XAttribute("feeGuid", guid.ToString("D")) : null));
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
                includeExcluded && !container.IsIncluded ? new XAttribute("export", "false") : null,
                new XElement("Component", container.Component),
                new XElement("Type", CanonicalizeContainerType(container.Type)),
                dataList,
                new XElement("SimObjects", ObjectAssociations.Where(item => item.ContainerId == container.Id)
                    .Select(item => CreateObjectElement(item)))));

        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("CAAMergeResult",
                new XAttribute("version", "1.0.0.0"),
                new XAttribute("createdAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
                new XAttribute("autoCreateFile", string.Empty),
                new XAttribute("zuli", string.Empty),
                new XElement("ContainerList", containerElements),
                _root.Provenance?.ContainerDocument.Root?.Element("FeeInventory") is { } inventory ? new XElement(inventory) : null));
        return new FeeContainerProvenanceSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal),
            document,
            bindings,
            containerElements.Count,
            signalCount,
            _root.Provenance?.SourceFingerprint ?? string.Empty);
    }

    private static string CanonicalizeContainerType(string? type)
    {
        var value = type?.Trim() ?? string.Empty;
        if (string.Equals(value, "Switch", StringComparison.OrdinalIgnoreCase))
            return "CabinetSwitch";
        if (string.Equals(value, "Fuse", StringComparison.OrdinalIgnoreCase))
            return "CabinetFuse";
        return value;
    }
}

public sealed class Fee2ContainerFoundContainerVM : NotifyBase
{
    private string _component;
    private string _type;
    private bool _isIncluded = true;
    private bool _isRelatedToSelection;
    private string _associatedObjects;

    public Fee2ContainerFoundContainerVM(
        string id,
        string component,
        string type,
        int originalSignalCount,
        string associatedObjects = "")
    {
        Id = id;
        _component = component;
        _type = type;
        OriginalSignalCount = originalSignalCount;
        _associatedObjects = associatedObjects;
    }

    public string Id { get; }
    public string Component { get => _component; set => SetPropertyChange(ref _component, value); }
    public string Type { get => _type; set => SetPropertyChange(ref _type, value); }
    public int OriginalSignalCount { get; }
    public string AssociatedObjects => _associatedObjects;
    public int AssociatedObjectCount => AssociatedObjectItems.Count;
    public ObservableCollection<FeeContainerObjectAssociation> AssociatedObjectItems { get; } = new();
    public bool IsIncluded { get => _isIncluded; set => SetPropertyChange(ref _isIncluded, value); }
    public bool IsRelatedToSelection
    {
        get => _isRelatedToSelection;
        set => SetPropertyChange(ref _isRelatedToSelection, value);
    }


    public void SetAssociatedObjects(IEnumerable<FeeContainerObjectAssociation> associations)
    {
        AssociatedObjectItems.ReplaceWith(associations.ToArray());
        _associatedObjects = string.Join(", ", AssociatedObjectItems.Select(item => $"{item.ObjectName} ({item.ObjectType})"));
        OnPropertyChanged(nameof(AssociatedObjects));
        OnPropertyChanged(nameof(AssociatedObjectCount));
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
