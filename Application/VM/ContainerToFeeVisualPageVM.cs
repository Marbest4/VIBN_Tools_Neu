using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows;
using Microsoft.Win32;
using VIBN_Tools.Application.Behaviors;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.Core.Collections;
using VIBN_Tools.SharedWpf.Commands;
using VIBN_Tools.Settings;

namespace VIBN_Tools.Application.VM;

/// <summary>
/// Presentation model for the optional visual Container2FEE workflow.  The
/// existing ContainerToFeePageVM is intentionally not used or modified.  All
/// mutations go through <see cref="ContainerToFeeVisualPlanService"/>, which
/// keeps assignments, sidecars and undo/redo consistent with the unchanged
/// legacy generation executor.
/// </summary>
public sealed class ContainerToFeeVisualPageVM : MvvmBase
{
    private const string LogArea = "Container2FEE Visual";
    private readonly ContainerToFeeVisualPlanService _planService;
    private readonly ApplicationLogService _log;
    private readonly FeeConnectionService _connection;
    private CancellationTokenSource? _operationCancellation;
    private ContainerToFeeVisualTreeNodeVM? _selectedTreeNode;
    private ContainerToFeeVisualTargetVM? _selectedTarget;
    private ContainerToFeeVisualFeeInterfaceVM? _selectedExistingInterface;
    private string _treeFilter = string.Empty;
    private string _feeObjectFilter = string.Empty;
    private string _feeSignalFilter = string.Empty;
    private bool _showOnlyCompatibleFeeObjects;
    private bool _isBusy;
    private string _statusText = "Container-XML öffnen, um eine Vorschau zu erstellen.";
    private string _sourceXmlPath = string.Empty;
    private bool _isApplyingPlan;
    private IReadOnlyList<VisualIssue> _lastExecutionIssues = Array.Empty<VisualIssue>();
    private int _generationProgress;
    private string _generationProgressText = string.Empty;
    private string _feeRefreshHint =
        "Nach Änderungen im FEE-Projekt zuerst 'FEE aktualisieren'. Fehlen danach erwartete Objekte, einmal Model Validation ausführen und anschließend erneut aktualisieren.";
    private readonly HashSet<string> _verifiedContainerIds = new(StringComparer.Ordinal);

    public ContainerToFeeVisualPageVM()
        : this(new ContainerToFeeVisualPlanService(), ResolveConnection(), ApplicationLogService.Instance)
    {
    }

    /// <summary>Creates the page around an already loaded plan, e.g. for host integration and UI tests.</summary>
    public ContainerToFeeVisualPageVM(ContainerToFeeVisualPlanService planService)
        : this(planService, ResolveConnection(), ApplicationLogService.Instance)
    {
    }

    internal ContainerToFeeVisualPageVM(
        ContainerToFeeVisualPlanService planService,
        FeeConnectionService connection,
        ApplicationLogService log)
    {
        _planService = planService;
        _connection = connection;
        _log = log;

        FeeObjectsView = CollectionViewSource.GetDefaultView(AvailableFeeObjects);
        FeeObjectsView.Filter = FilterFeeObject;
        FeeObjectsView.SortDescriptions.Add(
            new SortDescription(nameof(ContainerToFeeVisualFeeObjectVM.Name), ListSortDirection.Ascending));
        FeeSignalsView = CollectionViewSource.GetDefaultView(AvailableFeeSignals);
        FeeSignalsView.Filter = FilterFeeSignal;

        OpenXmlCommand = new AsyncRelayCommand(OpenXmlAsync, () => !IsBusy);
        LoadPlanCommand = new AsyncRelayCommand(LoadPlanAsync, () => !IsBusy);
        SavePlanCommand = new AsyncRelayCommand(SavePlanAsync, () => HasPlan && !IsBusy);
        SaveContainerXmlCommand = new AsyncRelayCommand(SaveContainerXmlAsync, () => HasPlan && !IsBusy);
        RefreshFeeObjectsCommand = new AsyncRelayCommand(
            RefreshFeeObjectsAsync,
            () => HasPlan && Connection.CanUseFeeFeatures && !IsBusy);
        AutoAssignCommand = new RelayCommand(
            AutoAssignMatches,
            () => HasPlan && AvailableFeeObjects.Count > 0 && !IsBusy);
        StartGenerationCommand = new AsyncRelayCommand(
            StartGenerationAsync,
            () => CanStartGeneration);
        LinkOnlyCommand = new AsyncRelayCommand(
            LinkOnlyAsync,
            () => CanLinkOnly);
        LinkSignalsOnlyCommand = new AsyncRelayCommand(
            LinkSignalsOnlyAsync,
            () => CanLinkSignalsOnly);
        SelectAllCommand = new RelayCommand(
            () => SetAllGenerationSelected(true),
            () => HasPlan && !IsBusy);
        DeselectAllCommand = new RelayCommand(
            () => SetAllGenerationSelected(false),
            () => HasPlan && !IsBusy);
        ExpandAllCommand = new RelayCommand(
            () => SetTreeExpanded(true),
            () => HasPlan && !IsBusy);
        CollapseAllCommand = new RelayCommand(
            () => SetTreeExpanded(false),
            () => HasPlan && !IsBusy);
        SelectAllMissingCreationCommand = new RelayCommand(
            () => SetAllCreationRequested(true),
            () => HasPlan && !IsBusy);
        DeselectAllMissingCreationCommand = new RelayCommand(
            () => SetAllCreationRequested(false),
            () => HasPlan && !IsBusy);
        CancelCommand = new RelayCommand(CancelOperation, () => IsBusy);
        UndoCommand = new RelayCommand(Undo, () => _planService.CanUndo && !IsBusy);
        RedoCommand = new RelayCommand(Redo, () => _planService.CanRedo && !IsBusy);
        DropCommand = new RelayCommand<ContainerToFeeVisualDropRequest>(
            HandleDrop,
            CanHandleDrop);
        RemoveAssignmentCommand = new RelayCommand<ContainerToFeeVisualAssignmentVM>(
            RemoveAssignment,
            assignment => assignment is not null && !IsBusy);
        RemoveSignalCommand = new RelayCommand<ContainerToFeeVisualSignalEntryVM>(
            RemoveSignal,
            signal => signal is not null && !IsBusy);
        DeleteTreeNodeCommand = new RelayCommand<ContainerToFeeVisualTreeNodeVM>(
            DeleteTreeNode,
            node => node is not null && !IsBusy && CanDeleteTreeNode(node));
        ToggleTreeNodeGenerationCommand = new RelayCommand<ContainerToFeeVisualTreeNodeVM>(
            ToggleTreeNodeGeneration,
            node => node?.ContainerId is not null && !IsBusy);

        _planService.PlanChanged += OnPlanChanged;
        _connection.PropertyChanged += OnConnectionPropertyChanged;

        if (_planService.CurrentPlan is not null)
        {
            ApplyPlan(_planService.CurrentPlan);
            StatusText = "Gespeicherter visueller Plan ist geladen.";
        }
    }

    public FeeConnectionService Connection => _connection;

    public ObservableCollection<ContainerToFeeVisualTreeNodeVM> TreeRoots { get; } = new();

    public ObservableCollection<ContainerToFeeVisualTargetVM> Targets { get; } = new();

    public ObservableCollection<ContainerToFeeVisualSignalEntryVM> SelectedContainerSignals { get; } = new();

    public ObservableCollection<ContainerToFeeVisualEdgeVM> VisibleEdges { get; } = new();

    public ObservableCollection<ContainerToFeeVisualFeeObjectVM> AvailableFeeObjects { get; } = new();

    public ObservableCollection<ContainerToFeeVisualFeeInterfaceVM> AvailableFeeInterfaces { get; } = new();

    public ObservableCollection<ContainerToFeeVisualFeeSignalVM> AvailableFeeSignals { get; } = new();

    public ObservableCollection<VisualIssue> Issues { get; } = new();

    public ICollectionView FeeObjectsView { get; }
    public ICollectionView FeeSignalsView { get; }

    public ICommand OpenXmlCommand { get; }

    public ICommand LoadPlanCommand { get; }

    public ICommand SavePlanCommand { get; }

    public ICommand SaveContainerXmlCommand { get; }

    public ICommand RefreshFeeObjectsCommand { get; }

    public ICommand AutoAssignCommand { get; }

    public ICommand StartGenerationCommand { get; }

    public ICommand LinkOnlyCommand { get; }
    public ICommand LinkSignalsOnlyCommand { get; }

    public ICommand SelectAllCommand { get; }

    public ICommand DeselectAllCommand { get; }

    public ICommand ExpandAllCommand { get; }

    public ICommand CollapseAllCommand { get; }

    public ICommand SelectAllMissingCreationCommand { get; }

    public ICommand DeselectAllMissingCreationCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand UndoCommand { get; }

    public ICommand RedoCommand { get; }

    public ICommand DropCommand { get; }

    public ICommand RemoveAssignmentCommand { get; }

    public ICommand RemoveSignalCommand { get; }

    public ICommand DeleteTreeNodeCommand { get; }

    public ICommand ToggleTreeNodeGenerationCommand { get; }

    public bool HasPlan => _planService.CurrentPlan is not null;

    public bool HasValidationErrors => Issues.Any(issue => issue.Severity == VisualIssueSeverity.Error);

    public bool IsFeeObjectDiscoveryAvailable => Connection.CanUseFeeFeatures && HasPlan && !IsBusy;

    public bool CanStartGeneration => HasPlan && Connection.CanUseFeeFeatures && !IsBusy &&
                                      SelectedContainerCount > 0;

    public bool CanLinkOnly => HasPlan && Connection.CanUseFeeFeatures && !IsBusy &&
                               !HasValidationErrors && SelectedAssignmentCount > 0;

    public bool CanLinkSignalsOnly => HasPlan && Connection.CanUseFeeFeatures && !IsBusy &&
                                      SelectedExistingInterface?.IsNone == false;

    public int SelectedAssignmentCount
    {
        get
        {
            var plan = _planService.CurrentPlan;
            if (plan is null)
                return 0;
            var selectedContainerIds = plan.Nodes
                .Where(node => node.Kind == VisualNodeKind.Container && plan.IsGenerationSelected(node.Id))
                .Select(node => node.Id)
                .ToHashSet(StringComparer.Ordinal);
            return plan.Assignments.Count(assignment =>
                plan.FindTarget(assignment.TargetId) is { } target &&
                selectedContainerIds.Contains(target.ContainerId));
        }
    }

    public string FeeUnavailableReason => Connection.CanUseFeeFeatures
        ? string.Empty
        : FeeConnectionService.MissingConnectionMessage;

    public string RefreshFeeObjectsUnavailableReason => IsBusy
        ? "Ein Container2FEE-Vorgang läuft bereits."
        : !HasPlan
            ? "Zuerst eine Container-XML oder einen gespeicherten Plan laden."
            : !Connection.CanUseFeeFeatures
                ? Connection.UnavailableReason
                : string.Empty;

    public string StartGenerationUnavailableReason => GetExecutionUnavailableReason(linkOnly: false);

    public string LinkOnlyUnavailableReason => GetExecutionUnavailableReason(linkOnly: true);

