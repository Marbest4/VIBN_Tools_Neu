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

public sealed partial class Fee2ContainerPageVM : MvvmBase
{
    private const string LogArea = "FEE2Container";
    private readonly Fee2ContainerService _service;
    private readonly FeeConnectionService _connection;
    private Fee2ContainerRootSelectionVM? _selectedRoot;
    private CancellationTokenSource? _operationCancellation;
    private bool _isBusy;
    private bool _isIssuesExpanded;
    private int _progressValue;
    private string _statusText = "FEE verbinden, Update Objects ausführen und zuerst die Signal-Interfaces einlesen.";
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
        OpenRequirementsCommand = new AsyncRelayCommand(OpenRequirementsAsync, () => !IsBusy);
        RefreshRequirementDefinitions();
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
                (CanAssignSignals(request.Source) || GetDraggedObjects(request.Source) is { Count: > 0 } items && items.All(item => !item.IsSpecialDevice)) && !IsBusy);
        RemoveObjectAssociationCommand = new RelayCommand<FeeContainerObjectAssociation>(
            RemoveObjectAssociation,
            association => association is not null && !IsBusy);
        EditContainersCommand = new RelayCommand(EditContainers, () => SelectedRoot is not null && !IsBusy);
        InitializeLiveDetails();
        InitializeSignalWorkflow();
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
            ObjectTypeFilters.All(filter => filter.FeeType != feeObject.FeeType || filter.IsVisible) &&
            Matches(ObjectSearchText, feeObject.Name, feeObject.FeeType, feeObject.Reason,
                feeObject.TargetComponent, feeObject.TargetContainerType, feeObject.ConnectionText, feeObject.ParentName, feeObject.LogicDefinitionName);
        SpecialDevicesView = new ListCollectionView(FoundContainers);
        SpecialDevicesView.Filter = item => item is Fee2ContainerFoundContainerVM { IsSpecialDevice: true };
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
        _supportedContainerTypes;

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
            OnPropertyChanged(nameof(CanConfigureRequirements));
            OnPropertyChanged(nameof(CanExport));
            OnPropertyChanged(nameof(RefreshUnavailableReason));
            OnPropertyChanged(nameof(ExportUnavailableReason));
            NotifyWorkflow();
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

    public bool IsIssuesExpanded
    {
        get => _isIssuesExpanded;
        set { if (_isIssuesExpanded == value) return; _isIssuesExpanded = value; OnPropertyChanged(); }
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
        BeginOperation(); var operation = _operationCancellation!;
        try
        {
            ObjectRoots.Clear();
            _service.SelectedInterfaceGuids = Interfaces.Where(item => item.IsSelected).Select(item => item.Guid).ToHashSet(StringComparer.OrdinalIgnoreCase);
            RefreshRequirementDefinitions();
            var progress = new Progress<Fee2ContainerProgress>(item =>
            {
                if (IsBusy && ReferenceEquals(_operationCancellation, operation) && !operation.IsCancellationRequested)
                { ProgressValue = item.Percent; StatusText = item.Message; }
            });
            var result = await _service.DiscoverAsync(operation.Token, reconstructLegacyRoots: true, progress);
            foreach (var root in result.Roots)
            {
                var choice = new Fee2ContainerRootSelectionVM(root);
                choice.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(Fee2ContainerRootSelectionVM.IsSelected)) NotifyWorkflow(); };
                ObjectRoots.Add(choice);
            }
            Issues.ReplaceWith(result.Issues.Select(item => $"{item.RootName}: {item.Message}"));
            IsIssuesExpanded = Issues.Count > 0;
            StatusText = $"{ObjectRoots.Count} FEE-Roots gelesen. Gewünschte Roots markieren und FEE-Objekte separat zuordnen. Die Signalcontainer wurden nicht ersetzt.";
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { StatusText = "Root-Einlesen durch den Benutzer abgebrochen."; }
        catch (Exception exception) { ReportOperationFailure("FEE-Roots konnten nicht gelesen werden", exception); }
        finally { EndOperation(); NotifyWorkflow(); }
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
        var operationCancellation = _operationCancellation!;
        try
        {
            StatusText = "Ausgewählte FEE-Teilbäume, Logiken, Slots und Signale werden zusammengeführt …";
            var export = await _service.CreateCombinedExportAsync(selectedRoots, _operationCancellation!.Token);
            Issues.Clear();
            foreach (var issue in export.Issues)
                Issues.Add($"{(issue.ObjectGuid is Guid guid ? $"{guid:D}: " : string.Empty)}{issue.Message}");
            IsIssuesExpanded = Issues.Count > 0;
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
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            StatusText = "ContainerFile-Export wurde abgebrochen; es wurde keine Datei geschrieben.";
            ApplicationLogService.Instance.Information(LogArea, StatusText);
        }
        catch (Exception exception)
        {
            ReportOperationFailure("ContainerFile konnte nicht exportiert werden", exception);
        }
        finally { EndOperation(); }
    }

    private void ReportOperationFailure(string operation, Exception exception)
    {
        StatusText = $"{operation}: {Fee2ContainerReadPhase.Describe(exception)}";
        Issues.Add(StatusText);
        Issues.Add(exception.ToString());
        IsIssuesExpanded = true;
        ApplicationLogService.Instance.Error(LogArea, StatusText, exception);
    }

    private void ClearDiscoveredRoots()
    {
        _selectionRevision++;
        SelectedRoot = null;
        foreach (var root in Roots) root.PropertyChanged -= OnRootSelectionChanged;
        Roots.Clear();
        Issues.Clear();
        IsIssuesExpanded = false;
        RefreshSelectionDetails(null);
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
        NotifyWorkflow();
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
        var signals = new RangeObservableCollection<Fee2ContainerFoundSignalVM>(); signals.ReplaceRange(editor?.Signals ?? []);
        FoundSignals = signals;
        NonContainerObjects = new ObservableCollection<Fee2ContainerUnmappedObjectVM>(editor?.NonContainerObjects ?? []);
        foreach (var item in NonContainerObjects)
            if (!SupportedContainerTypes.Contains(item.TargetContainerType, StringComparer.OrdinalIgnoreCase))
                item.TargetContainerType = SupportedContainerTypes.FirstOrDefault() ?? "";
        ConfigureDetailViews();
        OnPropertyChanged(nameof(FoundContainers));
        OnPropertyChanged(nameof(FoundSignals));
        OnPropertyChanged(nameof(NonContainerObjects));
        OnPropertyChanged(nameof(FoundContainersView));
        OnPropertyChanged(nameof(FoundSignalsView));
        OnPropertyChanged(nameof(NonContainerObjectsView));
        RefreshLiveDetailViews();
    }

    private static bool Matches(string query, params string?[] values) => FeeContainerSearch.Matches(query, values);

    private void EditContainers()
    {
        if (SelectedRoot is not { } root || IsBusy) return;
        var viewModel = new Fee2ContainerEditVM(root, SupportedContainerTypes);
        var window = new VIBN_Tools.Application.View.Fee2ContainerEditWindow(viewModel);
        if (System.Windows.Application.Current?.MainWindow is { } owner) window.Owner = owner;
        if (window.ShowDialog() != true) return;
        using var edit = BeginWorkspaceEdit();
        root.ApplyEditor(viewModel.CreateResult());
        RefreshSelectionDetails(root);
        StatusText = $"Container und Grouping für '{root.Name}' übernommen. Vor Export die FEE-Objektzuordnungen und Export-Checkboxen prüfen.";
        OnPropertyChanged(nameof(SelectionSummary));
    }

    private void RemoveContainer(Fee2ContainerFoundContainerVM? container)
    {
        if (container is null || SelectedRoot?.Editor is not { } editor || !editor.Containers.Contains(container)) return;
        using var change = BeginWorkspaceEdit();
        if (!editor.RemoveContainer(container)) return;
        ++_crossSelectionRevision;
        _selectedFoundContainer = null; _selectedFoundSignal = null;
        OnPropertyChanged(nameof(SelectedFoundContainer)); OnPropertyChanged(nameof(SelectedFoundSignal));
        ContainerRevealTarget = null; SignalRevealTarget = null;
        FoundContainers.Remove(container);
        NonContainerObjects.ReplaceWith(editor.NonContainerObjects);
        RefreshSignalRows(); RefreshLiveDetailViews();
        OnPropertyChanged(nameof(SelectionSummary));
        StatusText = $"Container '{container.Component}' aus dem Arbeitsstand entfernt. Seine FEE-Objekte und Signalverknüpfungen bleiben im Projekt erhalten.";
    }

    private void RemoveSignal(Fee2ContainerFoundSignalVM? signal)
    {
        if (signal is null)
            return;
        if (SelectedRoot?.Editor is not { } editor) return;
        using var change = BeginWorkspaceEdit();
        editor.Signals.Remove(signal);
        editor.RefreshLiveMetadata();
        RefreshSignalRows();
        StatusText = $"Signal '{signal.Signal}' aus der Containerzuordnung entfernt. Seine FEE-Verknüpfung bleibt erhalten.";
    }

    private void AddObjectAsContainer(Fee2ContainerUnmappedObjectVM? item)
    {
        if (item is null || SelectedRoot?.Editor is not { } editor || !item.CanAdd)
            return;
        using var change = BeginWorkspaceEdit();
        var container = editor.AddObjectAsContainer(item);
        if (!FoundContainers.Contains(container))
            FoundContainers.Add(container);
        item.AddedAsContainer = true;
        editor.NonContainerObjects.Remove(item);
        NonContainerObjects.Remove(item);
        SelectedFoundContainer = container;
        RefreshSignalRows();
        RefreshLiveDetailViews();
        StatusText = $"FEE-Objekt '{item.Name}' wurde als Container '{item.TargetContainerType}' mit aktuellen verknüpften Signalen ergänzt. Typ und Slots vor Export prüfen.";
    }

    private void AssignObjectToContainer(ContainerToFeeVisualDropRequest? request)
    {
        if (request is not null && TryAssignDraggedSignals(request)) return;
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
        using var change = BeginWorkspaceEdit();
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
        if (association is null || SelectedRoot?.Editor is not { } editor || !editor.CreateObjectAssociations().Contains(association))
            return;
        using var change = BeginWorkspaceEdit();
        var restored = editor.RemoveObjectAssociation(association);
        if (restored is not null) NonContainerObjects.Add(restored);
        editor.RefreshLiveMetadata();
        RefreshLiveDetailViews();
        StatusText = $"Zuordnung von '{association.ObjectName}' entfernt. Das FEE-Objekt bleibt im Projekt erhalten.";
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
    public Fee2ContainerRoot Root { get; private set; }
    public Fee2ContainerRootEditor Editor { get; private set; }
    public void ApplyEditor(Fee2ContainerRootEditor editor)
    {
        Editor = editor;
        OnPropertyChanged(nameof(Editor));
        OnPropertyChanged(nameof(ContainerCount));
        OnPropertyChanged(nameof(SignalCount));
    }
    public void ApplyRoot(Fee2ContainerRoot root)
    {
        Root = root; ApplyEditor(new Fee2ContainerRootEditor(root));
        OnPropertyChanged(nameof(InterfacesText)); OnPropertyChanged(nameof(SourceKind));
    }
    public void NotifyCounts()
    { OnPropertyChanged(nameof(ContainerCount)); OnPropertyChanged(nameof(SignalCount)); }
    public bool IsSelected { get => _isSelected; set => SetPropertyChange(ref _isSelected, value); }
    public Guid Guid => Root.Guid;
    public string Name => Root.Name;
    public string SourceKind => Root.SourceKind;
    public string InterfacesText => string.Join(", ", (Root.LiveVariables ?? []).Select(item => item.InterfaceName).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct());
    public int ContainerCount => Editor.Containers.Count(item => item.IsIncluded);
    public int SignalCount => Editor.Signals.Count(item => item.IsIncluded && !string.IsNullOrWhiteSpace(item.Signal) && Editor.Containers.Any(container => container.Id == item.ContainerId && container.IsIncluded));
    public int UpdatedSignalCount => Root.UpdatedSignalCount;
    public int MissingSignalCount => Root.MissingSignalCount;
    public int UpdatedSlotCount => Root.UpdatedSlotCount;
    public int UnresolvedSlotCount => Root.UnresolvedSlotCount;
    public FeeContainerProvenanceSnapshot? Provenance => Root.Provenance;
    public Fee2ContainerRoot CreateEditedRoot(bool includeExcluded = false) => Editor.SourceRoot with
    {
        Provenance = Editor.CreateSnapshot(includeExcluded),
        NonContainerObjects = Editor.NonContainerObjects.Select(item => item.Model).ToArray(),
        ObjectAssociations = Editor.CreateObjectAssociations(),
    };
}

