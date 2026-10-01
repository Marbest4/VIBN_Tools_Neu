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
using VIBN_Tools.Core.Diagnostics;
using VIBN_Tools.SharedWpf.Commands;
using VIBN_Tools.Settings;
using VIBN_Tools.Quality;

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
    private static readonly TimeSpan DeleteFeeObjectResponseTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan FeeDisconnectResponseTimeout = TimeSpan.FromSeconds(2);
    private readonly ContainerToFeeVisualPlanService _planService;
    private readonly ApplicationLogService _log;
    private readonly FeeConnectionService _connection;
    private CancellationTokenSource? _operationCancellation;
    private Task? _cancelledOperationFinishing;
    private ContainerToFeeVisualTreeNodeVM? _selectedTreeNode;
    private ContainerToFeeVisualTargetVM? _selectedTarget;
    private ContainerToFeeVisualFeeInterfaceVM? _selectedExistingInterface;
    private ContainerToFeeVisualFeeObjectVM? _selectedFeeObject;
    private ContainerToFeeVisualFeeSignalVM? _selectedFeeSignal;
    private ContainerToFeeVisualSignalSlotVM? _selectedSignalSlot;
    private string? _selectedSignalOnlyContainerType;
    private VisualIssue? _selectedIssue;
    private bool _synchronizeRelatedSelections = true;
    private bool _isSynchronizingSelections;
    private string _treeFilter = string.Empty;
    private string _feeObjectFilter = string.Empty;
    private string _feeSignalFilter = string.Empty;
    private VisualStatusFilterOption _selectedTreeStatusFilter = VisualStatusFilterOption.All;
    private VisualTreeSortOption _selectedTreeSort = VisualTreeSortOption.ByType;
    private VisualStatusFilterOption _selectedFeeObjectStatusFilter = VisualStatusFilterOption.All;
    private VisualStatusFilterOption _selectedFeeSignalStatusFilter = VisualStatusFilterOption.All;
    private bool _showOnlyCompatibleFeeObjects;
    private bool _showFeeObjectDetails;
    private bool _showFeeSignalDetails;
    private bool _isBusy;
    private string _statusText = "Container-XML öffnen, um eine Vorschau zu erstellen.";
    private string _sourceXmlPath = string.Empty;
    private bool _isApplyingPlan;
    private bool _isRefreshingFeeInterfaceProjection;
    private IReadOnlyList<VisualIssue> _lastExecutionIssues = Array.Empty<VisualIssue>();
    private int _generationProgress;
    private string _generationProgressText = string.Empty;
    private string _feeRefreshHint =
        "Nach Änderungen im FEE-Projekt zuerst 'FEE aktualisieren'. Fehlen danach erwartete Objekte, einmal Model Validation ausführen und anschließend erneut aktualisieren.";
    private readonly HashSet<string> _verifiedContainerIds = new(StringComparer.Ordinal);
    private readonly GenerationManifestStore _manifestStore = new();
    private readonly GenerationManifestBuilder _manifestBuilder = new();
    private readonly HashSet<string> _selectedInterfaceGuids = new(StringComparer.OrdinalIgnoreCase);
    private string _lastManifestSummary = "Noch kein Generierungsmanifest für diesen Plan erstellt.";
    private string _lastManifestPath = string.Empty;

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
        HardAbortFeeCommand = new AsyncRelayCommand(
            HardAbortFeeConnectionAsync,
            () => IsBusy || HasPendingFeeSdkOperation);
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
        ConfirmDuplicateFeeObjectCommand = new RelayCommand<ContainerToFeeVisualTreeNodeVM>(
            ConfirmDuplicateFeeObject,
            node => node?.CanConfirmDuplicate == true && !IsBusy);
        DeleteFeeObjectCommand = new RelayCommand<ContainerToFeeVisualFeeObjectVM>(
            item => _ = DeleteFeeObjectAsync(item),
            item => item is not null && !IsBusy && Connection.CanUseFeeFeatures);
        ResumeLastGenerationCommand = new RelayCommand(
            ResumeLastGeneration,
            () => HasPlan && !IsBusy);

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

    public ObservableCollection<ContainerToFeeVisualSignalSlotVM> SignalSlots { get; } = new();

    public ObservableCollection<ContainerToFeeVisualEdgeVM> VisibleEdges { get; } = new();

    public ObservableCollection<ContainerToFeeVisualOperationDetailVM> OperationDetails { get; } = new();

    public ObservableCollection<ContainerToFeeVisualFeeObjectVM> AvailableFeeObjects { get; } = new();

    public ObservableCollection<ContainerToFeeVisualFeeInterfaceVM> AvailableFeeInterfaces { get; } = new();

    public ObservableCollection<ContainerToFeeVisualFeeSignalVM> AvailableFeeSignals { get; } = new();

    public ObservableCollection<VisualIssue> Issues { get; } = new();

    public ICollectionView FeeObjectsView { get; }
    public ICollectionView FeeSignalsView { get; }

    public IReadOnlyList<VisualStatusFilterOption> TreeStatusFilters { get; } =
        VisualStatusFilterOption.TreeOptions;
    public IReadOnlyList<VisualTreeSortOption> TreeSortOptions { get; } =
        VisualTreeSortOption.Options;
    public IReadOnlyList<VisualStatusFilterOption> FeeObjectStatusFilters { get; } =
        VisualStatusFilterOption.AssignmentOptions;
    public IReadOnlyList<VisualStatusFilterOption> FeeSignalStatusFilters { get; } =
        VisualStatusFilterOption.SignalOptions;

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

    public ICommand HardAbortFeeCommand { get; }

    public ICommand UndoCommand { get; }

    public ICommand RedoCommand { get; }

    public ICommand DropCommand { get; }

    public ICommand RemoveAssignmentCommand { get; }

    public ICommand RemoveSignalCommand { get; }

    public ICommand DeleteTreeNodeCommand { get; }

    public ICommand ToggleTreeNodeGenerationCommand { get; }

    public ICommand ConfirmDuplicateFeeObjectCommand { get; }

    public ICommand DeleteFeeObjectCommand { get; }

    public ICommand ResumeLastGenerationCommand { get; }

    public string LastManifestSummary
    {
        get => _lastManifestSummary;
        private set { _lastManifestSummary = value; OnPropertyChanged(); }
    }

    public string LastManifestPath
    {
        get => _lastManifestPath;
        private set { _lastManifestPath = value; OnPropertyChanged(); }
    }

    public bool HasPlan => _planService.CurrentPlan is not null;

    private bool HasPendingFeeSdkOperation => _cancelledOperationFinishing is { IsCompleted: false };

    public bool HasValidationErrors => Issues.Any(issue => issue.Severity == VisualIssueSeverity.Error);

    public bool IsFeeObjectDiscoveryAvailable => Connection.CanUseFeeFeatures && HasPlan && !IsBusy;

    public bool CanStartGeneration => HasPlan && Connection.CanUseFeeFeatures && !IsBusy &&
                                      SelectedContainerCount > 0;

    public bool CanLinkOnly => HasPlan && Connection.CanUseFeeFeatures && !IsBusy &&
                               !HasValidationErrors && SelectedAssignmentCount > 0;

    public bool CanLinkSignalsOnly => HasPlan && Connection.CanUseFeeFeatures && !IsBusy &&
                                      HasSelectedExistingInterfaces;

    public bool HasSelectedExistingInterfaces =>
        AvailableFeeInterfaces.Any(item => item.IsSelected);

    public string SelectedExistingInterfacesSummary => HasSelectedExistingInterfaces
        ? $"{AvailableFeeInterfaces.Count(item => item.IsSelected)} Interface(s) werden durchsucht"
        : "Kein Interface ausgewählt";

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
                : !HasSelectedExistingInterfaces
                    ? "Mindestens ein vorhandenes Interface auswählen."
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

    public bool ShowFeeObjectDetails
    {
        get => _showFeeObjectDetails;
        set
        {
            if (_showFeeObjectDetails == value)
                return;
            _showFeeObjectDetails = value;
            OnPropertyChanged();
        }
    }

    public bool ShowFeeSignalDetails
    {
        get => _showFeeSignalDetails;
        set
        {
            if (_showFeeSignalDetails == value)
                return;
            _showFeeSignalDetails = value;
            OnPropertyChanged();
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

    public IReadOnlyList<string> SignalOnlyContainerTypes => _planService.SupportedContainerTypes;

    public bool CanClassifySignalOnlyContainer =>
        _planService.CanClassifySignalOnlyContainer(SelectedTreeNode?.ContainerId);

    public string? SelectedSignalOnlyContainerType
    {
        get => _selectedSignalOnlyContainerType;
        set
        {
            if (string.Equals(_selectedSignalOnlyContainerType, value, StringComparison.OrdinalIgnoreCase))
                return;
            _selectedSignalOnlyContainerType = value;
            OnPropertyChanged();
            if (_isApplyingPlan || string.IsNullOrWhiteSpace(value) ||
                SelectedTreeNode?.ContainerId is not { } containerId)
                return;
            if (!_planService.SetSignalOnlyContainerType(containerId, value))
            {
                StatusText = "Der Signal-only-Container konnte nicht typisiert werden.";
                return;
            }
            StatusText = $"Signal-only-Container wurde als '{value}' klassifiziert. Die dafür bekannten Logik- und SimObject-Ziele wurden in den Plan aufgenommen.";
            _log.Information(LogArea, StatusText);
            AddOperationDetail("Containertyp gewählt", StatusText);
        }
    }

    public ContainerToFeeVisualFeeInterfaceVM? SelectedExistingInterface
    {
        get => _selectedExistingInterface;
        set
        {
            if (ReferenceEquals(_selectedExistingInterface, value))
                return;

            _selectedExistingInterface = value?.Model is null ? null : value;
            OnPropertyChanged();
            if (!_isApplyingPlan && !_isRefreshingFeeInterfaceProjection)
            {
                _isRefreshingFeeInterfaceProjection = true;
                try
                {
                    foreach (var item in AvailableFeeInterfaces)
                        item.IsSelected = ReferenceEquals(item, value);
                }
                finally
                {
                    _isRefreshingFeeInterfaceProjection = false;
                }
                CommitExistingInterfaceSelection();
            }
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
            var plan = _planService.CurrentPlan;
            if (containerId is null || plan is null || IsCreationRequestedForSelection == value)
                return;

            if (value)
            {
                var targetIds = plan.Targets
                    .Where(target => string.Equals(target.ContainerId, containerId, StringComparison.Ordinal))
                    .Select(target => target.Id)
                    .ToHashSet(StringComparer.Ordinal);
                var existing = plan.Assignments
                    .Where(assignment => targetIds.Contains(assignment.TargetId))
                    .Select(assignment => assignment.FeeObjectName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (existing.Length > 0)
                {
                    MessageBox.Show(
                        "ACHTUNG: Dieser Container enthält bereits vorhandene FEE-SimObjects:\n\n" +
                        string.Join("\n", existing.Select(name => $"• {name}")) +
                        "\n\nDiese Option erzeugt ausschließlich fehlende Ziele; vorhandene Objekte werden wiederverwendet " +
                        "und nicht absichtlich dupliziert. Für einen bewussten Ersatz zuerst die Zuordnung entfernen.",
                        "Vorhandene FEE-Objekte",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }

            if (!_planService.SetCreationRequested(containerId, value))
                return;

            StatusText = value
                ? "Fehlende SimObjects werden bei der Generierung erzeugt."
                : "Nicht zugeordnete SimObjects werden übersprungen.";
            _log.Information(LogArea, StatusText);
            AddOperationDetail("Erzeugungsoption", StatusText);
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
            OnPropertyChanged(nameof(CanClassifySignalOnlyContainer));
            OnPropertyChanged(nameof(IsCreationRequestedForSelection));
            _selectedSignalOnlyContainerType = value?.ContainerId is { } selectedContainerId
                ? _planService.CurrentPlan?.ContainerTypeOverrides.FirstOrDefault(item =>
                      string.Equals(item.ContainerId, selectedContainerId, StringComparison.Ordinal))?.TypeName ??
                  (_planService.CurrentPlan?.FindNode(selectedContainerId) is { } selectedContainer &&
                   ContainerMetadataCatalog.TryGet(selectedContainer.TypeName, out _)
                      ? selectedContainer.TypeName
                      : null)
                : null;
            OnPropertyChanged(nameof(SelectedSignalOnlyContainerType));
            RefreshSelectionProjection();
            SynchronizeSelectionsFromTree(value);
        }
    }

    public bool SynchronizeRelatedSelections
    {
        get => _synchronizeRelatedSelections;
        set
        {
            if (_synchronizeRelatedSelections == value)
                return;
            _synchronizeRelatedSelections = value;
            OnPropertyChanged();
            if (!value)
                ClearSynchronizationMatches();
            else if (SelectedTreeNode is not null)
                SynchronizeSelectionsFromTree(SelectedTreeNode);
        }
    }

    public ContainerToFeeVisualFeeObjectVM? SelectedFeeObject
    {
        get => _selectedFeeObject;
        set
        {
            if (ReferenceEquals(_selectedFeeObject, value))
                return;
            _selectedFeeObject = value;
            OnPropertyChanged();
            if (value is null || !SynchronizeRelatedSelections)
                return;
            var node = TreeRoots.SelectMany(root => root.SelfAndDescendants())
                .FirstOrDefault(item => string.Equals(item.FeeObjectId, value.Id, StringComparison.Ordinal));
            if (node is null)
            {
                var targetId = _planService.CurrentPlan?.Assignments.FirstOrDefault(assignment =>
                    string.Equals(assignment.FeeObjectId, value.Id, StringComparison.Ordinal))?.TargetId;
                node = FindTreeNode(targetId);
            }
            SelectRelatedTreeNode(node);
        }
    }

    public ContainerToFeeVisualFeeSignalVM? SelectedFeeSignal
    {
        get => _selectedFeeSignal;
        set
        {
            if (ReferenceEquals(_selectedFeeSignal, value))
                return;
            _selectedFeeSignal = value;
            OnPropertyChanged();
            if (value is null || !SynchronizeRelatedSelections)
                return;
            var nodeId = value.AssignedNodeIds.FirstOrDefault() ??
                _planService.CurrentPlan?.SignalAssignments.FirstOrDefault(assignment =>
                    string.Equals(assignment.FeeSignalGuid, value.GuidString, StringComparison.OrdinalIgnoreCase))?.SignalNodeId;
            SelectRelatedTreeNode(FindTreeNode(nodeId));
        }
    }

    public ContainerToFeeVisualSignalSlotVM? SelectedSignalSlot
    {
        get => _selectedSignalSlot;
        set
        {
            if (ReferenceEquals(_selectedSignalSlot, value))
                return;
            _selectedSignalSlot = value;
            OnPropertyChanged();
            if (value is null || !SynchronizeRelatedSelections)
                return;
            SelectRelatedTreeNode(FindTreeNode(value.PrimaryNodeId));
        }
    }

    public VisualIssue? SelectedIssue
    {
        get => _selectedIssue;
        set
        {
            if (ReferenceEquals(_selectedIssue, value))
                return;
            _selectedIssue = value;
            OnPropertyChanged();
            if (value is null || !SynchronizeRelatedSelections)
                return;
            SelectRelatedTreeNode(FindTreeNode(value.NodeId));
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
            if (value is not null && SynchronizeRelatedSelections)
                SelectRelatedTreeNode(FindTreeNode(value.Id));
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
            FeeRefreshSnapshot snapshot = await RefreshFeeStateAsync(cancellationToken);
            FeeObjectsView.Refresh();
            FeeRefreshHint = snapshot.ObjectCount == 0 || snapshot.SignalCount == 0
                ? "FEE lieferte keine SimObjects oder Signale. Model Validation ausführen, damit der Projektzustand vollständig eingelesen wird, danach hier erneut 'FEE aktualisieren' wählen."
                : "FEE-Daten wurden direkt über die API aktualisiert. Falls kürzlich geänderte SimObjects oder Signale fehlen: Model Validation ausführen und danach erneut aktualisieren.";
            StatusText = snapshot.AutomaticAssignmentCount > 0
                ? $"{snapshot.ObjectCount} FEE-SimObjects, {snapshot.ContainerObjectCount} Logik-/Cabinet-Objekte, {snapshot.SignalCount} Signale und {snapshot.InterfaceCount} Interfaces geladen; " +
                  $"{snapshot.SignalLinkCount} Signal- und {snapshot.SimObjectLinkCount} SimObject-Slot-Verknüpfungen gelesen; {snapshot.AutomaticAssignmentCount} automatisch zugeordnet; {snapshot.VerifiedContainerCount} Container mit Provenienz abgeglichen."
                : $"{snapshot.ObjectCount} FEE-SimObjects, {snapshot.ContainerObjectCount} Logik-/Cabinet-Objekte, {snapshot.SignalCount} Signale und {snapshot.InterfaceCount} Interfaces geladen; " +
                  $"{snapshot.SignalLinkCount} Signal- und {snapshot.SimObjectLinkCount} SimObject-Slot-Verknüpfungen gelesen; {snapshot.VerifiedContainerCount} Container mit Provenienz abgeglichen.";
            _log.Information(LogArea, StatusText);
            AddOperationDetail("FEE aktualisiert", StatusText);
            RecordCurrentConnectionDetails();
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
        AddOperationDetail("Automatische Zuordnung", StatusText);
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

        var startedUtc = DateTimeOffset.UtcNow;
        var before = CaptureGenerationObservations();
        await RunBusyAsync("Container werden mit dem bestehenden Executor erzeugt …", async cancellationToken =>
        {
            VisualExecutionResult? result = null;
            try
            {
                GenerationProgress = 0;
                GenerationProgressText = "Generierung wird vorbereitet …";
                var progress = new Progress<VisualGenerationProgress>(update =>
                {
                    GenerationProgress = update.Percent;
                    GenerationProgressText = update.Message;
                });
                result = await _planService.ExecuteAsync(
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
                    GenerationProgressText = "FEE-Verknüpfungen werden rückgelesen …";
                    try
                    {
                        await RefreshFeeStateAsync(cancellationToken);
                        FeeObjectsView.Refresh();
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        _log.Warning(
                            LogArea,
                            $"Generierung abgeschlossen, die FEE-Verknüpfungen konnten anschließend nicht verifiziert werden: {exception.Message}");
                        StatusText = result.Message +
                                     " Die Anzeige bleibt bis zum nächsten erfolgreichen 'FEE aktualisieren' unverifiziert.";
                    }
                    GenerationProgress = 100;
                    GenerationProgressText = "FEE-Generierung abgeschlossen.";
                    _log.Information(LogArea, result.Message);
                    AddOperationDetail("Generierung", StatusText);
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
                    AddOperationDetail("Generierung unvollständig", result.Message);
                }
                SaveGenerationManifest(startedUtc, before, result.Success, result.Message,
                    result.Issues.Select(issue => $"[{issue.Code}] {issue.Message}"));
            }
            catch (Exception exception)
            {
                SaveGenerationManifest(startedUtc, before, false,
                    exception is OperationCanceledException ? "Generierung abgebrochen." : "Generierung mit Ausnahme beendet.",
                    [exception.Message]);
                throw;
            }
        });
    }

    private GenerationObjectObservation[] CaptureGenerationObservations() => TreeRoots
        .SelectMany(root => root.SelfAndDescendants())
        .Select(node => new GenerationObjectObservation(
            node.ContainerId ?? (node.Kind == VisualNodeKind.Container ? node.Id : string.Empty),
            node.Id,
            node.Kind.ToString(),
            node.Name,
            node.EffectiveState.Kind.ToString(),
            node.LinkedObjectDescription))
        .ToArray();

    private void SaveGenerationManifest(
        DateTimeOffset startedUtc,
        IReadOnlyList<GenerationObjectObservation> before,
        bool success,
        string summary,
        IEnumerable<string> errors)
    {
        var plan = _planService.CurrentPlan;
        if (plan is null)
            return;
        var manifest = _manifestBuilder.Build(
            plan.SourceXmlPath,
            plan.SourceFingerprint,
            startedUtc,
            success,
            summary,
            before,
            CaptureGenerationObservations(),
            errors);
        LastManifestPath = _manifestStore.Save(manifest);
        LastManifestSummary = $"Manifest: {manifest.Items.Count} Objekte; " +
                              $"{manifest.UnresolvedContainerIds.Count} Container offen; " +
                              $"{(manifest.Success ? "Lauf erfolgreich" : "Lauf unvollständig")}.";
        var findings = manifest.Errors.Select(error => new QualityFinding(
            LogArea, "GENERATION_MANIFEST_ERROR", QualityStatus.Failed, error)).ToArray();
        QualityEvidenceStore.Instance.Upsert(new QualityEvidence(
            LogArea,
            LastManifestPath,
            success && manifest.UnresolvedContainerIds.Count == 0 ? QualityStatus.Passed : QualityStatus.Failed,
            LastManifestSummary,
            DateTimeOffset.UtcNow,
            findings));
    }

    private void ResumeLastGeneration()
    {
        var plan = _planService.CurrentPlan;
        if (plan is null)
            return;
        var manifest = _manifestStore.LoadLatest(plan.SourceFingerprint);
        if (manifest is null)
        {
            StatusText = "Für dieses ContainerFile wurde noch kein Generierungsmanifest gefunden.";
            return;
        }
        var unresolved = manifest.UnresolvedContainerIds.ToHashSet(StringComparer.Ordinal);
        if (unresolved.Count == 0)
        {
            StatusText = "Das letzte Manifest enthält keine offenen Container.";
            return;
        }
        _planService.SetAllGenerationSelected(false);
        foreach (var containerId in unresolved)
            _planService.SetGenerationSelected(containerId, true);
        LastManifestSummary = $"{unresolved.Count} offene Container aus Manifest {manifest.Id[..8]} zur Reparatur ausgewählt.";
        StatusText = LastManifestSummary;
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
                {
                    await _planService.DiscoverFeeSimObjectLinksAsync(cancellationToken);
                    ApplyDiscoveredSimObjectStates();
                    _log.Information(LogArea, result.Message);
                }
                else
                    _log.Warning(LogArea, result.Message);
                AddOperationDetail(result.Success ? "SimObjects verknüpft" : "SimObject-Verknüpfung offen", result.Message);
                RecordCurrentConnectionDetails();
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
                {
                    await _planService.DiscoverFeeSignalLinksAsync(cancellationToken);
                    ApplyDiscoveredSignalStates(_planService.DiscoveredFeeSignals);
                    RefreshFeeSignalProjection(_planService.DiscoveredFeeSignals);
                    _log.Information(LogArea, result.Message);
                }
                else
                    _log.Warning(LogArea, result.Message);
                AddOperationDetail(result.Success ? "Signale verknüpft" : "Signal-Verknüpfung offen", result.Message);
                RecordCurrentConnectionDetails();
            });
    }

    private void RecordCurrentConnectionDetails()
    {
        var plan = _planService.CurrentPlan;
        if (plan is null)
            return;

        var verifiedSignals = _planService.FindVerifiedSignalNodeIds();
        foreach (var assignment in plan.SignalAssignments)
        {
            var node = plan.FindNode(assignment.SignalNodeId);
            var container = node?.ContainerId is null ? null : plan.FindNode(node.ContainerId);
            var verified = verifiedSignals.TryGetValue(assignment.FeeSignalGuid, out var nodeIds) &&
                           nodeIds.Contains(assignment.SignalNodeId, StringComparer.Ordinal);
            AddOperationDetail(
                verified ? "Signal verbunden" : "Signal-Verknüpfung offen",
                $"{assignment.FeeInterfaceName} / {assignment.FeeSignalTag} -> " +
                $"{container?.Name ?? "unbekannter Container"} / {node?.Name ?? assignment.SignalNodeId} " +
                $"[{(node is null ? "Slot unbekannt" : plan.GetEffectiveSlot(node))}].");
        }

        foreach (var assignment in plan.Assignments)
        {
            var target = plan.FindTarget(assignment.TargetId);
            var container = target is null ? null : plan.FindNode(target.ContainerId);
            var state = _planService.GetSimObjectConnectionState(assignment.TargetId, assignment.FeeObjectId);
            AddOperationDetail(
                state.IsVerified ? "SimObject verbunden" : "SimObject-Verknüpfung offen",
                $"{assignment.FeeObjectName} -> {container?.Name ?? "unbekannter Container"} / " +
                $"{target?.DisplayName ?? assignment.TargetId}: {state.Description}");
        }
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

        if (request?.Target is ContainerToFeeVisualSignalSlotVM signalSlot)
        {
            var feeSignalGuids = sources
                .Select(TryGetFeeSignalGuid)
                .Where(guid => !string.IsNullOrWhiteSpace(guid))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (feeSignalGuids.Length != sources.Count)
            {
                Reject("Auf einen Signalslot dürfen ausschließlich FEE-Signale gezogen werden. SimObjects und Signale sind getrennte Zuordnungsbereiche.");
                return;
            }

            var result = _planService.AssignSignalsToSlot(
                signalSlot.ContainerId,
                signalSlot.Slot,
                feeSignalGuids);
            PublishIssues(result.Issues);
            if (!result.Success)
            {
                Reject(result.Message);
                return;
            }

            StatusText = result.Message;
            _log.Information(LogArea, result.Message);
            AddOperationDetail(
                "Signalzuordnung",
                $"{string.Join(", ", feeSignalGuids)} -> Container {signalSlot.ContainerId}, Slot {signalSlot.Slot}. Die FEE-Verknüpfung wird bei Start Generation oder 'Nur Signale verknüpfen' ausgeführt.");
            return;
        }

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
            AddOperationDetail("Signale ergänzt", result.Message);
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
            AddOperationDetail("Signalzuordnung", StatusText);
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
        AddOperationDetail("SimObject-Zuordnung", StatusText);
    }

    private bool CanHandleDrop(ContainerToFeeVisualDropRequest? request)
    {
        if (IsBusy || request is null)
            return false;

        IReadOnlyList<object> sources = request.Source is IReadOnlyList<object> selectedItems
            ? selectedItems
            : new[] { request.Source };
        if (request.Target is ContainerToFeeVisualSignalSlotVM signalSlot)
        {
            var signalGuids = sources.Select(TryGetFeeSignalGuid).ToArray();
            return signalGuids.All(guid => !string.IsNullOrWhiteSpace(guid)) &&
                   (signalSlot.AllowMultiSelect || signalGuids.Length == 1);
        }
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

    public VisualStatusFilterOption SelectedTreeStatusFilter
    {
        get => _selectedTreeStatusFilter;
        set
        {
            if (ReferenceEquals(_selectedTreeStatusFilter, value) || value is null)
                return;
            _selectedTreeStatusFilter = value;
            OnPropertyChanged();
            ApplyTreeFilter();
        }
    }

    public VisualTreeSortOption SelectedTreeSort
    {
        get => _selectedTreeSort;
        set
        {
            if (ReferenceEquals(_selectedTreeSort, value) || value is null)
                return;
            _selectedTreeSort = value;
            OnPropertyChanged();
            ApplyTreeSort();
        }
    }

    public VisualStatusFilterOption SelectedFeeObjectStatusFilter
    {
        get => _selectedFeeObjectStatusFilter;
        set
        {
            if (ReferenceEquals(_selectedFeeObjectStatusFilter, value) || value is null)
                return;
            _selectedFeeObjectStatusFilter = value;
            OnPropertyChanged();
            FeeObjectsView.Refresh();
        }
    }

    public VisualStatusFilterOption SelectedFeeSignalStatusFilter
    {
        get => _selectedFeeSignalStatusFilter;
        set
        {
            if (ReferenceEquals(_selectedFeeSignalStatusFilter, value) || value is null)
                return;
            _selectedFeeSignalStatusFilter = value;
            OnPropertyChanged();
            FeeSignalsView.Refresh();
        }
    }

    private static string? TryGetFeeSignalGuid(object source) => source switch
    {
        ContainerToFeeVisualFeeSignalVM signal => signal.GuidString,
        ContainerToFeeVisualSignalEntryVM entry when !string.IsNullOrWhiteSpace(entry.FeeSignalGuid) =>
            entry.FeeSignalGuid,
        _ => null,
    };

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

    private async Task<FeeRefreshSnapshot> RefreshFeeStateAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<VisualFeeObject> objects =
            await _planService.DiscoverFeeObjectsAsync(cancellationToken);
        IReadOnlyList<VisualFeeInterface> interfaces =
            await _planService.DiscoverFeeInterfacesAsync(cancellationToken);
        RefreshFeeObjectProjection(objects);
        RefreshFeeInterfaceProjection(interfaces);

        // Auto-assignment raises PlanChanged and rebuilds the tree. Apply all
        // live discovery states afterwards, otherwise that rebuild can mask a
        // missing SimObject-slot link with a stale green container state.
        int automaticAssignments = _planService.AutoAssignMatches();
        IReadOnlyList<VisualFeeSignalLink> signalLinks =
            await _planService.DiscoverFeeSignalLinksAsync(cancellationToken);
        IReadOnlyList<VisualFeeObjectLink> simObjectLinks =
            await _planService.DiscoverFeeSimObjectLinksAsync(cancellationToken);
        // Rebuild the list after both link reads so identical SimObjects are
        // distinguished by their GUID-specific live connection state.
        RefreshFeeObjectProjection(objects);
        ApplyDiscoveredContainerObjectStates(_planService.DiscoveredFeeContainerObjects);
        ApplyDiscoveredSimObjectStates();
        ApplyDiscoveredSignalStates(_planService.DiscoveredFeeSignals);
        RefreshFeeSignalProjection(_planService.DiscoveredFeeSignals);
        IReadOnlySet<string> verifiedContainers = await _planService
            .DiscoverVerifiedContainerIdsAsync(cancellationToken);
        _verifiedContainerIds.Clear();
        _verifiedContainerIds.UnionWith(verifiedContainers);
        _planService.DeselectVerifiedContainers(verifiedContainers);
        ApplyVerifiedContainerStates(verifiedContainers);

        return new FeeRefreshSnapshot(
            objects.Count,
            _planService.DiscoveredFeeContainerObjects.Count,
            _planService.DiscoveredFeeSignals.Count,
            interfaces.Count,
            signalLinks.Count,
            simObjectLinks.Count,
            automaticAssignments,
            verifiedContainers.Count);
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
        if (_operationCancellation is null || _operationCancellation.IsCancellationRequested)
            return;
        _operationCancellation.Cancel();
        GenerationProgressText = "Abbruch angefordert. Der aktuelle FEE-SDK-Aufruf wird noch sauber verlassen …";
        StatusText = "Vorgang wird abgebrochen; die Oberfläche wird freigegeben …";
        _log.Information(LogArea, StatusText);
    }

    private async Task HardAbortFeeConnectionAsync()
    {
        if (!IsBusy && !HasPendingFeeSdkOperation)
            return;
        var answer = MessageBox.Show(
            "ACHTUNG: Die FEE-Verbindung wird getrennt und die Oberfläche sofort freigegeben. " +
            "Die verwendete FEE-SDK besitzt jedoch keinen Abbruch für einen bereits laufenden Remote-Aufruf. " +
            "Der Aufruf selbst kann innerhalb dieses Prozesses nicht sicher hart beendet werden und wird deshalb isoliert. " +
            "Vollständig beendet wird er spätestens beim Schließen der Anwendung. " +
            "Eine bereits begonnene FEE-Änderung besitzt keine transaktionale Rücknahme und kann teilweise " +
            "ausgeführt worden sein. Danach ist ein erneutes Verbinden und 'FEE aktualisieren' erforderlich.\n\n" +
            "FEE-Verbindung jetzt trennen und den Aufruf isolieren?",
            "Blockierenden FEE-SDK-Aufruf isolieren",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        CancelOperation();
        try
        {
            var disconnectTask = Task.Run(() => Services.ApiInstance?.Disconnect());
            var completed = await Task.WhenAny(
                disconnectTask,
                Task.Delay(FeeDisconnectResponseTimeout));
            if (ReferenceEquals(completed, disconnectTask))
                await disconnectTask;
            else
                _ = ObserveDetachedDisconnectAsync(disconnectTask);

            GenerationProgressText = "Abgebrochen; SDK-Aufruf isoliert";
            StatusText = ReferenceEquals(completed, disconnectTask)
                ? "FEE-Verbindung getrennt. Ein bereits blockierter SDK-Aufruf bleibt bis zu seiner Rückkehr isoliert."
                : "Die FEE-Trennung antwortet ebenfalls nicht. Oberfläche ist freigegeben; zum sicheren Beenden des SDK-Aufrufs Anwendung schließen.";
            _log.Warning(LogArea, StatusText);
            AddOperationDetail("SDK-Aufruf isoliert", StatusText);
        }
        catch (Exception exception)
        {
            StatusText = "Auch das Trennen der FEE-Verbindung ist fehlgeschlagen; Anwendung kontrolliert schließen.";
            _log.Error(LogArea, StatusText, exception);
        }
    }

    private async Task ObserveDetachedDisconnectAsync(Task disconnectTask)
    {
        try
        {
            await disconnectTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log.Error(LogArea, "Der im Hintergrund isolierte FEE-Trennaufruf ist fehlgeschlagen.", exception);
        }
    }

    private async Task DeleteFeeObjectAsync(ContainerToFeeVisualFeeObjectVM? item)
    {
        if (item is null || IsBusy || !Connection.CanUseFeeFeatures)
            return;
        if (!Guid.TryParse(item.GuidString, out var objectGuid))
        {
            Reject($"FEE-SimObject '{item.Name}' besitzt keine gültige GUID und kann nicht gelöscht werden.");
            return;
        }

        var answer = MessageBox.Show(
            $"FEE-SimObject wirklich dauerhaft löschen?\n\nName: {item.Name}\nTyp: {item.FeeType}\n" +
            $"Parent: {item.ParentName}\n" +
            $"Live-Status: {item.ConnectionStateText}\n\n" +
            "Alle FEE-Verknüpfungen dieses Objekts gehen verloren. Diese Aktion kann im Tool nicht rückgängig gemacht werden.",
            "FEE-SimObject löschen",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        await RunBusyAsync($"FEE-SimObject '{item.Name}' wird gelöscht …", async cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Once the destructive vendor call starts it is not cancellable.
            // Never report a cancellation after FEE may already have deleted
            // the object; reconcile local state and live state first.
            var deleteTask = Task.Run(() => Services.ApiInstance.Object.DeleteObject(objectGuid));
            try
            {
                await deleteTask.WaitAsync(DeleteFeeObjectResponseTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                StatusText = $"FEE hat das Löschen von '{item.Name}' nach " +
                             $"{DeleteFeeObjectResponseTimeout.TotalSeconds:0} Sekunden noch nicht bestätigt. " +
                             "Die Oberfläche bleibt bedienbar; bis zur Rückkehr des SDK-Aufrufs wird kein weiterer FEE-Aufruf gestartet.";
                _log.Warning(LogArea, StatusText);
                AddOperationDetail("FEE-Löschung läuft nach", $"{item.Name} ({item.GuidString})");
                _cancelledOperationFinishing = ObservePendingDeleteAsync(deleteTask, item);
                InvalidateCommands();
                return;
            }

            CompleteDeletedFeeObject(item);
        });
    }

    private async Task ObservePendingDeleteAsync(
        Task deleteTask,
        ContainerToFeeVisualFeeObjectVM item)
    {
        try
        {
            await deleteTask.ConfigureAwait(false);
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
                return;
            await dispatcher.InvokeAsync(() => CompleteDeletedFeeObject(item));
        }
        catch (Exception exception)
        {
            _log.Error(
                LogArea,
                $"Der verzögert zurückgekehrte FEE-Löschaufruf für '{item.Name}' ist fehlgeschlagen.",
                exception);
        }
        finally
        {
            _cancelledOperationFinishing = null;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null)
                await dispatcher.InvokeAsync(InvalidateCommands);
        }
    }

    private void CompleteDeletedFeeObject(ContainerToFeeVisualFeeObjectVM item)
    {
        _planService.ForgetDeletedFeeObject(item.Id);
        RefreshFeeObjectProjection(_planService.DiscoveredFeeObjects);
        StatusText = $"FEE-SimObject '{item.Name}' wurde gelöscht. Die lokale Ansicht ist bereinigt; " +
                     "ein vollständiger FEE-Abgleich kann bei Bedarf separat mit 'FEE aktualisieren' gestartet werden.";
        _log.Warning(LogArea, StatusText);
        AddOperationDetail("FEE-Objekt gelöscht", $"{item.Name} ({item.GuidString})");
    }

    private async Task RunBusyAsync(string status, Func<CancellationToken, Task> operation)
    {
        if (IsBusy)
            return;
        if (_cancelledOperationFinishing is { IsCompleted: false })
        {
            StatusText = "Der zuvor abgebrochene FEE-SDK-Aufruf wird noch beendet. Bitte kurz warten.";
            return;
        }

        CancellationTokenSource? cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        IsBusy = true;
        StatusText = status;
        Task operationTask = Task.CompletedTask;
        using var measurement = PerformanceMeasurementService.Instance.Start(LogArea, status);
        try
        {
            operationTask = operation(cancellation.Token);
            await operationTask.WaitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            measurement.MarkFailed();
            StatusText = "Vorgang abgebrochen.";
            GenerationProgressText = "Abgebrochen";
            _log.Information(LogArea, StatusText);
            if (!operationTask.IsCompleted)
            {
                _operationCancellation = null;
                _cancelledOperationFinishing = ObserveCancelledOperationAsync(operationTask, cancellation);
                cancellation = null;
            }
        }
        catch (Exception exception)
        {
            measurement.MarkFailed();
            StatusText = "Vorgang fehlgeschlagen. Details stehen im Protokoll.";
            _log.Error(LogArea, StatusText, exception);
        }
        finally
        {
            if (ReferenceEquals(_operationCancellation, cancellation))
                _operationCancellation = null;
            cancellation?.Dispose();
            IsBusy = false;
        }
    }

    private async Task ObserveCancelledOperationAsync(Task operationTask, CancellationTokenSource cancellation)
    {
        try
        {
            await operationTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: the SDK call returned and the next cancellation checkpoint stopped the workflow.
        }
        catch (Exception exception)
        {
            _log.Error(LogArea, "Der im Hintergrund auslaufende abgebrochene Vorgang ist fehlgeschlagen.", exception);
        }
        finally
        {
            cancellation.Dispose();
            _cancelledOperationFinishing = null;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
                InvalidateCommands();
            else
                await dispatcher.InvokeAsync(InvalidateCommands);
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
            ApplyDiscoveredContainerObjectStates(_planService.DiscoveredFeeContainerObjects);
            ApplyDiscoveredSimObjectStates();
            ApplyDiscoveredSignalStates(_planService.DiscoveredFeeSignals);
            RefreshFeeSignalProjection(_planService.DiscoveredFeeSignals);
            ApplyVerifiedContainerStates(_verifiedContainerIds);
            PublishIssues(validation.Issues);
            ApplyTreeSort();
            ApplyTreeFilter();

            SelectedTreeNode = FindTreeNode(selectedNodeId) ?? TreeRoots.FirstOrDefault();
            SelectedTarget = Targets.FirstOrDefault(target => target.Id == selectedTargetId) ?? Targets.FirstOrDefault();
            _selectedExistingInterface = AvailableFeeInterfaces.FirstOrDefault(item => item.IsSelected);
            OnPropertyChanged(nameof(SelectedExistingInterface));
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

        var feeObjectId = ResolveAssignedFeeObjectId(node, plan);
        var hasDuplicateIdentity = _planService.IsDuplicateFeeObject(feeObjectId);
        var isDuplicateConfirmed = _planService.IsDuplicateFeeObjectConfirmed(feeObjectId);
        var containerSelected = node.Kind == VisualNodeKind.Container
            ? plan.IsGenerationSelected(node.Id)
            : node.ContainerId is null || plan.IsGenerationSelected(node.ContainerId);

        return new(
            node,
            childModels.Select(child => BuildTree(child, plan, issues)),
            plan.IsGenerationSelected(node.Id),
            node.Kind == VisualNodeKind.Container && ContainerMetadataCatalog.TryGet(node.TypeName, out _),
            plan.GetEffectiveSlot(node),
            allowedSlots,
            feeObjectId,
            hasDuplicateIdentity,
            isDuplicateConfirmed,
            containerSelected,
            GetNodeState(node, plan),
            GetNodeConnectionDescription(node, plan),
            GetNodeErrors(node, plan, issues),
            SetGenerationSelected,
            SetSlotOverride);
    }

    private void ConfirmDuplicateFeeObject(ContainerToFeeVisualTreeNodeVM? node)
    {
        if (node?.ParentId is null || string.IsNullOrWhiteSpace(node.FeeObjectId))
            return;
        var result = _planService.ConfirmDuplicateAssignment(node.ParentId, node.FeeObjectId);
        StatusText = result.Message;
        if (result.Success)
        {
            _log.Information(LogArea, result.Message);
            RefreshFeeObjectProjection();
            if (SelectedTreeNode is not null)
                SynchronizeSelectionsFromTree(SelectedTreeNode);
        }
        else
            _log.Warning(LogArea, result.Message);
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
            (node.Kind == VisualNodeKind.SimObject &&
             string.Equals(issue.NodeId, node.ParentId, StringComparison.Ordinal)) ||
            (node.Kind == VisualNodeKind.Container &&
             plan.FindTarget(issue.NodeId ?? string.Empty)?.ContainerId == node.Id))
        .Select(issue => $"[{issue.Code}] {issue.Message}")
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private ContainerToFeeVisualNodeState GetNodeState(
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
                ? _planService.GetSimObjectConnectionState(node.Id).IsVerified
                    ? ContainerToFeeVisualNodeState.Verified
                    : ContainerToFeeVisualNodeState.FoundUnlinked
                : plan.IsCreationRequested(node.ContainerId ?? string.Empty)
                    ? ContainerToFeeVisualNodeState.Planned
                    : ContainerToFeeVisualNodeState.Missing;
        }

        if (node.Kind == VisualNodeKind.SimObject && node.ParentId is not null)
            return _planService.GetSimObjectConnectionState(
                    node.ParentId,
                    ResolveAssignedFeeObjectId(node, plan)).IsVerified
                ? ContainerToFeeVisualNodeState.Verified
                : ContainerToFeeVisualNodeState.FoundUnlinked;

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

    private string GetNodeConnectionDescription(VisualNode node, VisualPlan plan)
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
                : $"Zugeordnetes FEE-SimObject: {string.Join(", ", assignments)}. " +
                  _planService.GetSimObjectConnectionState(node.Id).Description;
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
            return node.ParentId is null
                ? $"Vorhandenes FEE-SimObject: {node.Name}"
                : $"Vorhandenes FEE-SimObject: {node.Name}. " +
                  _planService.GetSimObjectConnectionState(
                      node.ParentId,
                      ResolveAssignedFeeObjectId(node, plan)).Description;

        return string.Empty;
    }

    private void ApplyDiscoveredSignalStates(IReadOnlyList<VisualFeeSignal> signals)
    {
        var selectedGuids = GetSelectedInterfaceGuids();
        signals = selectedGuids.Count == 0
            ? []
            : signals.Where(signal => selectedGuids.Contains(signal.InterfaceGuidString)).ToArray();
        var selectedSignalGuids = signals
            .Select(signal => signal.GuidString)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var node in TreeRoots.SelectMany(root => root.SelfAndDescendants())
                     .Where(node => node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal))
        {
            var explicitAssignment = _planService.CurrentPlan?.SignalAssignments.LastOrDefault(assignment =>
                assignment.SignalNodeId == node.Id &&
                selectedSignalGuids.Contains(assignment.FeeSignalGuid));
            var explicitlyAssignedSignal = explicitAssignment is null
                ? null
                : signals.FirstOrDefault(signal => string.Equals(
                    signal.GuidString,
                    explicitAssignment.FeeSignalGuid,
                    StringComparison.OrdinalIgnoreCase));
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
            var resolvedSignal = explicitlyAssignedSignal ?? (exactMatches.Length == 1 ? exactMatches[0] : null);
            var state = resolvedSignal is not null
                ? _planService.GetSignalConnectionState(node.Id, resolvedSignal.GuidString).IsVerified
                    ? ContainerToFeeVisualNodeState.Verified
                    : ContainerToFeeVisualNodeState.FoundUnlinked
                : (matches.Length, exactMatches.Length) switch
            {
                (0, _) => ContainerToFeeVisualNodeState.Planned,
                _ => ContainerToFeeVisualNodeState.Ambiguous,
            };
            var connectionState = resolvedSignal is null
                ? null
                : _planService.GetSignalConnectionState(node.Id, resolvedSignal.GuidString);
            var connection = resolvedSignal is not null
                ? $"Gefundenes FEE-Signal: {resolvedSignal.Tag} · {resolvedSignal.InterfaceName} · {resolvedSignal.Location}. {connectionState!.Description}"
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
        RefreshSelectionProjection();
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

    private void ApplyDiscoveredSimObjectStates()
    {
        var plan = _planService.CurrentPlan;
        if (plan is null)
            return;
        foreach (var targetNode in TreeRoots.SelectMany(root => root.SelfAndDescendants())
                     .Where(node => node.Kind == VisualNodeKind.SimObjectTarget))
        {
            var hasAssignment = plan.Assignments.Any(item =>
                string.Equals(item.TargetId, targetNode.Id, StringComparison.Ordinal));
            if (!hasAssignment)
                continue;
            var connection = _planService.GetSimObjectConnectionState(targetNode.Id);
            var state = connection.IsVerified
                ? ContainerToFeeVisualNodeState.Verified
                : ContainerToFeeVisualNodeState.FoundUnlinked;
            targetNode.ApplyExecutionState(state, connection.Description);
            foreach (var child in targetNode.Children.Where(item => item.Kind == VisualNodeKind.SimObject))
            {
                var childConnection = _planService.GetSimObjectConnectionState(
                    targetNode.Id,
                    child.FeeObjectId);
                child.ApplyExecutionState(
                    childConnection.IsVerified
                        ? ContainerToFeeVisualNodeState.Verified
                        : ContainerToFeeVisualNodeState.FoundUnlinked,
                    childConnection.Description);
            }
        }
        RefreshAggregateTreeStates();
    }

    private static string? ResolveAssignedFeeObjectId(VisualNode node, VisualPlan plan)
    {
        if (node.Kind != VisualNodeKind.SimObject || string.IsNullOrWhiteSpace(node.ParentId))
            return null;
        return plan.Assignments.FirstOrDefault(assignment =>
            string.Equals(
                node.Id,
                $"{node.ParentId}:assigned:{StableId.Encode(assignment.FeeObjectId)}",
                StringComparison.Ordinal))?.FeeObjectId;
    }

    private void ApplyVerifiedContainerStates(IReadOnlySet<string> verifiedContainerIds)
    {
        foreach (var node in TreeRoots.SelectMany(root => root.SelfAndDescendants())
                     .Where(node => node.ContainerId is not null &&
                                    verifiedContainerIds.Contains(node.ContainerId) &&
                                    node.Kind is VisualNodeKind.BasicFrame or VisualNodeKind.Interface))
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
        SignalSlots.Clear();
        VisibleEdges.Clear();

        VisualPlan? plan = _planService.CurrentPlan;
        string? containerId = SelectedTreeNode?.ContainerId;
        if (plan is null || string.IsNullOrWhiteSpace(containerId))
        {
            _selectedTarget = null;
            OnPropertyChanged(nameof(SelectedTarget));
            return;
        }

        foreach (VisualSimObjectTarget target in plan.Targets.Where(target => target.ContainerId == containerId))
        {
            var assignments = plan.Assignments.Where(assignment => assignment.TargetId == target.Id);
            Targets.Add(new ContainerToFeeVisualTargetVM(
                target,
                assignments,
                plan.IsCreationRequested(containerId),
                Issues.Where(issue => string.Equals(issue.NodeId, target.Id, StringComparison.Ordinal)),
                feeObjectId => _planService.GetSimObjectConnectionState(target.Id, feeObjectId)));
        }

        var container = plan.FindNode(containerId);
        if (container is not null && ContainerMetadataCatalog.TryGet(container.TypeName, out var descriptor))
        {
            var signalNodes = plan.Nodes
                .Where(node => string.Equals(node.ContainerId, containerId, StringComparison.Ordinal) &&
                               node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal &&
                               !plan.IsSignalRemoved(node.Id))
                .ToArray();
            foreach (var slot in descriptor.Slots.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                var entries = signalNodes
                    .Where(node => string.Equals(
                        plan.GetEffectiveSlot(node),
                        slot,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(node => new ContainerToFeeVisualSignalEntryVM(node, plan, FindTreeNode(node.Id)))
                    .ToArray();
                SignalSlots.Add(new ContainerToFeeVisualSignalSlotVM(containerId, slot, entries));
            }
        }

        HashSet<string> nodeIds = plan.Nodes
            .Where(node => node.ContainerId == containerId)
            .Select(node => node.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (VisualEdge edge in plan.Edges.Where(edge =>
                     nodeIds.Contains(edge.SourceId) || nodeIds.Contains(edge.TargetId)))
            VisibleEdges.Add(ContainerToFeeVisualEdgeVM.Create(edge, plan));

        // Initial display anchor only. Calling the public setter here would
        // navigate from a selected parent container to its first child target.
        _selectedTarget = Targets.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedTarget));
        FeeObjectsView.Refresh();
    }

    private void ApplyTreeFilter()
    {
        foreach (ContainerToFeeVisualTreeNodeVM root in TreeRoots)
        {
            if (SelectedTreeStatusFilter.Key == VisualStatusFilterKey.All)
                root.ApplyFilter(TreeFilter);
            else
                root.ApplyContainerStatusFilter(TreeFilter, MatchesTreeStatus);
        }
    }

    private void ApplyTreeSort()
    {
        SortContainerChildren(TreeRoots);
        foreach (var root in TreeRoots)
        foreach (var node in root.SelfAndDescendants())
            SortContainerChildren(node.Children);
    }

    private void SortContainerChildren(ObservableCollection<ContainerToFeeVisualTreeNodeVM> nodes)
    {
        if (!nodes.Any(node => node.Kind == VisualNodeKind.Container))
            return;
        var sorted = SelectedTreeSort.Key switch
        {
            VisualTreeSortKey.ContainerName => nodes
                .OrderBy(node => node.Kind == VisualNodeKind.Container ? 0 : 1)
                .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase),
            _ => nodes
                .OrderBy(node => node.Kind == VisualNodeKind.Container ? 0 : 1)
                .ThenBy(node => node.TypeName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase),
        };
        nodes.ReplaceWith(sorted.ToArray());
    }

    private bool MatchesTreeStatus(ContainerToFeeVisualTreeNodeVM node) =>
        SelectedTreeStatusFilter.Key switch
        {
            VisualStatusFilterKey.Verified => node.EffectiveState.Kind == ContainerToFeeVisualNodeStateKind.Verified,
            VisualStatusFilterKey.LinkMissing => node.EffectiveState.Kind == ContainerToFeeVisualNodeStateKind.FoundUnlinked,
            VisualStatusFilterKey.Unassigned => node.EffectiveState.Kind == ContainerToFeeVisualNodeStateKind.None,
            VisualStatusFilterKey.Planned => node.EffectiveState.Kind == ContainerToFeeVisualNodeStateKind.Planned,
            VisualStatusFilterKey.Error => node.HasValidationError ||
                                           node.EffectiveState.Kind == ContainerToFeeVisualNodeStateKind.Missing,
            _ => true,
        };

    private bool FilterFeeObject(object item)
    {
        if (item is not ContainerToFeeVisualFeeObjectVM feeObject)
            return false;

        if (!string.IsNullOrWhiteSpace(FeeObjectFilter) &&
            !feeObject.Name.Contains(FeeObjectFilter, StringComparison.OrdinalIgnoreCase) &&
            !feeObject.FeeType.Contains(FeeObjectFilter, StringComparison.OrdinalIgnoreCase) &&
            !feeObject.TypeName.Contains(FeeObjectFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        if (SelectedFeeObjectStatusFilter.Key == VisualStatusFilterKey.Verified && !feeObject.IsValid)
            return false;
        if (SelectedFeeObjectStatusFilter.Key == VisualStatusFilterKey.LinkMissing && !feeObject.IsLinkMissing)
            return false;
        if (SelectedFeeObjectStatusFilter.Key == VisualStatusFilterKey.Error && !feeObject.HasError)
            return false;
        if (SelectedFeeObjectStatusFilter.Key == VisualStatusFilterKey.Planned)
            return false; // Diese Liste enthält ausschließlich bereits vorhandene FEE-Objekte.

        return !ShowOnlyCompatibleFeeObjects ||
               SelectedTarget is null ||
               SelectedTarget.Model.CanAssign(feeObject.Model);
    }

    private bool FilterFeeSignal(object item)
    {
        if (item is not ContainerToFeeVisualFeeSignalVM signal)
            return false;

        if (!GetSelectedInterfaceGuids().Contains(signal.Model.InterfaceGuidString))
            return false;
        if (SelectedFeeSignalStatusFilter.Key == VisualStatusFilterKey.Assigned && !signal.IsAssigned)
            return false;
        if (SelectedFeeSignalStatusFilter.Key == VisualStatusFilterKey.Unassigned && signal.IsAssigned)
            return false;
        if (SelectedFeeSignalStatusFilter.Key == VisualStatusFilterKey.Error && !signal.HasError)
            return false;
        if (SelectedFeeSignalStatusFilter.Key == VisualStatusFilterKey.Valid && signal.HasError)
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
        var selectedGuids = GetSelectedInterfaceGuids();
        var scopedSignals = selectedGuids.Count == 0
            ? Array.Empty<VisualFeeSignal>()
            : source.Where(signal => selectedGuids.Contains(signal.InterfaceGuidString)).ToArray();
        var verifiedNodeIds = _planService.FindVerifiedSignalNodeIds();
        AvailableFeeSignals.ReplaceWith(source.Select(signal =>
            new ContainerToFeeVisualFeeSignalVM(
                signal,
                scopedSignals,
                plan,
                verifiedNodeIds.TryGetValue(signal.GuidString, out var nodeIds) ? nodeIds : [])));
        FeeSignalsView.Refresh();
    }

    private void RefreshFeeObjectProjection(IReadOnlyList<VisualFeeObject>? objects = null)
    {
        var plan = _planService.CurrentPlan;
        var source = objects ?? AvailableFeeObjects.Select(item => item.Model).ToArray();
        AvailableFeeObjects.ReplaceWith(
            source.Select(item => new ContainerToFeeVisualFeeObjectVM(
                item,
                plan,
                _planService.GetFeeObjectConnectionSummary(item.Id),
                _planService.IsDuplicateFeeObjectConfirmed(item.Id))));
        FeeObjectsView.Refresh();
    }

    private void RefreshFeeInterfaceProjection(IReadOnlyList<VisualFeeInterface>? interfaces = null)
    {
        var selectedGuids = (_planService.CurrentPlan?.ExistingInterfaceSelections
                             .Select(item => item.InterfaceGuid) ??
                             AvailableFeeInterfaces.Where(item => item.IsSelected)
                                 .Select(item => item.GuidString))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var source = interfaces ?? AvailableFeeInterfaces
            .Where(item => item.Model is not null)
            .Select(item => item.Model!)
            .ToArray();
        var distinct = source
            .Where(item => !string.IsNullOrWhiteSpace(item.GuidString))
            .GroupBy(item => item.GuidString, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _isRefreshingFeeInterfaceProjection = true;
        try
        {
            foreach (var existing in AvailableFeeInterfaces)
                existing.PropertyChanged -= OnAvailableInterfacePropertyChanged;
            AvailableFeeInterfaces.Clear();
            foreach (var item in distinct)
            {
                var viewModel = new ContainerToFeeVisualFeeInterfaceVM(
                    item,
                    selectedGuids.Contains(item.GuidString));
                viewModel.PropertyChanged += OnAvailableInterfacePropertyChanged;
                AvailableFeeInterfaces.Add(viewModel);
            }
            _selectedExistingInterface = AvailableFeeInterfaces.FirstOrDefault(item => item.IsSelected);
            _selectedInterfaceGuids.Clear();
            _selectedInterfaceGuids.UnionWith(selectedGuids);
            OnPropertyChanged(nameof(SelectedExistingInterface));
        }
        finally
        {
            _isRefreshingFeeInterfaceProjection = false;
        }
        NotifyExistingInterfaceSelectionChanged();
    }

    private void OnAvailableInterfacePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ContainerToFeeVisualFeeInterfaceVM.IsSelected) ||
            _isRefreshingFeeInterfaceProjection ||
            _isApplyingPlan)
            return;

        CommitExistingInterfaceSelection();
    }

    private void CommitExistingInterfaceSelection()
    {
        var selected = AvailableFeeInterfaces
            .Where(item => item.IsSelected && item.Model is not null)
            .Select(item => item.Model!)
            .ToArray();
        _selectedInterfaceGuids.Clear();
        _selectedInterfaceGuids.UnionWith(selected.Select(item => item.GuidString));
        _selectedExistingInterface = AvailableFeeInterfaces.FirstOrDefault(item => item.IsSelected);
        _planService.SetExistingInterfaces(selected);
        ApplyDiscoveredSignalStates(_planService.DiscoveredFeeSignals);
        RefreshFeeSignalProjection(_planService.DiscoveredFeeSignals);
        NotifyExistingInterfaceSelectionChanged();
    }

    private IReadOnlySet<string> GetSelectedInterfaceGuids() => _selectedInterfaceGuids;

    private void NotifyExistingInterfaceSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedExistingInterface));
        OnPropertyChanged(nameof(HasSelectedExistingInterfaces));
        OnPropertyChanged(nameof(SelectedExistingInterfacesSummary));
        OnPropertyChanged(nameof(CanLinkSignalsOnly));
        OnPropertyChanged(nameof(LinkSignalsOnlyUnavailableReason));
        FeeSignalsView.Refresh();
        InvalidateCommands();
    }

    private void PublishIssues(IEnumerable<VisualIssue> issues)
    {
        var issueList = issues.ToArray();
        Issues.ReplaceWith(issueList);
        foreach (var issue in issueList.Where(issue =>
                     issue.Message.Contains("nicht eindeutig", StringComparison.OrdinalIgnoreCase) ||
                     issue.Code.Contains("SIGNAL", StringComparison.OrdinalIgnoreCase) &&
                     issue.Code.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase)))
        {
            AddOperationDetail(
                "Signalauflösung",
                issue.Message + " Prüfen: ausgewählte Interfaces, Tag/Adresse/Pfad, IO-Typ/Usage, gespeicherte GUID und doppelte FEE-Signale. Abweichende vorhandene Variablen werden nicht automatisch überschrieben.");
        }
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
        AddOperationDetail("Nicht ausgeführt", message);
    }

    private void AddOperationDetail(string category, string message)
    {
        OperationDetails.Insert(0, new ContainerToFeeVisualOperationDetailVM(
            DateTimeOffset.Now,
            category,
            message));
        while (OperationDetails.Count > 200)
            OperationDetails.RemoveAt(OperationDetails.Count - 1);
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
        ApplyTreeFilter();
    }

    private static ContainerToFeeVisualNodeState RefreshAggregateState(
        ContainerToFeeVisualTreeNodeVM node)
    {
        var childStates = node.Children.Select(RefreshAggregateState).ToArray();
        if (node.Kind is not (VisualNodeKind.Container or VisualNodeKind.Group or VisualNodeKind.Root or VisualNodeKind.SimObjectTarget) ||
            childStates.Length == 0)
        {
            return node.EffectiveState;
        }

        var effectiveChildStates = node.Kind == VisualNodeKind.SimObjectTarget
            ? childStates.Append(node.EffectiveState).ToArray()
            : childStates;
        var relevantStates = effectiveChildStates
            .Where(state => state.Kind != ContainerToFeeVisualNodeStateKind.None)
            .ToArray();
        var aggregate = relevantStates.Any(state => state.Kind == ContainerToFeeVisualNodeStateKind.Missing)
            ? ContainerToFeeVisualNodeState.Missing
            : relevantStates.Any(state => state.Kind == ContainerToFeeVisualNodeStateKind.FoundUnlinked)
                ? ContainerToFeeVisualNodeState.FoundUnlinked
                : relevantStates.Any(state => state.Kind == ContainerToFeeVisualNodeStateKind.Planned)
                    ? ContainerToFeeVisualNodeState.Planned
                    : relevantStates.Length == 0
                        ? ContainerToFeeVisualNodeState.None
                        : ContainerToFeeVisualNodeState.Verified;
        node.ApplyExecutionState(aggregate);
        return node.EffectiveState;
    }

    private static FeeConnectionService ResolveConnection() =>
        Services.Connection ?? new FeeConnectionService();

    private void SelectRelatedTreeNode(ContainerToFeeVisualTreeNodeVM? node)
    {
        if (node is null || _isSynchronizingSelections)
            return;
        _isSynchronizingSelections = true;
        try
        {
            if (!node.IsVisible)
            {
                TreeFilter = string.Empty;
                SelectedTreeStatusFilter = VisualStatusFilterOption.All;
                node = FindTreeNode(node.Id) ?? node;
            }
            ExpandTreeAncestors(node);
            if (ReferenceEquals(SelectedTreeNode, node))
            {
                // Force the reveal behavior to run again when a related item
                // points at the tree node that is already selected but has
                // meanwhile been scrolled out of view.
                _selectedTreeNode = null;
                OnPropertyChanged(nameof(SelectedTreeNode));
            }
            SelectedTreeNode = node;
            node.IsExpanded = true;
        }
        finally
        {
            _isSynchronizingSelections = false;
        }
        // Selecting an item in an outer list first moves the tree selection.
        // Project that tree node back into every related list afterwards so
        // all synchronized views receive the same visible selection anchor.
        SynchronizeSelectionsFromTree(node);
    }

    private void ExpandTreeAncestors(ContainerToFeeVisualTreeNodeVM node)
    {
        var parentId = node.ParentId;
        while (!string.IsNullOrWhiteSpace(parentId))
        {
            var parent = FindTreeNode(parentId);
            if (parent is null)
                break;
            parent.IsExpanded = true;
            parentId = parent.ParentId;
        }
    }

    private void SynchronizeSelectionsFromTree(ContainerToFeeVisualTreeNodeVM? node)
    {
        if (!SynchronizeRelatedSelections || node is null || _isSynchronizingSelections)
            return;
        _isSynchronizingSelections = true;
        try
        {
            ClearSynchronizationMatches();
            var plan = _planService.CurrentPlan;
            var scopeRoot = node.Kind == VisualNodeKind.SimObject && node.ParentId is not null
                ? FindTreeNode(node.ParentId) ?? node
                : node;
            var scope = scopeRoot.Kind is VisualNodeKind.Container or VisualNodeKind.Group or VisualNodeKind.SimObjectTarget
                ? scopeRoot.SelfAndDescendants().ToArray()
                : [scopeRoot];
            foreach (var item in scope)
            {
                item.IsSynchronizationMatch = true;
                if (scopeRoot.Kind == VisualNodeKind.Container)
                    item.IsExpanded = true;
            }

            var nodeIds = scope.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var targetIds = scope
                .Where(item => item.Kind == VisualNodeKind.SimObjectTarget)
                .Select(item => item.Id)
                .Concat(scope.Where(item => item.Kind == VisualNodeKind.SimObject)
                    .Select(item => item.ParentId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Cast<string>())
                .ToHashSet(StringComparer.Ordinal);
            var feeObjectIds = scope
                .Select(item => item.FeeObjectId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .Concat(plan?.Assignments
                    .Where(assignment => targetIds.Contains(assignment.TargetId))
                    .Select(assignment => assignment.FeeObjectId) ?? [])
                .ToHashSet(StringComparer.Ordinal);
            var signalNodeIds = scope
                .Where(item => item.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal)
                .Select(item => item.Id)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var item in AvailableFeeObjects)
                item.IsSynchronizationMatch = feeObjectIds.Contains(item.Id);
            foreach (var item in Targets)
                item.IsSynchronizationMatch = targetIds.Contains(item.Id);
            foreach (var item in AvailableFeeSignals)
                item.IsSynchronizationMatch = item.AssignedNodeIds.Any(signalNodeIds.Contains);
            foreach (var item in SignalSlots)
                item.IsSynchronizationMatch = item.Assignments.Any(assignment => signalNodeIds.Contains(assignment.NodeId));

            EnsureSynchronizedAnchorsAreVisible();
            SetSelectionAnchor(ref _selectedFeeObject,
                AvailableFeeObjects.FirstOrDefault(item => item.IsSynchronizationMatch),
                nameof(SelectedFeeObject));
            SetSelectionAnchor(ref _selectedTarget,
                Targets.FirstOrDefault(item => item.IsSynchronizationMatch),
                nameof(SelectedTarget));
            SetSelectionAnchor(ref _selectedFeeSignal,
                AvailableFeeSignals.FirstOrDefault(item => item.IsSynchronizationMatch),
                nameof(SelectedFeeSignal));
            SetSelectionAnchor(ref _selectedSignalSlot,
                SignalSlots.FirstOrDefault(item => item.IsSynchronizationMatch),
                nameof(SelectedSignalSlot));

            var relatedIssue = Issues.FirstOrDefault(issue =>
                issue.NodeId is not null &&
                (nodeIds.Contains(issue.NodeId) ||
                 scope.Any(item => string.Equals(item.ContainerId, issue.NodeId, StringComparison.Ordinal))));
            SetSelectionAnchor(ref _selectedIssue, relatedIssue, nameof(SelectedIssue));
            FeeObjectsView.Refresh();
        }
        finally
        {
            _isSynchronizingSelections = false;
        }
    }

    private void EnsureSynchronizedAnchorsAreVisible()
    {
        var feeObjectAnchor = AvailableFeeObjects.FirstOrDefault(item => item.IsSynchronizationMatch);
        if (feeObjectAnchor is not null && !FeeObjectsView.Contains(feeObjectAnchor))
        {
            _feeObjectFilter = string.Empty;
            _selectedFeeObjectStatusFilter = VisualStatusFilterOption.All;
            _showOnlyCompatibleFeeObjects = false;
            OnPropertyChanged(nameof(FeeObjectFilter));
            OnPropertyChanged(nameof(SelectedFeeObjectStatusFilter));
            OnPropertyChanged(nameof(ShowOnlyCompatibleFeeObjects));
            FeeObjectsView.Refresh();
        }

        var feeSignalAnchor = AvailableFeeSignals.FirstOrDefault(item => item.IsSynchronizationMatch);
        if (feeSignalAnchor is not null && !FeeSignalsView.Contains(feeSignalAnchor))
        {
            _feeSignalFilter = string.Empty;
            _selectedFeeSignalStatusFilter = VisualStatusFilterOption.All;
            OnPropertyChanged(nameof(FeeSignalFilter));
            OnPropertyChanged(nameof(SelectedFeeSignalStatusFilter));
            FeeSignalsView.Refresh();
        }
    }

    private void ClearSynchronizationMatches()
    {
        foreach (var item in TreeRoots.SelectMany(root => root.SelfAndDescendants()))
            item.IsSynchronizationMatch = false;
        foreach (var item in Targets)
            item.IsSynchronizationMatch = false;
        foreach (var item in SignalSlots)
            item.IsSynchronizationMatch = false;
        foreach (var item in AvailableFeeObjects)
            item.IsSynchronizationMatch = false;
        foreach (var item in AvailableFeeSignals)
            item.IsSynchronizationMatch = false;
    }

    private void SetSelectionAnchor<T>(ref T? field, T? value, string propertyName)
        where T : class
    {
        if (ReferenceEquals(field, value) && value is not null)
        {
            field = null;
            OnPropertyChanged(propertyName);
        }
        field = value;
        OnPropertyChanged(propertyName);
    }

}

internal sealed record FeeRefreshSnapshot(
    int ObjectCount,
    int ContainerObjectCount,
    int SignalCount,
    int InterfaceCount,
    int SignalLinkCount,
    int SimObjectLinkCount,
    int AutomaticAssignmentCount,
    int VerifiedContainerCount);

public enum VisualStatusFilterKey
{
    All,
    Verified,
    LinkMissing,
    Assigned,
    Unassigned,
    Planned,
    Error,
    Valid,
}

public enum VisualTreeSortKey
{
    ContainerType,
    ContainerName,
}

public sealed record VisualTreeSortOption(VisualTreeSortKey Key, string DisplayName)
{
    public static VisualTreeSortOption ByType { get; } =
        new(VisualTreeSortKey.ContainerType, "Containertyp (A-Z)");
    public static IReadOnlyList<VisualTreeSortOption> Options { get; } =
    [
        ByType,
        new(VisualTreeSortKey.ContainerName, "Containername (A-Z)"),
    ];
}

public sealed record VisualStatusFilterOption(VisualStatusFilterKey Key, string DisplayName)
{
    public static VisualStatusFilterOption All { get; } = new(VisualStatusFilterKey.All, "Alle");
    public static IReadOnlyList<VisualStatusFilterOption> TreeOptions { get; } =
    [
        All,
        new(VisualStatusFilterKey.Verified, "Alles vorhanden (grün)"),
        new(VisualStatusFilterKey.LinkMissing, "Verknüpfung fehlt"),
        new(VisualStatusFilterKey.Unassigned, "Nicht zugewiesen"),
        new(VisualStatusFilterKey.Planned, "Wird erzeugt"),
        new(VisualStatusFilterKey.Error, "Fehler / unklar"),
    ];
    public static IReadOnlyList<VisualStatusFilterOption> AssignmentOptions { get; } =
    [
        All,
        new(VisualStatusFilterKey.Verified, "Alles gültig (grün)"),
        new(VisualStatusFilterKey.LinkMissing, "Verknüpfung fehlt (lila)"),
        new(VisualStatusFilterKey.Error, "Fehler (rot)"),
        new(VisualStatusFilterKey.Planned, "Fehlt, wird erzeugt (gelb)"),
    ];
    public static IReadOnlyList<VisualStatusFilterOption> SignalOptions { get; } =
    [
        All,
        new(VisualStatusFilterKey.Assigned, "Zugewiesen"),
        new(VisualStatusFilterKey.Unassigned, "Nicht zugewiesen"),
        new(VisualStatusFilterKey.Error, "Fehler / Duplikat"),
        new(VisualStatusFilterKey.Valid, "Ohne Fehler"),
    ];
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
    private bool _isFilterContext;
    private bool _isGenerationSelected;
    private readonly Action<string, bool> _setGenerationSelected;
    private readonly Action<string, string> _setSlotOverride;
    private readonly bool _canSelectGeneration;
    private readonly bool _containerSelected;
    private IReadOnlyList<string> _validationErrors = Array.Empty<string>();
    private ContainerToFeeVisualNodeState _executionState;
    private bool _isSynchronizationMatch;

    public ContainerToFeeVisualTreeNodeVM(
        VisualNode model,
        IEnumerable<ContainerToFeeVisualTreeNodeVM> children,
        bool isGenerationSelected,
        bool canSelectGeneration,
        string effectiveSlot,
        IReadOnlyList<string> allowedSlots,
        string? feeObjectId,
        bool hasDuplicateIdentity,
        bool isDuplicateConfirmed,
        bool containerSelected,
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
        _containerSelected = containerSelected;
        _executionState = simObjectState;
        _linkedObjectDescription = linkedObjectDescription;
        _validationErrors = validationErrors.ToArray();
        _setGenerationSelected = setGenerationSelected;
        _setSlotOverride = setSlotOverride;
        _slot = effectiveSlot;
        AllowedSlots = allowedSlots;
        FeeObjectId = feeObjectId;
        HasDuplicateIdentity = hasDuplicateIdentity;
        IsDuplicateConfirmed = isDuplicateConfirmed;
        _isExpanded = !model.IsTechnical && model.Kind is VisualNodeKind.Root or VisualNodeKind.Container;
    }

    public VisualNode Model { get; }
    public string Id => Model.Id;
    public string? ContainerId => Model.ContainerId;
    public string Name => Model.Name;
    public string TypeName => Model.TypeName;
    public string DisplayTypeLabel => Kind == VisualNodeKind.Container
        ? $"Container: {TypeName}"
        : string.IsNullOrWhiteSpace(TypeName) || string.Equals(TypeName, Kind.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Kind.ToString()
            : $"{Kind}: {TypeName}";
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
    public string? FeeObjectId { get; }
    public string? ParentId => Model.ParentId;
    public bool HasDuplicateIdentity { get; }
    public bool IsDuplicateConfirmed { get; }
    public bool CanConfirmDuplicate => Kind == VisualNodeKind.SimObject &&
                                       HasDuplicateIdentity &&
                                       !IsDuplicateConfirmed;
    public string DuplicateConfirmationText => IsDuplicateConfirmed
        ? "Mehrfachfund bestätigt"
        : "Mehrfachfund bestätigen";
    public bool CanEditSlot => Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal &&
                               AllowedSlots.Count > 0;
    public string SourceLocation => Model.SourceLocation;
    public VisualNodeKind Kind => Model.Kind;
    public bool IsTechnical => Model.IsTechnical;
    public bool SupportsCreation => Model.SupportsCreation;
    public bool CanSelectGeneration => _canSelectGeneration;
    public bool ShowGenerationAction => Kind is not (VisualNodeKind.Root or VisualNodeKind.Group or VisualNodeKind.Container);
    public bool WillGenerateObject => ShowGenerationAction && _containerSelected &&
                                      EffectiveState.Kind == ContainerToFeeVisualNodeStateKind.Planned;
    public string GenerationActionText => WillGenerateObject
        ? "Wird neu erzeugt"
        : EffectiveState.Kind == ContainerToFeeVisualNodeStateKind.Verified
            ? "Vorhanden und vollständig verknüpft"
            : EffectiveState.Kind == ContainerToFeeVisualNodeStateKind.FoundUnlinked
                ? "Vorhanden; fehlende Verknüpfung wird ergänzt"
                : _containerSelected ? "Wird nicht erzeugt" : "Container ist abgewählt";
    public ContainerToFeeVisualNodeState SimObjectState => _executionState;
    public ContainerToFeeVisualNodeState EffectiveState => HasValidationError
        ? ContainerToFeeVisualNodeState.Missing
        : _executionState;
    public string StateBackground => HasValidationError ? "#FFFFCDD2" : _executionState.Background;
    public string DisplayBackground => _isFilterContext ? "Transparent" : StateBackground;
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
            $"Sucht das Interface-Signal „{Name}“ in allen ausgewählten Interfaces; fehlt es, wird es in einem neuen AutoGenerated-Interface erzeugt und dem angegebenen Slot zugewiesen.",
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
        OnPropertyChanged(nameof(DisplayBackground));
        OnPropertyChanged(nameof(WillGenerateObject));
        OnPropertyChanged(nameof(GenerationActionText));
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
            OnPropertyChanged(nameof(DisplayBackground));
            OnPropertyChanged(nameof(SimObjectStateDescription));
            OnPropertyChanged(nameof(WillGenerateObject));
            OnPropertyChanged(nameof(GenerationActionText));
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

    public bool IsSynchronizationMatch
    {
        get => _isSynchronizationMatch;
        set => SetPropertyChange(ref _isSynchronizationMatch, value);
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

    public bool ApplyFilter(
        string filter,
        Func<ContainerToFeeVisualTreeNodeVM, bool>? statusPredicate = null)
    {
        SetFilterContext(false);
        bool childMatches = Children.Aggregate(
            false,
            (match, child) => child.ApplyFilter(filter, statusPredicate) || match);
        bool selfMatches = string.IsNullOrWhiteSpace(filter) ||
                           Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                           TypeName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                           Slot.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                           SourceLocation.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                           LinkedObjectDescription.Contains(filter, StringComparison.OrdinalIgnoreCase);
        selfMatches = selfMatches && (statusPredicate?.Invoke(this) ?? true);
        IsVisible = selfMatches || childMatches;
        if (!string.IsNullOrWhiteSpace(filter) && childMatches)
            IsExpanded = true;
        return IsVisible;
    }

    /// <summary>
    /// Applies state filters at container level. Ancestors are retained only as
    /// transparent navigation context, preventing a green filter from showing a
    /// yellow or purple container just because one nested item is green.
    /// </summary>
    public bool ApplyContainerStatusFilter(
        string filter,
        Func<ContainerToFeeVisualTreeNodeVM, bool> containerPredicate)
    {
        if (Kind == VisualNodeKind.Container)
        {
            SetFilterContext(false);
            if (!containerPredicate(this))
            {
                SetSubtreeVisible(false);
                return false;
            }
            return ApplyFilter(filter);
        }

        var childMatches = Children.Aggregate(
            false,
            (match, child) => child.ApplyContainerStatusFilter(filter, containerPredicate) || match);
        SetFilterContext(childMatches);
        IsVisible = childMatches;
        return childMatches;
    }

    private void SetSubtreeVisible(bool visible)
    {
        SetFilterContext(false);
        IsVisible = visible;
        foreach (var child in Children)
            child.SetSubtreeVisible(visible);
    }

    private void SetFilterContext(bool value)
    {
        if (_isFilterContext == value)
            return;
        _isFilterContext = value;
        OnPropertyChanged(nameof(DisplayBackground));
    }
}

public sealed class ContainerToFeeVisualTargetVM : MvvmBase
{
    private IReadOnlyList<string> _validationErrors = Array.Empty<string>();
    private bool _isSynchronizationMatch;
    public ContainerToFeeVisualTargetVM(
        VisualSimObjectTarget model,
        IEnumerable<VisualAssignment> assignments,
        bool isCreationRequested,
        IEnumerable<VisualIssue> issues,
        Func<string, VisualSimObjectConnectionState>? connectionResolver = null)
    {
        Model = model;
        Assignments = new ObservableCollection<ContainerToFeeVisualAssignmentVM>(
            assignments.Select(assignment => new ContainerToFeeVisualAssignmentVM(
                assignment,
                model.AllowedTypeName,
                connectionResolver?.Invoke(assignment.FeeObjectId))));
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
    public bool IsConnectionVerified => IsAssigned && Assignments.All(assignment => assignment.IsConnectionVerified);
    public bool IsCreationRequested { get; }
    public IReadOnlyList<string> ValidationErrors => _validationErrors;
    public bool HasValidationError => ValidationErrors.Count > 0;
    public string ValidationErrorText => string.Join(Environment.NewLine, ValidationErrors);
    public string AssignmentState => IsAssigned
        ? IsConnectionVerified
            ? "Vorhandene FEE-SimObjects gefunden und verknüpft"
            : "FEE-SimObject gefunden; mindestens eine Verknüpfung fehlt"
        : IsCreationRequested
            ? "Wird bei der Generierung erzeugt"
            : "Simulationsobjekt fehlt – Zuordnung erforderlich";
    public bool IsSynchronizationMatch
    {
        get => _isSynchronizationMatch;
        set
        {
            if (_isSynchronizationMatch == value)
                return;
            _isSynchronizationMatch = value;
            OnPropertyChanged();
        }
    }
    public string StateBackground => HasValidationError
        ? "#FFFFCDD2"
        : IsAssigned
        ? IsConnectionVerified ? "#FFC6EFCE" : "#FFE8D9F3"
        : IsCreationRequested
            ? "#FFFFF2CC"
            : "#FFEF9A9A";
    public string ExecutionDescription => IsAssigned
        ? $"Verwendet {Assignments.Count} vorhandene(s) FEE-SimObject(s) und schreibt die Zuordnung zum Ziel „{DisplayName}“ ({AllowedTypeName})."
        : IsCreationRequested
            ? $"Erzeugt ein neues kompatibles FEE-SimObject vom Typ „{AllowedTypeName}“ und verknüpft es mit „{DisplayName}“."
            : $"Sucht ein vorhandenes FEE-SimObject vom Typ „{AllowedTypeName}“ für die Verknüpfung mit „{DisplayName}“.";
    public string ToolTipText
    {
        get
        {
            var details = string.Join(Environment.NewLine, Assignments.Select(assignment => assignment.ToolTipText));
            return string.Join(Environment.NewLine, new[]
            {
                ExecutionDescription,
                details,
                HasValidationError ? ValidationErrorText : string.Empty,
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
    }

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
public sealed class ContainerToFeeVisualFeeInterfaceVM : MvvmBase
{
    private bool _isSelected;

    public ContainerToFeeVisualFeeInterfaceVM(VisualFeeInterface? model, bool isSelected = false)
    {
        Model = model;
        _isSelected = isSelected;
    }

    public static ContainerToFeeVisualFeeInterfaceVM None { get; } = new(null);

    public VisualFeeInterface? Model { get; }
    public bool IsNone => Model is null;
    public string GuidString => Model?.GuidString ?? string.Empty;
    public string Name => Model?.Name ?? "Keins";
    public int SignalCount => Model?.SignalCount ?? 0;
    public string DisplayName => IsNone ? "Keins" : $"{Name} ({SignalCount} Signale)";

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
}

/// <summary>Presentation state showing whether an FEE object is already assigned.</summary>
public sealed class ContainerToFeeVisualFeeObjectVM : MvvmBase
{
    private bool _isSynchronizationMatch;

    public ContainerToFeeVisualFeeObjectVM(
        VisualFeeObject model,
        VisualPlan? plan,
        VisualFeeObjectConnectionSummary connectionSummary,
        bool isDuplicateConfirmed = false)
    {
        Model = model;
        ConnectionSummary = connectionSummary;
        IsDuplicateConfirmed = isDuplicateConfirmed;
        var assignments = plan?.Assignments
            .Where(assignment => assignment.FeeObjectId == model.Id)
            .ToArray() ?? [];
        AssignedTargets = assignments
            .Select(assignment => DescribeAssignment(plan, assignment))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public VisualFeeObject Model { get; }
    public VisualFeeObjectConnectionSummary ConnectionSummary { get; }
    public string Id => Model.Id;
    public string GuidString => Model.GuidString;
    public string Name => Model.Name;
    public string TypeName => Model.TypeName;
    public string FeeType => Model.FeeType;
    public string ParentName => string.IsNullOrWhiteSpace(Model.ParentName) ? "<oberste Ebene>" : Model.ParentName;
    public bool HasExactDuplicate => Model.HasExactDuplicate;
    public bool IsDuplicateConfirmed { get; }
    public IReadOnlyList<string> AssignedTargets { get; }
    public bool IsAssigned => AssignedTargets.Count > 0;
    public bool HasLiveConnections => ConnectionSummary.HasConnections;
    public bool HasError => HasExactDuplicate && !IsDuplicateConfirmed;
    public bool IsValid => !HasError && (IsAssigned || HasLiveConnections);
    public bool IsLinkMissing => !HasError && !HasLiveConnections;
    public string ConnectionStateText => !ConnectionSummary.WasRead
        ? "Live-Verknüpfungen noch nicht vollständig gelesen"
        : HasLiveConnections
            ? $"{ConnectionSummary.Details.Count} Live-Verknüpfung(en) gefunden"
            : "Keine Live-Verknüpfung zu Logik, Signal oder SimObject gefunden";
    public IReadOnlyList<string> ConnectionDetails => ConnectionSummary.Details;
    public string DuplicateStateText => !HasExactDuplicate
        ? string.Empty
        : IsDuplicateConfirmed
            ? "MEHRFACHFUND BESTÄTIGT"
        : !ConnectionSummary.WasRead
            ? "DUPLIKAT – Verknüpfungsstatus unbekannt"
            : HasLiveConnections
                ? "DUPLIKAT – LIVE VERKNÜPFT (vor Löschen genau prüfen)"
                : "DUPLIKAT – UNVERKNÜPFT (möglicher Löschkandidat)";
    public string PlanAssignmentText => IsAssigned
        ? $"Plan-Zuordnung: {string.Join("; ", AssignedTargets)}"
        : "Keine Plan-Zuordnung";
    public string AssignmentText => HasExactDuplicate
        ? $"{DuplicateStateText}. Parent: {ParentName}. {ConnectionStateText}. " +
          PlanAssignmentText + "."
        : IsAssigned
            ? $"{PlanAssignmentText}. {ConnectionStateText}"
            : ConnectionStateText;
    public string StateBackground => HasError
        ? "#FFFFC7CE"
        : IsValid ? "#FFC6EFCE" : "#FFE8D9F3";
    public string StateBorderBrush => HasExactDuplicate
        ? IsDuplicateConfirmed
            ? "#FF2E7D32"
            : HasLiveConnections ? "#FFC88719" : "#FFC00000"
        : HasLiveConnections ? "#FF548235" : "Transparent";
    public string StateForeground => IsDuplicateConfirmed
        ? "#FF2E7D32"
        : HasExactDuplicate && !ConnectionSummary.WasRead
        ? "#FF5B2C83"
        : HasExactDuplicate && !HasLiveConnections ? "#FF9C0006" : "#FF375623";
    public bool IsSynchronizationMatch
    {
        get => _isSynchronizationMatch;
        set
        {
            if (_isSynchronizationMatch == value)
                return;
            _isSynchronizationMatch = value;
            OnPropertyChanged();
        }
    }
    public string DeleteToolTip => HasLiveConnections
        ? $"ACHTUNG: '{Name}' besitzt Live-Verknüpfungen. {ConnectionStateText}. Löscht genau dieses ausgewählte Objekt unter '{ParentName}' dauerhaft aus FEE."
        : $"Löscht genau das ausgewählte Objekt '{Name}' unter '{ParentName}' dauerhaft aus FEE. {ConnectionStateText}.";

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
public sealed class ContainerToFeeVisualFeeSignalVM : MvvmBase
{
    private bool _isSynchronizationMatch;
    public ContainerToFeeVisualFeeSignalVM(
        VisualFeeSignal model,
        IReadOnlyCollection<VisualFeeSignal> allSignals,
        VisualPlan? plan,
        IReadOnlyCollection<string>? verifiedNodeIds = null)
    {
        Model = model;
        var assignments = plan?.SignalAssignments
            .Where(item => string.Equals(
                item.FeeSignalGuid,
                model.GuidString,
                StringComparison.OrdinalIgnoreCase))
            .ToArray() ?? [];
        var explicitTargets = assignments
            .Select(item => DescribeSignalAssignment(plan, item))
            .ToArray();
        var liveTargets = verifiedNodeIds?
            .Select(nodeId => DescribeSignalNode(plan, nodeId))
            .ToArray() ?? [];
        AssignedNodeIds = assignments.Select(item => item.SignalNodeId)
            .Concat(verifiedNodeIds ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        AssignedTargets = explicitTargets.Concat(liveTargets)
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
    public IReadOnlyList<string> AssignedNodeIds { get; }
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
    public string ToolTipText => $"Signal: {Tag}{Environment.NewLine}{AssignmentText}";
    public bool IsSynchronizationMatch
    {
        get => _isSynchronizationMatch;
        set
        {
            if (_isSynchronizationMatch == value)
                return;
            _isSynchronizationMatch = value;
            OnPropertyChanged();
        }
    }

    private static string DescribeSignalAssignment(VisualPlan? plan, VisualSignalAssignment assignment)
        => DescribeSignalNode(plan, assignment.SignalNodeId);

    private static string DescribeSignalNode(VisualPlan? plan, string signalNodeId)
    {
        var node = plan?.FindNode(signalNodeId);
        if (node is null)
            return signalNodeId;
        var container = node.ContainerId is null ? null : plan?.FindNode(node.ContainerId);
        return $"{container?.Name ?? "—"} / {node.Name} [{plan?.GetEffectiveSlot(node) ?? node.Slot}]";
    }
}

public sealed class ContainerToFeeVisualAssignmentVM
{
    public ContainerToFeeVisualAssignmentVM(
        VisualAssignment model,
        string feeType,
        VisualSimObjectConnectionState? connectionState = null)
    {
        Model = model;
        FeeType = feeType;
        ConnectionState = connectionState ?? new(
            VisualSimObjectConnectionKind.NotRead,
            "FEE-Verknüpfungen wurden noch nicht aktualisiert.");
    }

    public VisualAssignment Model { get; }
    public string TargetId => Model.TargetId;
    public string FeeObjectId => Model.FeeObjectId;
    public string FeeObjectName => Model.FeeObjectName;
    public string FeeObjectTypeName => Model.FeeObjectTypeName;
    public string FeeType { get; }
    public VisualSimObjectConnectionState ConnectionState { get; }
    public bool IsConnectionVerified => ConnectionState.IsVerified;
    public string StateBackground => IsConnectionVerified ? "#FFC6EFCE" : "#FFE8D9F3";
    public string ToolTipText => $"{FeeObjectName} -> {ConnectionState.Description}";
}

public sealed class ContainerToFeeVisualSignalEntryVM
{
    public ContainerToFeeVisualSignalEntryVM(
        VisualNode model,
        VisualPlan plan,
        ContainerToFeeVisualTreeNodeVM? treeNode = null)
    {
        Model = model;
        Slot = plan.GetEffectiveSlot(model);
        IsAdded = plan.IsAddedSignal(model.Id);
        var assignment = plan.SignalAssignments.LastOrDefault(item =>
            string.Equals(item.SignalNodeId, model.Id, StringComparison.Ordinal));
        FeeSignalGuid = assignment?.FeeSignalGuid ?? string.Empty;
        AssignedFeeSignal = assignment is not null
            ? $"{assignment.FeeSignalTag} · {assignment.FeeInterfaceName}"
            : treeNode is not null &&
              (treeNode.EffectiveState.Kind is ContainerToFeeVisualNodeStateKind.Verified or ContainerToFeeVisualNodeStateKind.FoundUnlinked) &&
              treeNode.LinkedObjectDescription.Contains("FEE-Signal", StringComparison.OrdinalIgnoreCase)
                ? treeNode.LinkedObjectDescription
                : "Keine vorhandene FEE-Zuordnung";
        State = treeNode?.EffectiveState ?? ContainerToFeeVisualNodeState.Planned;
        ToolTipText = string.Join(
            Environment.NewLine,
            new[]
            {
                $"Signal: {Name}",
                $"Slot: {Slot}",
                AssignedFeeSignal,
                treeNode?.LinkedObjectDescription,
            }.Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    public VisualNode Model { get; }
    public string NodeId => Model.Id;
    public string Name => Model.Name;
    public string Slot { get; }
    public string SourceLocation => Model.SourceLocation;
    public bool IsAdded { get; }
    public string FeeSignalGuid { get; }
    public string AssignedFeeSignal { get; }
    public ContainerToFeeVisualNodeState State { get; }
    public string StateBackground => State.Background;
    public string ToolTipText { get; }
    public string RemoveToolTip => IsAdded
        ? "Zusätzliches Signal vollständig aus dem bearbeiteten ContainerFile entfernen"
        : "Signal aus dem wirksamen ContainerFile entfernen; die Quelldatei bleibt unverändert und Rückgängig stellt es wieder her";
}

/// <summary>
/// One declared signal slot of the selected container. Empty entries remain
/// visible so users can assign existing FEE signals without first creating an
/// artificial XML signal row.
/// </summary>
public sealed class ContainerToFeeVisualSignalSlotVM : MvvmBase
{
    private bool _isSynchronizationMatch;
    public ContainerToFeeVisualSignalSlotVM(
        string containerId,
        string slot,
        IEnumerable<ContainerToFeeVisualSignalEntryVM> assignments)
    {
        ContainerId = containerId;
        Slot = slot;
        Assignments = new ObservableCollection<ContainerToFeeVisualSignalEntryVM>(assignments);
    }

    public string ContainerId { get; }
    public string Slot { get; }
    public string? PrimaryNodeId => Assignments.FirstOrDefault()?.NodeId;
    public bool AllowMultiSelect => global::VIBN_Tools.ContainerGeneration.Models.ContainerSlotMultiplicityPolicy.IsPlcInput(Slot);
    public string SelectionMode => AllowMultiSelect ? "Mehrfachbelegung" : "Einzelbelegung";
    public ObservableCollection<ContainerToFeeVisualSignalEntryVM> Assignments { get; }
    public string AssignmentState => Assignments.Count == 0
        ? "Noch kein Containersignal belegt"
        : $"{Assignments.Count} Containersignal(e) belegt";
    public string StateBackground
    {
        get
        {
            var states = Assignments.Select(assignment => assignment.State.Kind).ToArray();
            if (states.Length == 0)
                return "#FFF3F5F7";
            if (states.Any(state => state == ContainerToFeeVisualNodeStateKind.Missing))
                return ContainerToFeeVisualNodeState.Missing.Background;
            if (states.Any(state => state is ContainerToFeeVisualNodeStateKind.Planned or ContainerToFeeVisualNodeStateKind.None))
                return ContainerToFeeVisualNodeState.Planned.Background;
            if (states.Any(state => state == ContainerToFeeVisualNodeStateKind.FoundUnlinked))
                return ContainerToFeeVisualNodeState.FoundUnlinked.Background;
            return ContainerToFeeVisualNodeState.Verified.Background;
        }
    }
    public string ToolTipText =>
        $"Signalslot '{Slot}' · {SelectionMode}. Hier dürfen nur FEE-Signale abgelegt werden; SimObjects werden abgewiesen.";
    public bool IsSynchronizationMatch
    {
        get => _isSynchronizationMatch;
        set
        {
            if (_isSynchronizationMatch == value)
                return;
            _isSynchronizationMatch = value;
            OnPropertyChanged();
        }
    }
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

public sealed record ContainerToFeeVisualOperationDetailVM(
    DateTimeOffset Timestamp,
    string Category,
    string Message)
{
    public string TimestampText => Timestamp.ToString("HH:mm:ss");
    public string ToolTipText => $"{Timestamp:dd.MM.yyyy HH:mm:ss} · {Category}{Environment.NewLine}{Message}";
}