    public string LinkSignalsOnlyUnavailableReason => IsBusy
        ? "Ein Container2FEE-Vorgang läuft bereits."
        : !HasPlan
            ? "Zuerst eine Container-XML laden."
            : !Connection.CanUseFeeFeatures
                ? Connection.UnavailableReason
                : SelectedExistingInterface?.IsNone != false
                    ? "Ein vorhandenes Interface auswählen."
                    : string.Empty;

    public string SourceXmlPath
    {
        get => _sourceXmlPath;
        private set
        {
            _sourceXmlPath = value;
            OnPropertyChanged();
        }
    }

    public string SidecarPath => _planService.CurrentPlan?.SidecarPath ?? string.Empty;

    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public string FeeRefreshHint
    {
        get => _feeRefreshHint;
        private set
        {
            if (string.Equals(_feeRefreshHint, value, StringComparison.Ordinal))
                return;
            _feeRefreshHint = value;
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
            OnPropertyChanged(nameof(IsFeeObjectDiscoveryAvailable));
            OnPropertyChanged(nameof(CanStartGeneration));
            OnPropertyChanged(nameof(CanLinkOnly));
            OnPropertyChanged(nameof(CanLinkSignalsOnly));
            OnPropertyChanged(nameof(RefreshFeeObjectsUnavailableReason));
            OnPropertyChanged(nameof(StartGenerationUnavailableReason));
            OnPropertyChanged(nameof(LinkOnlyUnavailableReason));
            OnPropertyChanged(nameof(LinkSignalsOnlyUnavailableReason));
            InvalidateCommands();
        }
    }

    public string TreeFilter
    {
        get => _treeFilter;
        set
        {
            if (string.Equals(_treeFilter, value, StringComparison.Ordinal))
                return;

            _treeFilter = value ?? string.Empty;
            OnPropertyChanged();
            ApplyTreeFilter();
        }
    }

    public string FeeObjectFilter
    {
        get => _feeObjectFilter;
        set
        {
            if (string.Equals(_feeObjectFilter, value, StringComparison.Ordinal))
                return;

            _feeObjectFilter = value ?? string.Empty;
            OnPropertyChanged();
            FeeObjectsView.Refresh();
        }
    }

    public string FeeSignalFilter
    {
        get => _feeSignalFilter;
        set
        {
            if (string.Equals(_feeSignalFilter, value, StringComparison.Ordinal))
                return;

            _feeSignalFilter = value ?? string.Empty;
            OnPropertyChanged();
            FeeSignalsView.Refresh();
        }
    }

    public bool ShowOnlyCompatibleFeeObjects
    {
        get => _showOnlyCompatibleFeeObjects;
        set
        {
            if (_showOnlyCompatibleFeeObjects == value)
                return;

            _showOnlyCompatibleFeeObjects = value;
            OnPropertyChanged();
            FeeObjectsView.Refresh();
        }
    }

    public bool SelectedContainerSupportsCreation
    {
        get
        {
            var containerId = SelectedTreeNode?.ContainerId;
            return containerId is not null &&
                   _planService.CurrentPlan?.FindNode(containerId)?.SupportsCreation == true;
        }
    }

    public ContainerToFeeVisualFeeInterfaceVM? SelectedExistingInterface
    {
        get => _selectedExistingInterface;
        set
        {
            if (ReferenceEquals(_selectedExistingInterface, value))
                return;

            _selectedExistingInterface = value;
            OnPropertyChanged();
            if (!_isApplyingPlan)
                _planService.SetExistingInterface(value?.Model);
            RefreshFeeSignalProjection(_planService.DiscoveredFeeSignals);
            OnPropertyChanged(nameof(CanLinkSignalsOnly));
            OnPropertyChanged(nameof(LinkSignalsOnlyUnavailableReason));
            InvalidateCommands();
        }
    }

    public bool IsCreationRequestedForSelection
    {
        get
        {
            var containerId = SelectedTreeNode?.ContainerId;
            return containerId is not null &&
                   _planService.CurrentPlan?.IsCreationRequested(containerId) == true;
        }
        set
        {
            var containerId = SelectedTreeNode?.ContainerId;
            if (containerId is null ||
                IsCreationRequestedForSelection == value ||
                !_planService.SetCreationRequested(containerId, value))
                return;

            StatusText = value
                ? "Fehlende SimObjects werden bei der Generierung erzeugt."
                : "Nicht zugeordnete SimObjects werden übersprungen.";
            _log.Information(LogArea, StatusText);
            OnPropertyChanged();
        }
    }