public sealed class Fee2ContainerRootEditor
{
    public Fee2ContainerRoot SourceRoot => _root;
    private readonly Fee2ContainerRoot _root;
    private readonly Dictionary<Guid, XElement> _sourceObjects = [];
    private readonly IReadOnlyDictionary<Guid, FeeContainerObjectDetails> _objectDetailsByGuid;

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
        _objectDetailsByGuid = (root.ObjectDetails ?? []).ToDictionary(item => item.Guid);
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
                    entries.Length, recognitionOrigin: container.Attribute("origin")?.Value ?? "FEE-Struktur",
                    signalTargetGuid: container.Attribute("targetGuid")?.Value ?? "") { IsIncluded = container.Attribute("export")?.Value != "false",
                        IsManuallySpecialDevice = container.Attribute("specialDevice")?.Value == "true", ManuallyChecked = container.Attribute("manuallyChecked")?.Value == "true" };
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
                        variableGuid == Guid.Empty ? null : variableGuid,
                        entry.Attribute("interfaceGuid")?.Value ?? root.LiveVariables?.FirstOrDefault(item => item.VariableGuid == variableGuid)?.InterfaceGuid ?? "",
                        entry.Attribute("interfaceName")?.Value ?? root.LiveVariables?.FirstOrDefault(item => item.VariableGuid == variableGuid)?.InterfaceName ?? "") { IsIncluded = entry.Attribute("export")?.Value != "false" });
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
                (association.IsManual || ContainerFileXml.IsStructuralObjectAssociation(association.ObjectType, association.Role) ||
                    ContainerFileXml.HasMatchingObjectName(association.ObjectName, containerRows[item].Component))).ToArray();
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
            if (group.Select(item => item.ContainerId).Distinct(StringComparer.Ordinal).Count() == 1 ||
                group.All(item => ContainerFileXml.IsTechnicalHelperAssociation(item.ObjectType, item.Role)))
                foreach (var containerGroup in group.GroupBy(item => item.ContainerId))
                    ObjectAssociations.Add(containerGroup.FirstOrDefault(item => item.IsManual) ?? containerGroup.First());
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
        RefreshLiveMetadata();
        foreach (var signal in Signals)
            signal.PropertyChanged += (_, args) =>
            {
                if (IsSignalEdit(args.PropertyName)) RefreshLiveMetadata();
            };
        foreach (var container in Containers) ObserveContainer(container);
    }

    private void ObserveContainer(Fee2ContainerFoundContainerVM container)
    {
        container.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(Fee2ContainerFoundContainerVM.IsIncluded) or nameof(Fee2ContainerFoundContainerVM.ManuallyChecked) or
                nameof(Fee2ContainerFoundContainerVM.IsManuallySpecialDevice)) { RefreshLiveMetadata(); return; }
            if (args.PropertyName is not (nameof(Fee2ContainerFoundContainerVM.Component) or nameof(Fee2ContainerFoundContainerVM.Type))) return;
            foreach (var signal in Signals.Where(item => item.ContainerId == container.Id))
            { signal.Container = container.Component; signal.ContainerType = container.Type; }
            for (var index = 0; index < ObjectAssociations.Count; index++)
            {
                var association = ObjectAssociations[index];
                if (association.ContainerId == container.Id && !ContainerFileXml.IsStructuralObjectAssociation(association.ObjectType, association.Role) &&
                    !ContainerFileXml.HasMatchingObjectName(association.ObjectName, container.Component))
                    ObjectAssociations[index] = association with { IsManual = true, Reason = "Manuelle Änderung des Containernamens" };
            }
            RefreshObjectAssociations();
        };
    }

    public ObservableCollection<Fee2ContainerFoundContainerVM> Containers { get; } = new();
    public ObservableCollection<Fee2ContainerFoundSignalVM> Signals { get; } = new();
    public void AddSignal(Fee2ContainerFoundSignalVM signal)
    {
        foreach (var placeholder in Signals.Where(item => item.ContainerId == signal.ContainerId && item.VariableGuid is null &&
            string.IsNullOrWhiteSpace(item.Signal)).ToArray()) Signals.Remove(placeholder);
        Signals.Add(signal);
        signal.PropertyChanged += (_, args) =>
        {
            if (IsSignalEdit(args.PropertyName)) RefreshLiveMetadata();
        };
    }
    private static bool IsSignalEdit(string? name) => name is nameof(Fee2ContainerFoundSignalVM.Signal) or nameof(Fee2ContainerFoundSignalVM.Slot) or
        nameof(Fee2ContainerFoundSignalVM.IsIncluded) or nameof(Fee2ContainerFoundSignalVM.Address) or nameof(Fee2ContainerFoundSignalVM.DataType) or
        nameof(Fee2ContainerFoundSignalVM.SignalId) or nameof(Fee2ContainerFoundSignalVM.Note);
    public ObservableCollection<Fee2ContainerUnmappedObjectVM> NonContainerObjects { get; } = new();
    private List<FeeContainerObjectAssociation> ObjectAssociations { get; } = [];

    public bool RemoveContainer(Fee2ContainerFoundContainerVM container)
    {
        if (!Containers.Remove(container)) return false;
        foreach (var signal in Signals.Where(item => item.ContainerId == container.Id).ToArray()) Signals.Remove(signal);
        var removed = ObjectAssociations.Where(item => item.ContainerId == container.Id).ToArray();
        ObjectAssociations.RemoveAll(item => item.ContainerId == container.Id);
        foreach (var association in removed.DistinctBy(item => item.ObjectGuid))
        {
            if (ObjectAssociations.Any(item => item.ObjectGuid == association.ObjectGuid) || NonContainerObjects.Any(item => item.Guid == association.ObjectGuid)) continue;
            var restored = new Fee2ContainerUnmappedObjectVM(new FeeContainerUnmappedObject(association.ObjectGuid,
                association.ObjectName, association.ObjectType, "Container wurde aus dem Arbeitsstand entfernt")) { TargetContainerType = container.Type };
            restored.ApplyDetails(_objectDetailsByGuid.GetValueOrDefault(association.ObjectGuid));
            NonContainerObjects.Add(restored);
        }
        RefreshObjectAssociations();
        return true;
    }

    public Fee2ContainerFoundContainerVM AddObjectAsContainer(Fee2ContainerUnmappedObjectVM item)
    {
        if (!item.CanAdd) throw new InvalidOperationException("Cabinets dürfen nur als technische Hilfsobjekte zugeordnet werden; Komponente und Typ müssen angegeben sein.");
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
        var variables = LiveVariables.ToDictionary(variable => variable.VariableGuid);
        var currentRoutes = LiveAssignments.Where(route => route.TargetObjectGuid == item.Guid && variables.ContainsKey(route.VariableGuid)).Distinct().ToArray();
        var added = new Fee2ContainerFoundContainerVM(
            id,
            item.TargetComponent,
            item.TargetContainerType,
            currentRoutes.Length);
        Containers.Add(added);
        ObserveContainer(added);
        AssignObjectToContainer(item, added);
        foreach (var route in currentRoutes)
            if (variables.TryGetValue(route.VariableGuid, out var variable))
                AddSignal(new Fee2ContainerFoundSignalVM(id, added.Component, added.Type, variable.Signal, route.TargetSlot,
                    string.IsNullOrWhiteSpace(variable.Address) ? variable.Path : variable.Address, variable.DataType, variable.Comment,
                    "Aktuelle FEE-Verknüpfung; manuell als Container ergänzt.", variable.VariableGuid, variable.InterfaceGuid, variable.InterfaceName));
        RefreshLiveMetadata();
        return added;
    }

    public void AssignObjectToContainer(
        Fee2ContainerUnmappedObjectVM item,
        Fee2ContainerFoundContainerVM container)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(container);
        if (!ContainerFileXml.IsTechnicalHelperType(item.FeeType)) ObjectAssociations.RemoveAll(association => association.ObjectGuid == item.Guid);
        else ObjectAssociations.RemoveAll(association => association.ObjectGuid == item.Guid && association.ContainerId == container.Id);
        ObjectAssociations.Add(new FeeContainerObjectAssociation(
            item.Guid,
            item.Name,
            item.FeeType,
            Guid.TryParse(container.Id.Replace("fee:", string.Empty, StringComparison.OrdinalIgnoreCase), out var containerGuid)
                ? containerGuid
                : Guid.Empty,
            container.Id,
            "Manuell per Drag-and-drop in FEE2Container zugeordnet",
            ContainerFileXml.IsTechnicalHelperType(item.FeeType) ? "TechnicalHelper" : container.Id == $"manual:{item.Guid:D}" ? "Primary" : "SimObject", IsManual: true));
        RefreshObjectAssociations();
    }

    public static bool HasMatchingName(Fee2ContainerUnmappedObjectVM item, Fee2ContainerFoundContainerVM container) =>
        ContainerFileXml.HasMatchingObjectName(item.Name, container.Component);

    private void RefreshObjectAssociations()
    {
        var details = _objectDetailsByGuid;
        for (var index = 0; index < ObjectAssociations.Count; index++)
            if (details.TryGetValue(ObjectAssociations[index].ObjectGuid, out var detail))
                ObjectAssociations[index] = ObjectAssociations[index] with { ParentName = detail.ParentName,
                    LogicDefinitionName = detail.LogicDefinitionName, ConnectionText = detail.ConnectionText };
        foreach (var container in Containers)
            container.SetAssociatedObjects(ObjectAssociations.Where(item => item.ContainerId == container.Id));
        RefreshLiveMetadata();
    }

    public event EventHandler? ContentChanged;
    public IReadOnlyList<FeeContainerLiveAssignment> LiveAssignments => _root.LiveAssignments ?? [];
    public IReadOnlyList<FeeContainerLiveVariable> LiveVariables => _root.LiveVariables ?? [];
    public IReadOnlyList<FeeContainerObjectDetails> ObjectDetails => _root.ObjectDetails ?? [];

    public void RefreshLiveMetadata()
    {
        var byContainer = Signals.ToLookup(item => item.ContainerId, StringComparer.Ordinal);
        var linksByObject = LiveAssignments.ToLookup(item => item.TargetObjectGuid);
        var details = _objectDetailsByGuid;
        var devices = (_root.SpecialDevices ?? []).ToDictionary(item => item.Guid);
        foreach (var container in Containers)
        {
            var rows = byContainer[container.Id].ToArray();
            if (!container.DataList.SequenceEqual(rows)) container.DataList.ReplaceWith(rows);
            var guids = container.AssociatedObjectItems.Select(item => item.ObjectGuid).ToHashSet();
            container.SetSpecialDevice(guids.Select(guid => devices.GetValueOrDefault(guid)).FirstOrDefault(item => item is not null));
            var assigned = rows.Where(item => item.IsAssigned && item.VariableGuid is not null).Select(item => item.VariableGuid!.Value).ToHashSet();
            container.LinkedUnassignedSignalCount = guids.SelectMany(guid => linksByObject[guid]).Where(item =>
                !assigned.Contains(item.VariableGuid)).Select(item => item.VariableGuid).Distinct().Count();
            container.NotifyValidation();
        }
        foreach (var item in NonContainerObjects) item.ApplyDetails(details.GetValueOrDefault(item.Guid));
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    public Fee2ContainerUnmappedObjectVM? RemoveObjectAssociation(FeeContainerObjectAssociation association)
    {
        if (!ObjectAssociations.Remove(association))
            return null;
        RefreshObjectAssociations();
        if (ObjectAssociations.Any(item => item.ObjectGuid == association.ObjectGuid)) return null;
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
        element.SetAttributeValue("assignment", association.IsManual ? "Manual" :
            ContainerFileXml.IsStructuralObjectAssociation(association.ObjectType, association.Role) ? "Linked" : "Automatic");
        var definition = _objectDetailsByGuid.GetValueOrDefault(association.ObjectGuid)?.LogicDefinitionName;
        if (!string.IsNullOrWhiteSpace(definition)) element.SetElementValue("LogicDefinitionName", definition);
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
                var written = dataList.Elements("Entry").Last();
                if (signal.InterfaceGuid.Length > 0) written.SetAttributeValue("interfaceGuid", signal.InterfaceGuid);
                if (signal.InterfaceName.Length > 0) written.SetAttributeValue("interfaceName", signal.InterfaceName);
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
                new XAttribute("origin", container.RecognitionOrigin),
                container.SignalTargetGuid.Length > 0 ? new XAttribute("targetGuid", container.SignalTargetGuid) : null,
                _root.Provenance?.ContainerDocument.Descendants("Container").FirstOrDefault(item => item.Attribute("id")?.Value == container.Id)?.Attribute("feeRootGuid") is { } rootGuid ? new XAttribute(rootGuid) : null,
                container.IsManuallySpecialDevice ? new XAttribute("specialDevice", "true") : null,
                container.ManuallyChecked ? new XAttribute("manuallyChecked", "true") : null,
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
    private bool _isExpanded;
    private bool _isManuallySpecialDevice;
    private int _linkedUnassignedSignalCount;
    private bool _manuallyChecked;
    private VIBN_Tools.SpecialDevices.Fee2SpecialDeviceRoot? _specialDevice;

    public Fee2ContainerFoundContainerVM(
        string id,
        string component,
        string type,
        int originalSignalCount,
        string associatedObjects = "", string recognitionOrigin = "Manuell", string signalTargetGuid = "")
    {
        Id = id;
        _component = component;
        _type = type;
        OriginalSignalCount = originalSignalCount;
        _associatedObjects = associatedObjects;
        RecognitionOrigin = recognitionOrigin; SignalTargetGuid = signalTargetGuid;
    }

    public string Id { get; }
    public bool IsExpanded { get => _isExpanded; set => SetPropertyChange(ref _isExpanded, value); }
    public string Name => Component;
    public string RecognitionOrigin { get; }
    public string SignalTargetGuid { get; }
    public string InterfacesText => string.Join(", ", DataList.Select(item => item.InterfaceName).Where(name => name.Length > 0).Distinct());
    public bool IsManuallySpecialDevice { get => _isManuallySpecialDevice; set { if (SetPropertyChange(ref _isManuallySpecialDevice, value))
        { OnPropertyChanged(nameof(IsSpecialDevice)); OnPropertyChanged(nameof(SpecialDeviceWarning)); } } }
    public string SpecialDeviceWarning => IsSpecialDevice && VIBN_Tools.SpecialDevices.FeeSpecialDeviceLogicCatalog.Find(LogicDefinitionName) is null
        ? $"Logik '{LogicDefinitionName}' ist noch nicht in SpecialDevices definiert. Die aktuellen Signale bleiben als Kandidat sichtbar." : "";
    public string FeeType => IsSpecialDevice ? "LogicObject" : Type;
    public bool IsSpecialDevice => IsManuallySpecialDevice || _specialDevice is not null || AssociatedObjectItems.Any(item => item.Role == "SpecialDevice");
    public string InputByteText => _specialDevice?.InputByteText ?? "—";
    public string OutputByteText => _specialDevice?.OutputByteText ?? "—";
    public string SignalAddresses => string.Join("; ", DataList.Where(item => !string.IsNullOrWhiteSpace(item.Signal))
        .Select(item => $"{item.Signal}: {item.Address}"));
    public void SetSpecialDevice(VIBN_Tools.SpecialDevices.Fee2SpecialDeviceRoot? device)
    {
        _specialDevice = device;
        OnPropertyChanged(nameof(IsSpecialDevice)); OnPropertyChanged(nameof(InputByteText)); OnPropertyChanged(nameof(OutputByteText));
        OnPropertyChanged(nameof(SignalAddresses));
        OnPropertyChanged(nameof(InterfacesText)); OnPropertyChanged(nameof(SpecialDeviceWarning));
    }

    public string Component { get => _component; set { if (SetPropertyChange(ref _component, value)) OnPropertyChanged(nameof(Name)); } }
    public string Type { get => _type; set { if (SetPropertyChange(ref _type, value)) OnPropertyChanged(nameof(FeeType)); } }
    public int OriginalSignalCount { get; }
    public string AssociatedObjects => _associatedObjects;
    public int AssociatedObjectCount => AssociatedObjectItems.Count;
    public ObservableCollection<Fee2ContainerFoundSignalVM> DataList { get; } = new();
    public int LinkedUnassignedSignalCount { get => _linkedUnassignedSignalCount; internal set => SetPropertyChange(ref _linkedUnassignedSignalCount, value); }
    public string ParentName => string.Join(", ", AssociatedObjectItems.Select(item => item.ParentName).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct());
    public string LogicDefinitionName
    {
        get
        {
            var primary = AssociatedObjectItems.Where(item => item.Role is "Primary" or "SpecialDevice")
                .Select(item => item.LogicDefinitionName).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().ToArray();
            return string.Join(", ", primary.Length > 0 ? primary : AssociatedObjectItems.Select(item => item.LogicDefinitionName)
                .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct());
        }
    }
    public string ConnectionText => string.Join("; ", AssociatedObjectItems.Select(item => item.ConnectionText).Where(text => !string.IsNullOrWhiteSpace(text)).Distinct());
    public bool ManuallyChecked { get => _manuallyChecked; set => SetPropertyChange(ref _manuallyChecked, value); }
    public bool HasErrors => string.IsNullOrWhiteSpace(Component) || string.IsNullOrWhiteSpace(Type);
    public bool HasWarnings => !HasErrors && (string.Equals(Type, "N/A", StringComparison.OrdinalIgnoreCase) ||
        AssociatedObjectCount == 0 || DataList.Count == 0 || DataList.Any(item => item.IsIncluded && !item.IsAssigned) || LinkedUnassignedSignalCount > 0 || SpecialDeviceWarning.Length > 0);
    public string ValidationError => HasErrors ? "Komponente oder Typ fehlt" : string.Equals(Type, "N/A", StringComparison.OrdinalIgnoreCase)
        ? "Typ N/A: Container-/Slotdefinition für die vorhandene Logik noch festlegen."
        : SpecialDeviceWarning.Length > 0 ? SpecialDeviceWarning
        : AssociatedObjectCount == 0 ? "Signalcontainer erkannt; FEE-Objekte noch separat zuordnen oder bewusst ohne Objekt exportieren."
        : HasWarnings ? "Signalzuordnung prüfen" : "";
    public void NotifyValidation()
    { OnPropertyChanged(nameof(HasErrors)); OnPropertyChanged(nameof(HasWarnings)); OnPropertyChanged(nameof(ValidationError)); }
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
        OnPropertyChanged(nameof(ParentName));
        OnPropertyChanged(nameof(LogicDefinitionName));
        OnPropertyChanged(nameof(ConnectionText));
        OnPropertyChanged(nameof(SpecialDeviceWarning));
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
        string address, string dataType, string signalId, string note, Guid? variableGuid, string interfaceGuid = "", string interfaceName = "")
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
        InterfaceGuid = interfaceGuid; InterfaceName = interfaceName;
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
    public string InterfaceGuid { get; }
    public string InterfaceName { get; }
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
    private FeeContainerObjectDetails? _details;

    public Fee2ContainerUnmappedObjectVM(FeeContainerUnmappedObject model)
    {
        Model = model;
        _targetContainerType = "N/A";
        _targetComponent = model.Name;
    }

    public FeeContainerUnmappedObject Model { get; }
    public Guid Guid => Model.Guid;
    public string Name => Model.Name;
    public string FeeType => Model.FeeType;
    public string Reason => Model.Reason;
    public string ParentName => _details?.ParentName ?? "";
    public string LogicDefinitionName => _details?.LogicDefinitionName ?? "";
    public string ConnectionText => _details?.ConnectionText ?? "";
    public bool IsSpecialDevice => _details?.IsSpecialDevice == true;
    public void ApplyDetails(FeeContainerObjectDetails? details)
    {
        if (Equals(_details, details)) return;
        _details = details;
        OnPropertyChanged(nameof(ParentName)); OnPropertyChanged(nameof(LogicDefinitionName));
        OnPropertyChanged(nameof(ConnectionText)); OnPropertyChanged(nameof(IsSpecialDevice));
    }
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
    public bool CanAdd => !string.Equals(FeeType.Split('.').Last(), "Cabinet", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(TargetContainerType) && !string.IsNullOrWhiteSpace(TargetComponent);
}