    public ContainerToFeeVisualTreeNodeVM? SelectedTreeNode
    {
        get => _selectedTreeNode;
        set
        {
            if (ReferenceEquals(_selectedTreeNode, value))
                return;

            _selectedTreeNode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedContainerSupportsCreation));
            OnPropertyChanged(nameof(IsCreationRequestedForSelection));
            RefreshSelectionProjection();
        }
    }

    public ContainerToFeeVisualTargetVM? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (ReferenceEquals(_selectedTarget, value))
                return;

            _selectedTarget = value;
            OnPropertyChanged();
            FeeObjectsView.Refresh();
        }
    }

    public int ContainerCount => _planService.CurrentPlan?.Nodes.Count(node =>
        node.Kind == VisualNodeKind.Container) ?? 0;

    public int SelectedContainerCount => _planService.CurrentPlan?.Nodes.Count(node =>
        node.Kind == VisualNodeKind.Container &&
        ContainerMetadataCatalog.TryGet(node.TypeName, out _) &&
        _planService.CurrentPlan.IsGenerationSelected(node.Id)) ?? 0;

    public int ObjectCount => _planService.CurrentPlan?.Nodes.Count ?? 0;

    public int EdgeCount => _planService.CurrentPlan?.Edges.Count ?? 0;

    public int AssignmentCount => _planService.CurrentPlan?.Assignments.Count ?? 0;

    private async Task OpenXmlAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Container XML (*.xml)|*.xml|Alle Dateien (*.*)|*.*",
            Title = "Container-XML für visuelle Planung öffnen",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
            return;

        await RunBusyAsync("Container-XML wird analysiert …", async cancellationToken =>
        {
            VisualPlanLoadResult result = await _planService.LoadXmlAsync(dialog.FileName, cancellationToken);
            PublishIssues(result.Issues);
            if (!result.Success)
            {
                StatusText = result.Message;
                _log.Warning(LogArea, result.Message);
                return;
            }

            ApplyPlan(result.Plan!);
            StatusText = result.Message;
            _log.Information(LogArea, result.Message);
        });
    }

    private async Task LoadPlanAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Container2FEE Visual Plan (*.json)|*.json|Alle Dateien (*.*)|*.*",
            Title = "Gespeicherten Container2FEE-Plan laden",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
            return;

        await RunBusyAsync("Plan wird geladen …", async cancellationToken =>
        {
            VisualPlanLoadResult result = await _planService.LoadSidecarAsync(dialog.FileName, cancellationToken);
            PublishIssues(result.Issues);
            if (!result.Success)
            {
                StatusText = result.Message;
                _log.Warning(LogArea, result.Message);
                return;
            }

            ApplyPlan(result.Plan!);
            StatusText = result.Message;
            _log.Information(LogArea, result.Message);
        });
    }

    private async Task SavePlanAsync()
    {
        await RunBusyAsync("Plan wird gespeichert …", async cancellationToken =>
        {
            await _planService.SaveSidecarAsync(cancellationToken: cancellationToken);
            OnPropertyChanged(nameof(SidecarPath));
            StatusText = $"Plan gespeichert: {_planService.CurrentPlan?.SidecarPath}";
            _log.Information(LogArea, StatusText);
        });
    }

    private async Task RefreshFeeObjectsAsync()
    {
        if (!Connection.CanUseFeeFeatures)
        {
            Reject(FeeConnectionService.MissingConnectionMessage);
            return;
        }

        await RunBusyAsync("FEE-SimObjects werden gelesen …", async cancellationToken =>
        {
            var objectsTask = _planService.DiscoverFeeObjectsAsync(cancellationToken);
            var interfacesTask = _planService.DiscoverFeeInterfacesAsync(cancellationToken);
            await Task.WhenAll(objectsTask, interfacesTask);
            IReadOnlyList<VisualFeeObject> objects = await objectsTask;
            IReadOnlyList<VisualFeeInterface> interfaces = await interfacesTask;
            RefreshFeeObjectProjection(objects);
            RefreshFeeInterfaceProjection(interfaces);
            RefreshFeeSignalProjection(_planService.DiscoveredFeeSignals);
            // Auto-assignment raises PlanChanged and rebuilds the tree. Apply
            // live discovery colours only afterwards so complete-container
            // verification is not lost again in that rebuild.
            int automaticAssignments = _planService.AutoAssignMatches();
            ApplyDiscoveredContainerObjectStates(_planService.DiscoveredFeeContainerObjects);
            ApplyDiscoveredSignalStates(_planService.DiscoveredFeeSignals);
            var verifiedContainers = await _planService
                .DiscoverVerifiedContainerIdsAsync(cancellationToken);
            _verifiedContainerIds.Clear();
            _verifiedContainerIds.UnionWith(verifiedContainers);
            ApplyVerifiedContainerStates(verifiedContainers);
            FeeObjectsView.Refresh();
            FeeRefreshHint = objects.Count == 0 || _planService.DiscoveredFeeSignals.Count == 0
                ? "FEE lieferte keine SimObjects oder Signale. Model Validation ausführen, damit der Projektzustand vollständig eingelesen wird, danach hier erneut 'FEE aktualisieren' wählen."
                : "FEE-Daten wurden direkt über die API aktualisiert. Falls kürzlich geänderte SimObjects oder Signale fehlen: Model Validation ausführen und danach erneut aktualisieren.";
            StatusText = automaticAssignments > 0
                ? $"{objects.Count} FEE-SimObjects, {_planService.DiscoveredFeeContainerObjects.Count} Logik-/Cabinet-Objekte, {_planService.DiscoveredFeeSignals.Count} Signale und {interfaces.Count} Interfaces geladen; " +
                  $"{automaticAssignments} automatisch zugeordnet; {verifiedContainers.Count} Container vollständig verifiziert."
                : $"{objects.Count} FEE-SimObjects, {_planService.DiscoveredFeeContainerObjects.Count} Logik-/Cabinet-Objekte, {_planService.DiscoveredFeeSignals.Count} Signale und {interfaces.Count} Interfaces geladen; " +
                  $"{verifiedContainers.Count} Container vollständig verifiziert.";
            _log.Information(LogArea, StatusText);
            InvalidateCommands();
        });
    }

    private void AutoAssignMatches()
    {
        int count = _planService.AutoAssignMatches();
        StatusText = count > 0
            ? $"{count} FEE-SimObject-Zuordnung(en) automatisch erkannt."
            : "Keine weiteren eindeutigen Namens-/Typzuordnungen gefunden.";
        _log.Information(LogArea, StatusText);
    }

    private async Task StartGenerationAsync()
    {
        if (!Connection.CanUseFeeFeatures)
        {
            Reject(FeeConnectionService.MissingConnectionMessage);
            return;
        }

        VisualValidationResult validation = _planService.Validate();
        var currentIssues = validation.Issues
            .Concat(_lastExecutionIssues)
            .DistinctBy(issue => (issue.Severity, issue.Code, issue.Message, issue.NodeId))
            .ToArray();
        PublishIssues(currentIssues);
        IReadOnlyList<VisualIssue>? acceptedErrors = null;
        if (currentIssues.Any(issue => issue.Severity == VisualIssueSeverity.Error))
        {
            var errors = currentIssues
                .Where(issue => issue.Severity == VisualIssueSeverity.Error)
                .ToArray();
            var details = string.Join(
                Environment.NewLine,
                errors.Take(12).Select(issue => $"• [{issue.Code}] {issue.Message}"));
            if (errors.Length > 12)
                details += $"{Environment.NewLine}• … und {errors.Length - 12} weitere Fehler";
            var answer = MessageBox.Show(
                "ACHTUNG: Der Plan ist ungültig. Eine reguläre Generierung ist gesperrt." +
                Environment.NewLine + Environment.NewLine + details +
                Environment.NewLine + Environment.NewLine +
                "Wenn Sie trotzdem fortfahren, wird eine ausdrücklich bestätigte Best-Effort-Generierung " +
                "auch für auffällige Container versucht. Der erzeugte erste BasicFrame erhält den Zusatz " +
                "„Trotz Validierungsfehlern erstellt“. Zusätzlich wird pro bestätigtem Fehler ein eigener " +
                "BasicFrame als Kind mit Fehlercode und Meldung angelegt. Nicht deterministisch auflösbare " +
                "Laufzeitkonflikte (zum Beispiel widersprüchliche Signale) brechen weiterhin sicher ab." +
                Environment.NewLine + Environment.NewLine +
                "Fehlerhafte Teilgenerierung ausdrücklich starten?",
                "UNGÜLTIGE GENERIERUNG ERZWINGEN",
                MessageBoxButton.YesNo,
                MessageBoxImage.Error,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                StatusText = "Generierung abgebrochen: Der ungültige Plan wurde nicht bestätigt.";
                _log.Warning(LogArea, StatusText);
                return;
            }
            acceptedErrors = errors;
        }

        await RunBusyAsync("Container werden mit dem bestehenden Executor erzeugt …", async cancellationToken =>
        {
            GenerationProgress = 0;
            GenerationProgressText = "Generierung wird vorbereitet …";
            var progress = new Progress<VisualGenerationProgress>(update =>
            {
                GenerationProgress = update.Percent;
                GenerationProgressText = update.Message;
            });
            VisualExecutionResult result = await _planService.ExecuteAsync(
                acceptedErrors,
                progress,
                cancellationToken);
            _lastExecutionIssues = result.Issues
                .Where(issue => issue.Severity == VisualIssueSeverity.Error)
                .ToArray();
            PublishIssues(result.Issues);
            StatusText = result.Message;
            if (result.Success)
            {
                MarkSelectedTreeNodesVerified();
                GenerationProgress = 100;
                GenerationProgressText = "FEE-Generierung abgeschlossen.";
                _log.Information(LogArea, result.Message);
                MessageBox.Show(
                    result.Message,
                    "Container2FEE Visual",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                GenerationProgressText = "Generierung nicht vollständig abgeschlossen.";
                _log.Warning(LogArea, result.Message);
            }
        });
    }

    private async Task LinkOnlyAsync()
    {
        if (!Connection.CanUseFeeFeatures)
        {
            Reject(FeeConnectionService.MissingConnectionMessage);
            return;
        }

        await RunBusyAsync(
            "Bestehende FEE-SimObjects werden ohne Neugenerierung verknüpft …",
            async cancellationToken =>
            {
                VisualExecutionResult result =
                    await _planService.LinkExistingAssignmentsOnlyAsync(cancellationToken);
                PublishIssues(result.Issues);
                StatusText = result.Message;
                if (result.Success)
                    _log.Information(LogArea, result.Message);
                else
                    _log.Warning(LogArea, result.Message);
        });
    }

    private async Task LinkSignalsOnlyAsync()
    {
        if (!Connection.CanUseFeeFeatures)
        {
            Reject(FeeConnectionService.MissingConnectionMessage);
            return;
        }

        await RunBusyAsync(
            "Vorhandene Interface-Signale werden ohne Objekterzeugung verknüpft …",
            async cancellationToken =>
            {
                VisualExecutionResult result =
                    await _planService.LinkExistingSignalsOnlyAsync(cancellationToken);
                PublishIssues(result.Issues);
                StatusText = result.Message;
                if (result.Success)
                    _log.Information(LogArea, result.Message);
                else
                    _log.Warning(LogArea, result.Message);
            });
    }

    private void SetAllGenerationSelected(bool selected)
    {
        var changed = _planService.SetAllGenerationSelected(selected);
        StatusText = changed == 0
            ? selected ? "Alle unterstützten Container waren bereits ausgewählt."
                       : "Alle unterstützten Container waren bereits abgewählt."
            : selected ? $"{changed} Container wurden ausgewählt."
                       : $"{changed} Container wurden abgewählt.";
        _log.Information(LogArea, StatusText);
    }

    public int GenerationProgress
    {
        get => _generationProgress;
        private set
        {
            _generationProgress = Math.Clamp(value, 0, 100);
            OnPropertyChanged();
        }
    }

    public string GenerationProgressText
    {
        get => _generationProgressText;
        private set
        {
            _generationProgressText = value;
            OnPropertyChanged();
        }
    }

    private void SetAllCreationRequested(bool requested)
    {
        var changed = _planService.SetAllCreationRequested(requested);
        StatusText = changed == 0
            ? requested
                ? "Die Erzeugung fehlender SimObjects war bereits überall aktiviert."
                : "Die Erzeugung fehlender SimObjects war bereits überall deaktiviert."
            : requested
                ? $"Für {changed} Container wurde die Erzeugung fehlender SimObjects aktiviert."
                : $"Für {changed} Container wurde die Erzeugung fehlender SimObjects deaktiviert.";
        _log.Information(LogArea, StatusText);
    }

    private void SetTreeExpanded(bool expanded)
    {
        foreach (var node in TreeRoots.SelectMany(root => root.SelfAndDescendants()))
            node.IsExpanded = expanded;

        StatusText = expanded ? "Beide Strukturen wurden aufgeklappt." : "Beide Strukturen wurden zugeklappt.";
    }

    private void HandleDrop(ContainerToFeeVisualDropRequest? request)
    {
        IReadOnlyList<object> sources = request?.Source is IReadOnlyList<object> selectedItems
            ? selectedItems
            : request?.Source is null ? [] : [request.Source];

        if (request?.Target is ContainerToFeeVisualTreeNodeVM dropNode)
        {
            // The selected item is the scroll anchor used after the immutable
            // tree projection is rebuilt by the plan mutation.
            SelectedTreeNode = dropNode;
        }

        if (request?.Target is ContainerToFeeVisualTreeNodeVM signalGroup &&
            signalGroup.Kind == VisualNodeKind.Group &&
            signalGroup.Id.EndsWith(":signals", StringComparison.Ordinal) &&
            sources.All(item => item is ContainerToFeeVisualFeeSignalVM))
        {
            var result = _planService.AddSignals(
                signalGroup.ContainerId ?? string.Empty,
                sources.Cast<ContainerToFeeVisualFeeSignalVM>().Select(item => item.GuidString));
            PublishIssues(result.Issues);
            if (!result.Success)
            {
                Reject(result.Message);
                return;
            }
            StatusText = result.Message;
            _log.Information(LogArea, result.Message);
            return;
        }

        if (request?.Target is ContainerToFeeVisualTreeNodeVM signalTarget &&
            signalTarget.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal &&
            sources.All(item => item is ContainerToFeeVisualFeeSignalVM))
        {
            var signalSources = sources.Cast<ContainerToFeeVisualFeeSignalVM>().ToArray();
            var signalTargets = ResolveSignalDropTargets(signalTarget, signalSources.Length);
            if (signalTargets.Count < signalSources.Length)
            {
                Reject($"Für {signalSources.Length} ausgewählte FEE-Signale sind nur {signalTargets.Count} freie Container-Signale mit Slot '{signalTarget.Slot}' vorhanden.");
                return;
            }

            for (var index = 0; index < signalSources.Length; index++)
            {
                var signalResult = _planService.TryAssignSignal(
                    signalTargets[index].Id,
                    signalSources[index].GuidString);
                PublishIssues(signalResult.Issues);
                if (!signalResult.Success)
                {
                    Reject(signalResult.Message);
                    return;
                }
            }

            StatusText = signalSources.Length == 1
                ? $"FEE-Signal '{signalSources[0].Tag}' wurde Slot '{signalTarget.Slot}' zugeordnet."
                : $"{signalSources.Length} FEE-Signale wurden freien Einträgen mit Slot '{signalTarget.Slot}' zugeordnet.";
            _log.Information(LogArea, StatusText);
            return;
        }

        var target = request?.Target switch
        {
            ContainerToFeeVisualTargetVM targetVm => targetVm,
            ContainerToFeeVisualTreeNodeVM treeNode when treeNode.Kind == VisualNodeKind.SimObjectTarget =>
                CreateTargetVm(treeNode.Id),
            ContainerToFeeVisualTreeNodeVM treeNode when treeNode.Kind == VisualNodeKind.Group &&
                                                            treeNode.Id.EndsWith(":simobjects", StringComparison.Ordinal) =>
                ResolveCompatibleTarget(treeNode, sources),
            _ => null,
        };
        if (target is null)
            return;

        var feeObjectIds = sources.Select(item => item switch
            {
                ContainerToFeeVisualFeeObjectVM feeObject => feeObject.Id,
                ContainerToFeeVisualAssignmentVM assignment => assignment.FeeObjectId,
                _ => null,
            })
            .Where(item => item is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (feeObjectIds.Length != sources.Count)
        {
            Reject("Das gezogene Element ist kein zuweisbares FEE-SimObject.");
            return;
        }
        if (!target.Model.AllowMultiSelect && feeObjectIds.Length > 1)
        {
            Reject($"'{target.DisplayName}' erlaubt nur eine SimObject-Zuordnung.");
            return;
        }

        foreach (var feeObjectId in feeObjectIds)
        {
            VisualAssignmentResult result = _planService.TryAssign(target.Id, feeObjectId);
            PublishIssues(result.Issues);
            if (!result.Success)
            {
                Reject(result.Message);
                return;
            }
        }

        StatusText = feeObjectIds.Length == 1
            ? $"FEE-SimObject wurde '{target.DisplayName}' zugeordnet."
            : $"{feeObjectIds.Length} FEE-SimObjects wurden gemeinsam '{target.DisplayName}' zugeordnet.";
        _log.Information(LogArea, StatusText);
    }

    private bool CanHandleDrop(ContainerToFeeVisualDropRequest? request)
    {
        if (IsBusy || request is null)
            return false;

        IReadOnlyList<object> sources = request.Source is IReadOnlyList<object> selectedItems
            ? selectedItems
            : new[] { request.Source };
        if (sources.All(item => item is ContainerToFeeVisualFeeSignalVM) &&
            request.Target is ContainerToFeeVisualTreeNodeVM signalTarget)
        {
            return signalTarget.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal ||
                   signalTarget.Kind == VisualNodeKind.Group &&
                   signalTarget.Id.EndsWith(":signals", StringComparison.Ordinal);
        }

        var target = request.Target switch
        {
            ContainerToFeeVisualTargetVM targetVm => targetVm,
            ContainerToFeeVisualTreeNodeVM treeNode when treeNode.Kind == VisualNodeKind.SimObjectTarget =>
                CreateTargetVm(treeNode.Id),
            ContainerToFeeVisualTreeNodeVM treeNode when treeNode.Kind == VisualNodeKind.Group &&
                                                            treeNode.Id.EndsWith(":simobjects", StringComparison.Ordinal) =>
                ResolveCompatibleTarget(treeNode, sources),
            _ => null,
        };
        if (target is null || (!target.Model.AllowMultiSelect && sources.Count > 1))
            return false;

        return sources.All(source => source switch
            {
                ContainerToFeeVisualFeeObjectVM feeObject => target.Model.CanAssign(feeObject.Model),
                ContainerToFeeVisualAssignmentVM assignment =>
                    string.Equals(target.AllowedTypeName, assignment.FeeObjectTypeName, StringComparison.Ordinal) ||
                    string.Equals(target.AllowedTypeName, assignment.FeeType, StringComparison.Ordinal),
                _ => false,
            });
    }

    private async Task SaveContainerXmlAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Bearbeitetes ContainerFile speichern",
            Filter = "Container XML (*.xml)|*.xml|Alle Dateien (*.*)|*.*",
            FileName = string.IsNullOrWhiteSpace(SourceXmlPath)
                ? "Container.container.xml"
                : $"{Path.GetFileNameWithoutExtension(SourceXmlPath)}.bearbeitet.container.xml",
            DefaultExt = ".xml",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true)
            return;

        await RunBusyAsync("Bearbeitetes ContainerFile wird gespeichert …", async cancellationToken =>
        {
            await _planService.SaveEffectiveContainerXmlAsync(dialog.FileName, cancellationToken);
            StatusText = $"ContainerFile mit Slotkorrekturen, zusätzlichen Signalen und der aktuellen Containerauswahl gespeichert: {dialog.FileName}";
            _log.Information(LogArea, StatusText);
        });
    }

    private ContainerToFeeVisualTargetVM? CreateTargetVm(string targetId)
    {
        var plan = _planService.CurrentPlan;
        var target = plan?.FindTarget(targetId);
        return plan is null || target is null
            ? null
            : new ContainerToFeeVisualTargetVM(
                target,
                plan.Assignments.Where(item => item.TargetId == target.Id),
                plan.IsCreationRequested(target.ContainerId),
                Issues.Where(issue => issue.NodeId == target.Id));
    }

    private ContainerToFeeVisualTargetVM? ResolveCompatibleTarget(
        ContainerToFeeVisualTreeNodeVM group,
        IReadOnlyList<object> sources)
    {
        var plan = _planService.CurrentPlan;
        if (plan is null || group.ContainerId is null)
            return null;
        var objects = sources.Select(source => source switch
            {
                ContainerToFeeVisualFeeObjectVM item => item.Model,
                ContainerToFeeVisualAssignmentVM item => _planService.DiscoveredFeeObjects.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, item.FeeObjectId, StringComparison.Ordinal)),
                _ => null,
            })
            .Where(item => item is not null)
            .Cast<VisualFeeObject>()
            .ToArray();
        if (objects.Length != sources.Count)
            return null;
        var compatible = plan.Targets
            .Where(target => string.Equals(target.ContainerId, group.ContainerId, StringComparison.Ordinal))
            .Where(target => objects.All(target.CanAssign))
            .Where(target => target.AllowMultiSelect || objects.Length == 1)
            .ToArray();
        return compatible.Length == 1 ? CreateTargetVm(compatible[0].Id) : null;
    }

    private IReadOnlyList<ContainerToFeeVisualTreeNodeVM> ResolveSignalDropTargets(
        ContainerToFeeVisualTreeNodeVM initialTarget,
        int count)
    {
        if (count <= 1)
            return [initialTarget];
        var assignedIds = _planService.CurrentPlan?.SignalAssignments
            .Select(item => item.SignalNodeId)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        return TreeRoots.SelectMany(root => root.SelfAndDescendants())
            .Where(node => node.Id == initialTarget.Id ||
                           (node.ContainerId == initialTarget.ContainerId &&
                            node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal &&
                            string.Equals(node.Slot, initialTarget.Slot, StringComparison.Ordinal) &&
                            !assignedIds.Contains(node.Id)))
            .OrderByDescending(node => node.Id == initialTarget.Id)
            .Take(count)
            .ToArray();
    }

    private void RemoveAssignment(ContainerToFeeVisualAssignmentVM? assignment)
    {
        if (assignment is null)
            return;

        VisualAssignmentResult result =
            _planService.RemoveAssignment(assignment.TargetId, assignment.FeeObjectId);
        PublishIssues(result.Issues);
        if (!result.Success)
        {
            Reject(result.Message);
            return;
        }

        StatusText = result.Message;
        _log.Information(LogArea, result.Message);
    }

    private void RemoveSignal(ContainerToFeeVisualSignalEntryVM? signal)
    {
        if (signal is null)
            return;
        var result = _planService.RemoveSignal(signal.NodeId);
        PublishIssues(result.Issues);
        if (!result.Success)
        {
            Reject(result.Message);
            return;
        }
        StatusText = result.Message;
        _log.Information(LogArea, result.Message);
    }

    private static bool CanDeleteTreeNode(ContainerToFeeVisualTreeNodeVM node) =>
        node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal or
            VisualNodeKind.SimObject or VisualNodeKind.SimObjectTarget or VisualNodeKind.Container;

    private void DeleteTreeNode(ContainerToFeeVisualTreeNodeVM? node)
    {
        if (node is null)
            return;
        var plan = _planService.CurrentPlan;
        if (plan is null)
            return;
        if (node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal)
        {
            RemoveSignal(new ContainerToFeeVisualSignalEntryVM(node.Model, plan));
            return;
        }
        if (node.Kind == VisualNodeKind.SimObject)
        {
            var assignment = plan.Assignments.FirstOrDefault(item => string.Equals(
                $"{item.TargetId}:assigned:{StableId.Encode(item.FeeObjectId)}",
                node.Id,
                StringComparison.Ordinal));
            if (assignment is not null)
                RemoveAssignment(new ContainerToFeeVisualAssignmentVM(
                    assignment,
                    plan.FindTarget(assignment.TargetId)?.AllowedTypeName ?? assignment.FeeObjectTypeName));
            return;
        }
        if (node.Kind == VisualNodeKind.SimObjectTarget)
        {
            foreach (var assignment in plan.Assignments
                         .Where(item => string.Equals(item.TargetId, node.Id, StringComparison.Ordinal))
                         .ToArray())
                _planService.RemoveAssignment(assignment.TargetId, assignment.FeeObjectId);
            StatusText = "Alle SimObject-Zuordnungen des ausgewählten Ziels wurden entfernt.";
            return;
        }
        if (node.Kind == VisualNodeKind.Container)
            SetGenerationSelected(node.Id, false);
    }

    private void ToggleTreeNodeGeneration(ContainerToFeeVisualTreeNodeVM? node)
    {
        var plan = _planService.CurrentPlan;
        var containerId = node?.Kind == VisualNodeKind.Container ? node.Id : node?.ContainerId;
        if (plan is null || containerId is null)
            return;
        SetGenerationSelected(containerId, !plan.IsGenerationSelected(containerId));
    }

    private void Undo()
    {
        if (_planService.Undo())
        {
            StatusText = "Letzte Zuordnungsänderung rückgängig gemacht.";
            _log.Information(LogArea, StatusText);
        }
    }

    private void Redo()
    {
        if (_planService.Redo())
        {
            StatusText = "Zuordnungsänderung wiederhergestellt.";
            _log.Information(LogArea, StatusText);
        }
    }

    private void CancelOperation()
    {
        _operationCancellation?.Cancel();
        StatusText = "Vorgang wird abgebrochen …";
        _log.Information(LogArea, StatusText);
    }

    private async Task RunBusyAsync(string status, Func<CancellationToken, Task> operation)
    {
        if (IsBusy)
            return;

        _operationCancellation = new CancellationTokenSource();
        IsBusy = true;
        StatusText = status;
        try
        {
            await operation(_operationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Vorgang abgebrochen.";
            _log.Information(LogArea, StatusText);
        }
        catch (Exception exception)
        {
            StatusText = "Vorgang fehlgeschlagen. Details stehen im Protokoll.";
            _log.Error(LogArea, StatusText, exception);
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            IsBusy = false;
        }
    }

    private void OnPlanChanged(object? sender, VisualPlanChangedEventArgs args)
    {
        void Apply() => ApplyPlan(args.Plan);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Apply();
        else
            dispatcher.BeginInvoke(Apply);
    }

    private void ApplyPlan(VisualPlan plan)
    {
        string? selectedNodeId = SelectedTreeNode?.Id;
        string? selectedTargetId = SelectedTarget?.Id;
        var expansionState = TreeRoots
            .SelectMany(root => root.SelfAndDescendants())
            .ToDictionary(node => node.Id, node => node.IsExpanded, StringComparer.Ordinal);
        if (!string.Equals(SourceXmlPath, plan.SourceXmlPath, StringComparison.OrdinalIgnoreCase))
            _verifiedContainerIds.Clear();
        _isApplyingPlan = true;
        try
        {
            _lastExecutionIssues = Array.Empty<VisualIssue>();
            SourceXmlPath = plan.SourceXmlPath;
            var validation = _planService.Validate();
            TreeRoots.ReplaceWith(plan.Roots.Select(node => BuildTree(node, plan, validation.Issues)));
            foreach (var node in TreeRoots.SelectMany(root => root.SelfAndDescendants()))
            {
                if (expansionState.TryGetValue(node.Id, out var wasExpanded))
                    node.IsExpanded = wasExpanded;
            }
            RefreshFeeObjectProjection(_planService.DiscoveredFeeObjects);
            RefreshFeeInterfaceProjection(_planService.DiscoveredFeeInterfaces);
            RefreshFeeSignalProjection(_planService.DiscoveredFeeSignals);
            ApplyDiscoveredContainerObjectStates(_planService.DiscoveredFeeContainerObjects);
            ApplyDiscoveredSignalStates(_planService.DiscoveredFeeSignals);
            ApplyVerifiedContainerStates(_verifiedContainerIds);
            PublishIssues(validation.Issues);
            ApplyTreeFilter();

            SelectedTreeNode = FindTreeNode(selectedNodeId) ?? TreeRoots.FirstOrDefault();
            SelectedTarget = Targets.FirstOrDefault(target => target.Id == selectedTargetId) ?? Targets.FirstOrDefault();
            SelectedExistingInterface = AvailableFeeInterfaces.FirstOrDefault(item =>
                string.Equals(
                    item.GuidString,
                    plan.ExistingInterfaceSelection?.InterfaceGuid,
                    StringComparison.OrdinalIgnoreCase)) ?? AvailableFeeInterfaces.FirstOrDefault(item => item.IsNone);
        }
        finally
        {
            _isApplyingPlan = false;
        }

        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(SidecarPath));
        OnPropertyChanged(nameof(ContainerCount));
        OnPropertyChanged(nameof(SelectedContainerCount));
        OnPropertyChanged(nameof(ObjectCount));
        OnPropertyChanged(nameof(EdgeCount));
        OnPropertyChanged(nameof(AssignmentCount));
        OnPropertyChanged(nameof(SelectedAssignmentCount));
        OnPropertyChanged(nameof(HasValidationErrors));
        OnPropertyChanged(nameof(IsFeeObjectDiscoveryAvailable));
        OnPropertyChanged(nameof(CanStartGeneration));
        OnPropertyChanged(nameof(CanLinkOnly));
        OnPropertyChanged(nameof(CanLinkSignalsOnly));
        OnPropertyChanged(nameof(RefreshFeeObjectsUnavailableReason));
        OnPropertyChanged(nameof(StartGenerationUnavailableReason));
        OnPropertyChanged(nameof(LinkOnlyUnavailableReason));
        OnPropertyChanged(nameof(LinkSignalsOnlyUnavailableReason));
        OnPropertyChanged(nameof(SelectedContainerSupportsCreation));
        OnPropertyChanged(nameof(IsCreationRequestedForSelection));
        InvalidateCommands();
    }

    private ContainerToFeeVisualTreeNodeVM BuildTree(
        VisualNode node,
        VisualPlan plan,
        IReadOnlyList<VisualIssue> issues)
    {
        IReadOnlyList<string> allowedSlots = [];
        if (node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal &&
            node.ContainerId is not null &&
            plan.FindNode(node.ContainerId) is { } containerNode &&
            ContainerMetadataCatalog.TryGet(containerNode.TypeName, out var descriptor))
        {
            allowedSlots = descriptor.Slots
                .OrderBy(slot => slot, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var childModels = node.Children
            .Where(child => !plan.IsSignalRemoved(child.Id))
            .Concat(plan.AddedSignals
                .Where(added => string.Equals(added.SignalGroupId, node.Id, StringComparison.Ordinal))
                .Select(added => plan.FindNode(added.NodeId))
                .Where(child => child is not null)
                .Cast<VisualNode>())
            .ToList();
        if (node.Kind == VisualNodeKind.SimObjectTarget)
        {
            childModels.AddRange(plan.Assignments
                .Where(assignment => string.Equals(assignment.TargetId, node.Id, StringComparison.Ordinal))
                .Select(assignment => new VisualNode(
                    $"{node.Id}:assigned:{StableId.Encode(assignment.FeeObjectId)}",
                    node.Id,
                    node.ContainerId,
                    VisualNodeKind.SimObject,
                    assignment.FeeObjectName,
                    assignment.FeeObjectTypeName,
                    null,
                    isTechnical: false)));
        }

        return new(
            node,
            childModels.Select(child => BuildTree(child, plan, issues)),
            plan.IsGenerationSelected(node.Id),
            node.Kind == VisualNodeKind.Container && ContainerMetadataCatalog.TryGet(node.TypeName, out _),
            plan.GetEffectiveSlot(node),
            allowedSlots,
            GetNodeState(node, plan),
            GetNodeConnectionDescription(node, plan),
            GetNodeErrors(node, plan, issues),
            SetGenerationSelected,
            SetSlotOverride);
    }

    private void SetSlotOverride(string signalNodeId, string slot)
    {
        if (!_planService.SetSlotOverride(signalNodeId, slot))
        {
            Reject("Die gewählte Slot-Zuordnung ist für diesen Containertyp nicht zulässig.");
            return;
        }

        StatusText = $"Slot-Zuordnung auf '{slot}' geändert. Die Quell-XML bleibt unverändert; Plan und Generierung verwenden den neuen Wert.";
        _log.Information(LogArea, StatusText);
    }

    private static IReadOnlyList<string> GetNodeErrors(
        VisualNode node,
        VisualPlan plan,
        IEnumerable<VisualIssue> issues) => issues
        .Where(issue => issue.Severity == VisualIssueSeverity.Error)
        .Where(issue =>
            string.Equals(issue.NodeId, node.Id, StringComparison.Ordinal) ||
            (node.Kind == VisualNodeKind.Container &&
             plan.FindTarget(issue.NodeId ?? string.Empty)?.ContainerId == node.Id))
        .Select(issue => $"[{issue.Code}] {issue.Message}")
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private static ContainerToFeeVisualNodeState GetNodeState(
        VisualNode node,
        VisualPlan plan)
    {
        var containerSelected = node.ContainerId is null || plan.IsGenerationSelected(node.ContainerId);
        if (!containerSelected)
            return ContainerToFeeVisualNodeState.Missing;

        if (node.Kind == VisualNodeKind.SimObjectTarget)
        {
            var assigned = plan.Assignments.Any(assignment => assignment.TargetId == node.Id);
            return assigned
                ? ContainerToFeeVisualNodeState.FoundUnlinked
                : plan.IsCreationRequested(node.ContainerId ?? string.Empty)
                    ? ContainerToFeeVisualNodeState.Planned
                    : ContainerToFeeVisualNodeState.Missing;
        }

        if (node.Kind == VisualNodeKind.SimObject)
            return ContainerToFeeVisualNodeState.Verified;

        if (node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal &&
            plan.SignalAssignments.Any(assignment => assignment.SignalNodeId == node.Id))
            return ContainerToFeeVisualNodeState.FoundUnlinked;

        if (node.Kind == VisualNodeKind.UnknownSignal)
            return ContainerToFeeVisualNodeState.Planned;

        if (node.Kind is VisualNodeKind.Group or VisualNodeKind.Root)
            return ContainerToFeeVisualNodeState.None;

        if (node.Kind != VisualNodeKind.Container)
            return node.ContainerId is not null && plan.IsGenerationSelected(node.ContainerId)
                ? ContainerToFeeVisualNodeState.Planned
                : ContainerToFeeVisualNodeState.None;

        var targets = plan.Targets.Where(target => target.ContainerId == node.Id).ToArray();
        if (targets.Length == 0)
            return plan.IsGenerationSelected(node.Id)
                ? ContainerToFeeVisualNodeState.Planned
                : ContainerToFeeVisualNodeState.None;

        var assignedTargetIds = plan.Assignments
            .Select(assignment => assignment.TargetId)
            .ToHashSet(StringComparer.Ordinal);
        return targets.All(target => assignedTargetIds.Contains(target.Id)) || plan.IsCreationRequested(node.Id)
            ? ContainerToFeeVisualNodeState.Planned
            : ContainerToFeeVisualNodeState.Missing;
    }

    private static string GetNodeConnectionDescription(VisualNode node, VisualPlan plan)
    {
        if (node.Kind == VisualNodeKind.Container)
        {
            var children = plan.Nodes.Where(item =>
                string.Equals(item.ContainerId, node.Id, StringComparison.Ordinal)).ToArray();
            if (children.Any(item => item.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal) &&
                children.All(item => item.Kind is not (VisualNodeKind.Logic or VisualNodeKind.SimObjectTarget or VisualNodeKind.TechnicalHelper)))
            {
                return "Signal-only-Container: Es werden Interface-Signale verarbeitet, aber keine FEE-Szenenobjekte oder Objektverknüpfungen erzeugt.";
            }
        }

        if (node.Kind == VisualNodeKind.SimObjectTarget)
        {
            var assignments = plan.Assignments
                .Where(assignment => assignment.TargetId == node.Id)
                .Select(assignment => assignment.FeeObjectName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return assignments.Length == 0
                ? plan.IsCreationRequested(node.ContainerId ?? string.Empty)
                    ? "Noch nicht vorhanden – wird neu erzeugt"
                    : "Nicht vorhanden und von der Erzeugung ausgeschlossen"
                : $"Verbundenes FEE-SimObject: {string.Join(", ", assignments)}";
        }

        if (node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal)
        {
            var assignment = plan.SignalAssignments.LastOrDefault(item => item.SignalNodeId == node.Id);
            return assignment is null
                ? node.Kind == VisualNodeKind.UnknownSignal
                    ? "Keine unterstützte Schnittstellenzuordnung – Unknown"
                    : "Noch kein vorhandenes FEE-Signal eindeutig zugeordnet"
                : $"Verbundenes FEE-Signal: {assignment.FeeSignalTag} · {assignment.FeeInterfaceName}";
        }

        if (node.Kind == VisualNodeKind.SimObject)
            return $"Vorhandenes FEE-SimObject: {node.Name}";

        return string.Empty;
    }

    private void ApplyDiscoveredSignalStates(IReadOnlyList<VisualFeeSignal> signals)
    {
        var selectedGuid = SelectedExistingInterface?.GuidString;
        signals = string.IsNullOrWhiteSpace(selectedGuid)
            ? []
            : signals.Where(signal => string.Equals(
                signal.InterfaceGuidString,
                selectedGuid,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        var selectedSignalGuids = signals
            .Select(signal => signal.GuidString)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var node in TreeRoots.SelectMany(root => root.SelfAndDescendants())
                     .Where(node => node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal))
        {
            if (_planService.CurrentPlan?.SignalAssignments.Any(assignment =>
                    assignment.SignalNodeId == node.Id &&
                    selectedSignalGuids.Contains(assignment.FeeSignalGuid)) == true)
            {
                node.ApplyExecutionState(
                    node.Kind == VisualNodeKind.UnknownSignal
                        ? ContainerToFeeVisualNodeState.Planned
                        : ContainerToFeeVisualNodeState.FoundUnlinked,
                    node.LinkedObjectDescription);
                continue;
            }
            var matches = signals.Where(signal => string.Equals(
                    signal.Tag,
                    node.Name,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var exactMatches = string.IsNullOrWhiteSpace(node.SourceLocation)
                ? matches
                : matches.Where(signal => string.Equals(
                    signal.Location,
                    node.SourceLocation,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            var state = (matches.Length, exactMatches.Length) switch
            {
                (0, _) => ContainerToFeeVisualNodeState.Planned,
                (_, 1) => ContainerToFeeVisualNodeState.FoundUnlinked,
                _ => ContainerToFeeVisualNodeState.Ambiguous,
            };
            var connection = exactMatches.Length == 1
                ? $"Gefundenes FEE-Signal: {exactMatches[0].Tag} · {exactMatches[0].InterfaceName} · {exactMatches[0].Location}"
                : matches.Length > 1
                    ? $"Mehrdeutig: {matches.Length} FEE-Signale mit Tag '{node.Name}' gefunden"
                    : node.Kind == VisualNodeKind.UnknownSignal
                        ? "Keine unterstützte Schnittstellenzuordnung – Unknown"
                        : "Noch nicht vorhanden – wird bei der Generierung angelegt";
            node.ApplyExecutionState(
                node.Kind == VisualNodeKind.UnknownSignal ? ContainerToFeeVisualNodeState.Planned : state,
                connection);
        }
        RefreshAggregateTreeStates();
    }

    private void ApplyDiscoveredContainerObjectStates(
        IReadOnlyList<VisualFeeContainerObject> objects)
    {
        var plan = _planService.CurrentPlan;
        if (plan is null)
            return;
        var presenceByNode = VisualFeeContainerPresenceResolver.Resolve(plan, objects);
        foreach (var node in TreeRoots.SelectMany(root => root.SelfAndDescendants()))
        {
            if (!presenceByNode.TryGetValue(node.Id, out var presence))
                continue;
            var state = presence.Kind switch
            {
                VisualFeeNodePresenceKind.Found => ContainerToFeeVisualNodeState.Verified,
                VisualFeeNodePresenceKind.Ambiguous => ContainerToFeeVisualNodeState.Ambiguous,
                _ => ContainerToFeeVisualNodeState.Planned,
            };
            node.ApplyExecutionState(state, presence.Description);
        }
        RefreshAggregateTreeStates();
    }

    private void MarkSelectedTreeNodesVerified()
    {
        var plan = _planService.CurrentPlan;
        if (plan is null)
            return;
        var selected = plan.Nodes
            .Where(node => node.Kind == VisualNodeKind.Container && plan.IsGenerationSelected(node.Id))
            .Select(node => node.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var node in TreeRoots.SelectMany(root => root.SelfAndDescendants())
                     .Where(node => node.ContainerId is not null && selected.Contains(node.ContainerId)))
            node.ApplyExecutionState(ContainerToFeeVisualNodeState.Verified);
        _verifiedContainerIds.UnionWith(selected);
        RefreshAggregateTreeStates();
    }

    private void ApplyVerifiedContainerStates(IReadOnlySet<string> verifiedContainerIds)
    {
        foreach (var node in TreeRoots.SelectMany(root => root.SelfAndDescendants())
                     .Where(node => node.ContainerId is not null &&
                                    verifiedContainerIds.Contains(node.ContainerId)))
            node.ApplyExecutionState(ContainerToFeeVisualNodeState.Verified);
        RefreshAggregateTreeStates();
    }

    private void SetGenerationSelected(string containerId, bool selected)
    {
        if (!_planService.SetGenerationSelected(containerId, selected))
            return;
        StatusText = selected
            ? "Container wurde für die FEE-Aktion ausgewählt."
            : "Container wurde von der FEE-Aktion ausgeschlossen.";
    }

    private ContainerToFeeVisualTreeNodeVM? FindTreeNode(string? id)
    {
        if (id is null)
            return null;

        return TreeRoots.SelectMany(root => root.SelfAndDescendants()).FirstOrDefault(node => node.Id == id);
    }

    private void RefreshSelectionProjection()
    {
        Targets.Clear();
        SelectedContainerSignals.Clear();
        VisibleEdges.Clear();

        VisualPlan? plan = _planService.CurrentPlan;
        string? containerId = SelectedTreeNode?.ContainerId;
        if (plan is null || string.IsNullOrWhiteSpace(containerId))
        {
            SelectedTarget = null;
            return;
        }

        foreach (VisualSimObjectTarget target in plan.Targets.Where(target => target.ContainerId == containerId))
        {
            var assignments = plan.Assignments.Where(assignment => assignment.TargetId == target.Id);
            Targets.Add(new ContainerToFeeVisualTargetVM(
                target,
                assignments,
                plan.IsCreationRequested(containerId),
                Issues.Where(issue => string.Equals(issue.NodeId, target.Id, StringComparison.Ordinal))));
        }

        foreach (var signal in plan.Nodes
                     .Where(node => string.Equals(node.ContainerId, containerId, StringComparison.Ordinal) &&
                                    node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal &&
                                    !plan.IsSignalRemoved(node.Id))
                     .OrderBy(node => plan.GetEffectiveSlot(node), StringComparer.OrdinalIgnoreCase)
                     .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase))
            SelectedContainerSignals.Add(new ContainerToFeeVisualSignalEntryVM(signal, plan));

        HashSet<string> nodeIds = plan.Nodes
            .Where(node => node.ContainerId == containerId)
            .Select(node => node.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (VisualEdge edge in plan.Edges.Where(edge =>
                     nodeIds.Contains(edge.SourceId) || nodeIds.Contains(edge.TargetId)))
            VisibleEdges.Add(ContainerToFeeVisualEdgeVM.Create(edge, plan));

        SelectedTarget = Targets.FirstOrDefault();
    }

    private void ApplyTreeFilter()
    {
        foreach (ContainerToFeeVisualTreeNodeVM root in TreeRoots)
            root.ApplyFilter(TreeFilter);
    }

    private bool FilterFeeObject(object item)
    {
        if (item is not ContainerToFeeVisualFeeObjectVM feeObject)
            return false;

        if (!string.IsNullOrWhiteSpace(FeeObjectFilter) &&
            !feeObject.Name.Contains(FeeObjectFilter, StringComparison.OrdinalIgnoreCase) &&
            !feeObject.FeeType.Contains(FeeObjectFilter, StringComparison.OrdinalIgnoreCase) &&
            !feeObject.TypeName.Contains(FeeObjectFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        return !ShowOnlyCompatibleFeeObjects ||
               SelectedTarget is null ||
               SelectedTarget.Model.CanAssign(feeObject.Model);
    }

    private bool FilterFeeSignal(object item)
    {
        if (item is not ContainerToFeeVisualFeeSignalVM signal)
            return false;
        if (SelectedExistingInterface?.Model is not { } selectedInterface ||
            !string.Equals(
                signal.Model.InterfaceGuidString,
                selectedInterface.GuidString,
                StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrWhiteSpace(FeeSignalFilter))
            return true;
        return signal.Tag.Contains(FeeSignalFilter, StringComparison.OrdinalIgnoreCase) ||
               signal.Location.Contains(FeeSignalFilter, StringComparison.OrdinalIgnoreCase) ||
               signal.InterfaceName.Contains(FeeSignalFilter, StringComparison.OrdinalIgnoreCase) ||
               signal.DataType.Contains(FeeSignalFilter, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshFeeSignalProjection(IReadOnlyList<VisualFeeSignal>? signals = null)
    {
        var source = signals ?? AvailableFeeSignals.Select(item => item.Model).ToArray();
        var plan = _planService.CurrentPlan;
        var selectedGuid = SelectedExistingInterface?.GuidString;
        var scopedSignals = string.IsNullOrWhiteSpace(selectedGuid)
            ? Array.Empty<VisualFeeSignal>()
            : source.Where(signal => string.Equals(
                signal.InterfaceGuidString,
                selectedGuid,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        AvailableFeeSignals.ReplaceWith(source.Select(signal =>
            new ContainerToFeeVisualFeeSignalVM(signal, scopedSignals, plan)));
        FeeSignalsView.Refresh();
    }

    private void RefreshFeeObjectProjection(IReadOnlyList<VisualFeeObject>? objects = null)
    {
        var plan = _planService.CurrentPlan;
        var source = objects ?? AvailableFeeObjects.Select(item => item.Model).ToArray();
        AvailableFeeObjects.ReplaceWith(
            source.Select(item => new ContainerToFeeVisualFeeObjectVM(item, plan)));
        FeeObjectsView.Refresh();
    }

    private void RefreshFeeInterfaceProjection(IReadOnlyList<VisualFeeInterface>? interfaces = null)
    {
        var selectedGuid = _planService.CurrentPlan?.ExistingInterfaceSelection?.InterfaceGuid;
        var source = interfaces ?? AvailableFeeInterfaces
            .Where(item => item.Model is not null)
            .Select(item => item.Model!)
            .ToArray();
        AvailableFeeInterfaces.ReplaceWith(
            new[] { ContainerToFeeVisualFeeInterfaceVM.None }
                .Concat(source.Select(item => new ContainerToFeeVisualFeeInterfaceVM(item))));
        SelectedExistingInterface = AvailableFeeInterfaces.FirstOrDefault(item => string.Equals(
            item.GuidString,
            selectedGuid,
            StringComparison.OrdinalIgnoreCase)) ?? AvailableFeeInterfaces.First(item => item.IsNone);
    }

    private void PublishIssues(IEnumerable<VisualIssue> issues)
    {
        var issueList = issues.ToArray();
        Issues.ReplaceWith(issueList);
        var plan = _planService.CurrentPlan;
        if (plan is not null)
        {
            foreach (var node in TreeRoots.SelectMany(root => root.SelfAndDescendants()))
                node.ApplyValidationErrors(GetNodeErrors(node.Model, plan, issueList));
            foreach (var target in Targets)
                target.ApplyValidationErrors(issueList.Where(issue =>
                    string.Equals(issue.NodeId, target.Id, StringComparison.Ordinal)));
            RefreshAggregateTreeStates();
        }
        OnPropertyChanged(nameof(HasValidationErrors));
        OnPropertyChanged(nameof(CanStartGeneration));
        OnPropertyChanged(nameof(CanLinkOnly));
        OnPropertyChanged(nameof(CanLinkSignalsOnly));
        OnPropertyChanged(nameof(StartGenerationUnavailableReason));
        OnPropertyChanged(nameof(LinkOnlyUnavailableReason));
        OnPropertyChanged(nameof(LinkSignalsOnlyUnavailableReason));
    }

    private void Reject(string message)
    {
        StatusText = message;
        _log.Warning(LogArea, message);
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not nameof(FeeConnectionService.IsConnected) and
            not nameof(FeeConnectionService.CanUseFeeFeatures))
            return;

        OnPropertyChanged(nameof(FeeUnavailableReason));
        OnPropertyChanged(nameof(IsFeeObjectDiscoveryAvailable));
        OnPropertyChanged(nameof(CanStartGeneration));
        OnPropertyChanged(nameof(CanLinkOnly));
        OnPropertyChanged(nameof(CanLinkSignalsOnly));
        OnPropertyChanged(nameof(RefreshFeeObjectsUnavailableReason));
        OnPropertyChanged(nameof(StartGenerationUnavailableReason));
        OnPropertyChanged(nameof(LinkOnlyUnavailableReason));
        OnPropertyChanged(nameof(LinkSignalsOnlyUnavailableReason));
        InvalidateCommands();
    }

    private string GetExecutionUnavailableReason(bool linkOnly)
    {
        if (IsBusy)
            return "Ein Container2FEE-Vorgang läuft bereits.";
        if (!HasPlan)
            return "Zuerst eine Container-XML oder einen gespeicherten Plan laden.";
        if (!Connection.CanUseFeeFeatures)
            return Connection.UnavailableReason;
        var blockingIssue = Issues.FirstOrDefault(issue => issue.Severity == VisualIssueSeverity.Error);
        if (blockingIssue is not null)
            return linkOnly
                ? blockingIssue.Message
                : $"Fehler vorhanden: {blockingIssue.Message} Beim Start ist eine ausdrückliche Bestätigung erforderlich.";
        if (SelectedContainerCount == 0)
            return "Mindestens einen unterstützten Container auswählen.";
        if (linkOnly && SelectedAssignmentCount == 0)
            return "Mindestens einem Ziel eines ausgewählten Containers ein vorhandenes FEE-SimObject zuordnen.";
        return linkOnly
            ? "Verknüpft nur vorhandene SimObjects; eine Interface-Auswahl ist dafür nicht erforderlich."
            : string.Empty;
    }

    private void InvalidateCommands() => CommandManager.InvalidateRequerySuggested();

    private void RefreshAggregateTreeStates()
    {
        foreach (var root in TreeRoots)
            RefreshAggregateState(root);
    }

    private static ContainerToFeeVisualNodeState RefreshAggregateState(
        ContainerToFeeVisualTreeNodeVM node)
    {
        var childStates = node.Children.Select(RefreshAggregateState).ToArray();
        if (node.Kind is not (VisualNodeKind.Container or VisualNodeKind.Group or VisualNodeKind.Root) ||
            childStates.Length == 0)
        {
            return node.EffectiveState;
        }

        if (node.Kind == VisualNodeKind.Container)
        {
            var descendants = node.SelfAndDescendants().Skip(1).ToArray();
            var isSignalOnly = descendants.Any(child => child.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal) &&
                               descendants.All(child => child.Kind is not (VisualNodeKind.Logic or VisualNodeKind.SimObjectTarget or VisualNodeKind.TechnicalHelper));
            if (isSignalOnly)
            {
                node.ApplyExecutionState(ContainerToFeeVisualNodeState.Planned);
                return node.EffectiveState;
            }
        }

        var aggregate = childStates.Any(state => state.Kind == ContainerToFeeVisualNodeStateKind.Missing)
            ? ContainerToFeeVisualNodeState.Missing
            : childStates.Any(state => state.Kind is ContainerToFeeVisualNodeStateKind.Planned or ContainerToFeeVisualNodeStateKind.None)
                ? ContainerToFeeVisualNodeState.Planned
                : childStates.Any(state => state.Kind == ContainerToFeeVisualNodeStateKind.FoundUnlinked)
                    ? ContainerToFeeVisualNodeState.FoundUnlinked
                : ContainerToFeeVisualNodeState.Verified;
        node.ApplyExecutionState(aggregate);
        return node.EffectiveState;
    }

    private static FeeConnectionService ResolveConnection() =>
        Services.Connection ?? new FeeConnectionService();

}

public enum ContainerToFeeVisualNodeStateKind
{
    None,
    Verified,
    FoundUnlinked,
    Planned,
    Missing,
}

public sealed record ContainerToFeeVisualNodeState(
    ContainerToFeeVisualNodeStateKind Kind,
    string Background,
    string Description)
{
    public static ContainerToFeeVisualNodeState None { get; } =
        new(ContainerToFeeVisualNodeStateKind.None, "Transparent", string.Empty);
    public static ContainerToFeeVisualNodeState Verified { get; } =
        new(ContainerToFeeVisualNodeStateKind.Verified, "#FFC6EFCE", "In FEE eindeutig gefunden oder in dieser Sitzung erfolgreich erzeugt.");
    public static ContainerToFeeVisualNodeState FoundUnlinked { get; } =
        new(ContainerToFeeVisualNodeStateKind.FoundUnlinked, "#FFE8D9F3", "In FEE gefunden beziehungsweise ausgewählt, aber die erforderliche Verknüpfung ist noch nicht als ausgeführt bestätigt.");
    public static ContainerToFeeVisualNodeState Planned { get; } =
        new(ContainerToFeeVisualNodeStateKind.Planned, "#FFFFF2CC", "Wird bei der nächsten Generierung erzeugt oder vervollständigt.");
    public static ContainerToFeeVisualNodeState Missing { get; } =
        new(ContainerToFeeVisualNodeStateKind.Missing, "#FFEF9A9A", "Ein benötigtes Objekt fehlt, ist fehlerhaft oder wird nicht generiert.");
    public static ContainerToFeeVisualNodeState Ambiguous { get; } =
        new(ContainerToFeeVisualNodeStateKind.Missing, "#FFEF9A9A", "Mehrere widersprüchliche FEE-Treffer gefunden; eindeutige Zuordnung erforderlich.");
}

public sealed class ContainerToFeeVisualTreeNodeVM : NotifyBase
{
    private bool _isExpanded;
    private bool _isVisible = true;
    private bool _isGenerationSelected;
    private readonly Action<string, bool> _setGenerationSelected;
    private readonly Action<string, string> _setSlotOverride;
    private readonly bool _canSelectGeneration;
    private IReadOnlyList<string> _validationErrors = Array.Empty<string>();
    private ContainerToFeeVisualNodeState _executionState;

    public ContainerToFeeVisualTreeNodeVM(
        VisualNode model,
        IEnumerable<ContainerToFeeVisualTreeNodeVM> children,
        bool isGenerationSelected,
        bool canSelectGeneration,
        string effectiveSlot,
        IReadOnlyList<string> allowedSlots,
        ContainerToFeeVisualNodeState simObjectState,
        string linkedObjectDescription,
        IEnumerable<string> validationErrors,
        Action<string, bool> setGenerationSelected,
        Action<string, string> setSlotOverride)
    {
        Model = model;
        Children = new ObservableCollection<ContainerToFeeVisualTreeNodeVM>(children);
        _isGenerationSelected = isGenerationSelected;
        _canSelectGeneration = canSelectGeneration;
        _executionState = simObjectState;
        _linkedObjectDescription = linkedObjectDescription;
        _validationErrors = validationErrors.ToArray();
        _setGenerationSelected = setGenerationSelected;
        _setSlotOverride = setSlotOverride;
        _slot = effectiveSlot;
        AllowedSlots = allowedSlots;
        _isExpanded = !model.IsTechnical && model.Kind is VisualNodeKind.Root or VisualNodeKind.Container;
    }

    public VisualNode Model { get; }
    public string Id => Model.Id;
    public string? ContainerId => Model.ContainerId;
    public string Name => Model.Name;
    public string TypeName => Model.TypeName;
    private string _slot;
    public string Slot
    {
        get => _slot;
        set
        {
            if (!CanEditSlot || string.IsNullOrWhiteSpace(value) ||
                string.Equals(_slot, value, StringComparison.Ordinal))
                return;
            _setSlotOverride(Id, value);
        }
    }
    public IReadOnlyList<string> AllowedSlots { get; }
    public bool CanEditSlot => Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal &&
                               AllowedSlots.Count > 0;
    public string SourceLocation => Model.SourceLocation;
    public VisualNodeKind Kind => Model.Kind;
    public bool IsTechnical => Model.IsTechnical;
    public bool SupportsCreation => Model.SupportsCreation;
    public bool CanSelectGeneration => _canSelectGeneration;
    public ContainerToFeeVisualNodeState SimObjectState => _executionState;
    public ContainerToFeeVisualNodeState EffectiveState => HasValidationError
        ? ContainerToFeeVisualNodeState.Missing
        : _executionState;
    public string StateBackground => HasValidationError ? "#FFFFCDD2" : _executionState.Background;
    public string SimObjectStateDescription => _executionState.Description;
    private string _linkedObjectDescription;
    public string LinkedObjectDescription => _linkedObjectDescription;
    public IReadOnlyList<string> ValidationErrors => _validationErrors;
    public bool HasValidationError => ValidationErrors.Count > 0;
    public string ValidationErrorText => string.Join(Environment.NewLine, ValidationErrors);
    public string ExecutionDescription => Kind switch
    {
        VisualNodeKind.Container =>
            $"Erzeugt den Container „{Name}“ mit der Containerlogik „{TypeName}“, den benötigten Objekten und Signal-Slot-Verknüpfungen.",
        VisualNodeKind.SimObjectTarget =>
            $"Sucht ein kompatibles FEE-SimObject für „{Name}“ und verknüpft es mit diesem Logikziel.",
        VisualNodeKind.Signal =>
            $"Sucht das Interface-Signal „{Name}“; fehlt es, wird es im Grob Generation Interface erzeugt und dem angegebenen Slot zugewiesen.",
        VisualNodeKind.Logic => $"Erzeugt bzw. verwendet die Logik „{Name}“ vom Typ „{TypeName}“.",
        VisualNodeKind.BasicFrame => $"Erzeugt den BasicFrame „{Name}“ als Strukturknoten.",
        _ => $"Planobjekt „{Name}“ ({TypeName})."
    };

    public void ApplyValidationErrors(IEnumerable<string> errors)
    {
        var next = errors.Distinct(StringComparer.Ordinal).ToArray();
        if (_validationErrors.SequenceEqual(next, StringComparer.Ordinal))
            return;
        _validationErrors = next;
        OnPropertyChanged(nameof(ValidationErrors));
        OnPropertyChanged(nameof(HasValidationError));
        OnPropertyChanged(nameof(ValidationErrorText));
        OnPropertyChanged(nameof(EffectiveState));
        OnPropertyChanged(nameof(StateBackground));
    }

    public void ApplyExecutionState(
        ContainerToFeeVisualNodeState state,
        string? linkedObjectDescription = null)
    {
        if (!Equals(_executionState, state))
        {
            _executionState = state;
            OnPropertyChanged(nameof(SimObjectState));
            OnPropertyChanged(nameof(EffectiveState));
            OnPropertyChanged(nameof(StateBackground));
            OnPropertyChanged(nameof(SimObjectStateDescription));
        }
        if (linkedObjectDescription is not null &&
            !string.Equals(_linkedObjectDescription, linkedObjectDescription, StringComparison.Ordinal))
        {
            _linkedObjectDescription = linkedObjectDescription;
            OnPropertyChanged(nameof(LinkedObjectDescription));
        }
    }
    public ObservableCollection<ContainerToFeeVisualTreeNodeVM> Children { get; }

    public bool IsGenerationSelected
    {
        get => _isGenerationSelected;
        set
        {
            if (!CanSelectGeneration || !SetPropertyChange(ref _isGenerationSelected, value))
                return;
            _setGenerationSelected(Id, value);
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetPropertyChange(ref _isExpanded, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        private set => SetPropertyChange(ref _isVisible, value);
    }

    public IEnumerable<ContainerToFeeVisualTreeNodeVM> SelfAndDescendants()
    {
        yield return this;
        foreach (ContainerToFeeVisualTreeNodeVM child in Children)
        foreach (ContainerToFeeVisualTreeNodeVM descendant in child.SelfAndDescendants())
            yield return descendant;
    }

    public bool ApplyFilter(string filter)
    {
        bool childMatches = Children.Aggregate(false, (match, child) => child.ApplyFilter(filter) || match);
        bool selfMatches = string.IsNullOrWhiteSpace(filter) ||
                           Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                           TypeName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                           Slot.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                           SourceLocation.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                           LinkedObjectDescription.Contains(filter, StringComparison.OrdinalIgnoreCase);
        IsVisible = selfMatches || childMatches;
        if (!string.IsNullOrWhiteSpace(filter) && childMatches)
            IsExpanded = true;
        return IsVisible;
    }
}

public sealed class ContainerToFeeVisualTargetVM : MvvmBase
{
    private IReadOnlyList<string> _validationErrors = Array.Empty<string>();
    public ContainerToFeeVisualTargetVM(
        VisualSimObjectTarget model,
        IEnumerable<VisualAssignment> assignments,
        bool isCreationRequested,
        IEnumerable<VisualIssue> issues)
    {
        Model = model;
        Assignments = new ObservableCollection<ContainerToFeeVisualAssignmentVM>(
            assignments.Select(assignment => new ContainerToFeeVisualAssignmentVM(assignment, model.AllowedTypeName)));
        IsCreationRequested = isCreationRequested;
        _validationErrors = issues
            .Where(issue => issue.Severity == VisualIssueSeverity.Error)
            .Select(issue => $"[{issue.Code}] {issue.Message}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public VisualSimObjectTarget Model { get; }
    public string Id => Model.Id;
    public string DisplayName => Model.DisplayName;
    public string AllowedTypeName => Model.AllowedTypeName;
    public string SelectionMode => Model.AllowMultiSelect ? "Mehrfachauswahl" : "Einzelauswahl";
    public ObservableCollection<ContainerToFeeVisualAssignmentVM> Assignments { get; }
    public bool IsAssigned => Assignments.Count > 0;
    public bool IsCreationRequested { get; }
    public IReadOnlyList<string> ValidationErrors => _validationErrors;
    public bool HasValidationError => ValidationErrors.Count > 0;
    public string ValidationErrorText => string.Join(Environment.NewLine, ValidationErrors);
    public string AssignmentState => IsAssigned
        ? "Vorhandenes FEE-SimObject zugeordnet"
        : IsCreationRequested
            ? "Wird bei der Generierung erzeugt"
            : "Simulationsobjekt fehlt – Zuordnung erforderlich";
    public string StateBackground => HasValidationError
        ? "#FFFFCDD2"
        : IsAssigned
        ? "#FFC6EFCE"
        : IsCreationRequested
            ? "#FFFFF2CC"
            : "#FFEF9A9A";
    public string ExecutionDescription => IsAssigned
        ? $"Verwendet {Assignments.Count} vorhandene(s) FEE-SimObject(s) und schreibt die Zuordnung zum Ziel „{DisplayName}“ ({AllowedTypeName})."
        : IsCreationRequested
            ? $"Erzeugt ein neues kompatibles FEE-SimObject vom Typ „{AllowedTypeName}“ und verknüpft es mit „{DisplayName}“."
            : $"Sucht ein vorhandenes FEE-SimObject vom Typ „{AllowedTypeName}“ für die Verknüpfung mit „{DisplayName}“.";
    public string ToolTipText => HasValidationError
        ? ExecutionDescription + Environment.NewLine + ValidationErrorText
        : ExecutionDescription;

    public void ApplyValidationErrors(IEnumerable<VisualIssue> issues)
    {
        var next = issues
            .Where(issue => issue.Severity == VisualIssueSeverity.Error)
            .Select(issue => $"[{issue.Code}] {issue.Message}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (_validationErrors.SequenceEqual(next, StringComparer.Ordinal))
            return;
        _validationErrors = next;
        OnPropertyChanged(nameof(ValidationErrors));
        OnPropertyChanged(nameof(HasValidationError));
        OnPropertyChanged(nameof(ValidationErrorText));
        OnPropertyChanged(nameof(StateBackground));
        OnPropertyChanged(nameof(ToolTipText));
    }
}

/// <summary>Presentation wrapper for one existing FEE interface.</summary>
public sealed class ContainerToFeeVisualFeeInterfaceVM(VisualFeeInterface? model)
{
    public static ContainerToFeeVisualFeeInterfaceVM None { get; } = new(null);

    public VisualFeeInterface? Model { get; } = model;
    public bool IsNone => Model is null;
    public string GuidString => Model?.GuidString ?? string.Empty;
    public string Name => Model?.Name ?? "Keins";
    public int SignalCount => Model?.SignalCount ?? 0;
    public string DisplayName => IsNone ? "Keins" : $"{Name} ({SignalCount} Signale)";
}

/// <summary>Presentation state showing whether an FEE object is already assigned.</summary>
public sealed class ContainerToFeeVisualFeeObjectVM
{
    public ContainerToFeeVisualFeeObjectVM(VisualFeeObject model, VisualPlan? plan)
    {
        Model = model;
        var assignments = plan?.Assignments
            .Where(assignment => assignment.FeeObjectId == model.Id)
            .ToArray() ?? [];
        AssignedTargets = assignments
            .Select(assignment => DescribeAssignment(plan, assignment))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public VisualFeeObject Model { get; }
    public string Id => Model.Id;
    public string Name => Model.Name;
    public string TypeName => Model.TypeName;
    public string FeeType => Model.FeeType;
    public IReadOnlyList<string> AssignedTargets { get; }
    public bool IsAssigned => AssignedTargets.Count > 0;
    public string AssignmentText => IsAssigned
        ? $"Verknüpft mit: {string.Join("; ", AssignedTargets)}"
        : "Noch nicht zugeordnet";
    public string StateBackground => IsAssigned ? "#FFC6EFCE" : "Transparent";

    private static string DescribeAssignment(VisualPlan? plan, VisualAssignment assignment)
    {
        var target = plan?.FindTarget(assignment.TargetId);
        if (target is null)
            return assignment.TargetId;

        var container = plan?.FindNode(target.ContainerId);
        var logic = plan?.Nodes.FirstOrDefault(node =>
            node.ContainerId == target.ContainerId && node.Kind == VisualNodeKind.Logic);
        var logicOrContainer = logic?.Name ?? container?.TypeName ?? "—";
        return $"Ziel: {target.DisplayName} | Container: {container?.Name ?? "—"} | Logik/Typ: {logicOrContainer}";
    }
}

/// <summary>Presentation state for a discovered FEE signal and its plan usage.</summary>
public sealed class ContainerToFeeVisualFeeSignalVM
{
    public ContainerToFeeVisualFeeSignalVM(
        VisualFeeSignal model,
        IReadOnlyCollection<VisualFeeSignal> allSignals,
        VisualPlan? plan)
    {
        Model = model;
        var assignments = plan?.SignalAssignments
            .Where(item => string.Equals(
                item.FeeSignalGuid,
                model.GuidString,
                StringComparison.OrdinalIgnoreCase))
            .ToArray() ?? [];
        AssignedTargets = assignments
            .Select(item => DescribeSignalAssignment(plan, item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        HasDuplicateName = !string.IsNullOrWhiteSpace(model.Tag) && allSignals
            .Where(item => string.Equals(item.Tag, model.Tag, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.GuidString)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() > 1;
        HasDuplicateAssignment = assignments
            .Select(item => item.SignalNodeId)
            .Distinct(StringComparer.Ordinal)
            .Count() > 1;
    }

    public VisualFeeSignal Model { get; }
    public string GuidString => Model.GuidString;
    public string InterfaceName => Model.InterfaceName;
    public string Tag => Model.Tag;
    public string Location => Model.Location;
    public string DataType => Model.DataType;
    public string Usage => Model.Usage;
    public IReadOnlyList<string> AssignedTargets { get; }
    public bool IsAssigned => AssignedTargets.Count > 0;
    public bool HasDuplicateName { get; }
    public bool HasDuplicateAssignment { get; }
    public bool HasError => HasDuplicateName || HasDuplicateAssignment;
    public string AssignmentText => HasError
        ? string.Join(" · ", new[]
        {
            HasDuplicateName ? "Fehler: Signalname ist im FEE mehrfach vorhanden" : null,
            HasDuplicateAssignment ? "Fehler: Signal ist mehreren Container-Einträgen zugeordnet" : null,
        }.Where(item => item is not null))
        : IsAssigned
            ? $"Zugewiesen: {string.Join("; ", AssignedTargets)}"
            : "Noch nicht zugewiesen";
    public string StateBackground => HasError
        ? "#FFFFCDD2"
        : IsAssigned ? "#FFC6EFCE" : "#FFF3F5F7";
    public string ToolTipText =>
        $"Signal-GUID: {GuidString}{Environment.NewLine}{AssignmentText}";

    private static string DescribeSignalAssignment(VisualPlan? plan, VisualSignalAssignment assignment)
    {
        var node = plan?.FindNode(assignment.SignalNodeId);
        if (node is null)
            return assignment.SignalNodeId;
        var container = node.ContainerId is null ? null : plan?.FindNode(node.ContainerId);
        return $"{container?.Name ?? "—"} / {node.Name} [{plan?.GetEffectiveSlot(node) ?? node.Slot}]";
    }
}

public sealed class ContainerToFeeVisualAssignmentVM
{
    public ContainerToFeeVisualAssignmentVM(VisualAssignment model, string feeType)
    {
        Model = model;
        FeeType = feeType;
    }

    public VisualAssignment Model { get; }
    public string TargetId => Model.TargetId;
    public string FeeObjectId => Model.FeeObjectId;
    public string FeeObjectName => Model.FeeObjectName;
    public string FeeObjectTypeName => Model.FeeObjectTypeName;
    public string FeeType { get; }
}

public sealed class ContainerToFeeVisualSignalEntryVM
{
    public ContainerToFeeVisualSignalEntryVM(VisualNode model, VisualPlan plan)
    {
        Model = model;
        Slot = plan.GetEffectiveSlot(model);
        IsAdded = plan.IsAddedSignal(model.Id);
        var assignment = plan.SignalAssignments.LastOrDefault(item =>
            string.Equals(item.SignalNodeId, model.Id, StringComparison.Ordinal));
        AssignedFeeSignal = assignment is null
            ? "Keine vorhandene FEE-Zuordnung"
            : $"{assignment.FeeSignalTag} · {assignment.FeeInterfaceName}";
    }

    public VisualNode Model { get; }
    public string NodeId => Model.Id;
    public string Name => Model.Name;
    public string Slot { get; }
    public string SourceLocation => Model.SourceLocation;
    public bool IsAdded { get; }
    public string AssignedFeeSignal { get; }
    public string RemoveToolTip => IsAdded
        ? "Zusätzliches Signal vollständig aus dem bearbeiteten ContainerFile entfernen"
        : "Signal aus dem wirksamen ContainerFile entfernen; die Quelldatei bleibt unverändert und Rückgängig stellt es wieder her";
}

/// <summary>Readable presentation of one technical plan edge without exposing internal IDs.</summary>
public sealed record ContainerToFeeVisualEdgeVM(
    string Kind,
    string Source,
    string Target,
    string Description)
{
    public static ContainerToFeeVisualEdgeVM Create(VisualEdge edge, VisualPlan plan)
    {
        var source = DescribeNode(edge.SourceId, plan);
        var target = DescribeNode(edge.TargetId, plan);
        var kind = edge.Kind switch
        {
            VisualEdgeKind.ParentChild => "Struktur",
            VisualEdgeKind.SignalToSlot => "Signal → Slot",
            VisualEdgeKind.SlotToSlot => "Objekt → Logik",
            VisualEdgeKind.SimObjectAssignment => "FEE-Zuordnung",
            _ => edge.Kind.ToString(),
        };
        return new ContainerToFeeVisualEdgeVM(kind, source, target, edge.Label);
    }

    private static string DescribeNode(string id, VisualPlan plan)
    {
        var node = plan.FindNode(id);
        if (node is not null)
            return string.IsNullOrWhiteSpace(node.Slot) ? node.Name : $"{node.Name} [{node.Slot}]";
        var target = plan.FindTarget(id);
        if (target is not null)
            return target.DisplayName;
        var assignment = plan.Assignments.FirstOrDefault(item => item.FeeObjectId == id);
        return assignment?.FeeObjectName ?? "Externes FEE-Objekt";
    }
}
