using System.Collections.ObjectModel;

namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>Types of objects shown in the declarative Container2FEE plan.</summary>
public enum VisualNodeKind
{
    Root,
    Container,
    Group,
    BasicFrame,
    Interface,
    Logic,
    SimObjectTarget,
    SimObject,
    Signal,
    TechnicalHelper,
    UnknownSignal
}

/// <summary>Relationship types used by the visual plan.</summary>
public enum VisualEdgeKind
{
    ParentChild,
    SignalToSlot,
    SlotToSlot,
    SimObjectAssignment
}

public enum VisualIssueSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>A validation or runtime message which can be associated with one node.</summary>
public sealed record VisualIssue(
    VisualIssueSeverity Severity,
    string Code,
    string Message,
    string? NodeId = null);

/// <summary>One object, signal or technical helper in the generation plan.</summary>
public sealed class VisualNode
{
    private readonly List<VisualNode> _children = [];

    internal VisualNode(
        string id,
        string? parentId,
        string? containerId,
        VisualNodeKind kind,
        string name,
        string typeName,
        string? slot,
        bool isTechnical,
        bool supportsCreation = false,
        string? sourceLocation = null)
    {
        Id = id;
        ParentId = parentId;
        ContainerId = containerId;
        Kind = kind;
        Name = name;
        TypeName = typeName;
        Slot = slot;
        IsTechnical = isTechnical;
        SupportsCreation = supportsCreation;
        SourceLocation = sourceLocation ?? string.Empty;
    }

    public string Id { get; }

    public string? ParentId { get; }

    public string? ContainerId { get; }

    public VisualNodeKind Kind { get; }

    public string Name { get; }

    public string TypeName { get; }

    public string? Slot { get; }

    /// <summary>
    /// Technical nodes may be hidden or collapsed by the UI without losing
    /// generation information.
    /// </summary>
    public bool IsTechnical { get; }

    /// <summary>
    /// Indicates that the unchanged legacy container can create its default
    /// simulation object when no suitable existing object is assigned.
    /// </summary>
    public bool SupportsCreation { get; }

    /// <summary>Signal address or symbolic path from the source XML, if applicable.</summary>
    public string SourceLocation { get; }

    public IReadOnlyList<VisualNode> Children => _children;

    internal void AddChild(VisualNode node) => _children.Add(node);
}

/// <summary>A directed relationship between two stable plan node IDs.</summary>
public sealed record VisualEdge(
    string Id,
    string SourceId,
    string TargetId,
    VisualEdgeKind Kind,
    string Label);

/// <summary>
/// Lightweight FEE object identity. No SDK object is exposed to the WPF layer,
/// which keeps drag/drop and sidecar persistence deterministic.
/// </summary>
public sealed class VisualFeeObject
{
    internal VisualFeeObject(
        string id,
        string guidString,
        string name,
        string typeName,
        string feeType,
        IReadOnlyCollection<string> assignableTypeNames)
    {
        Id = id;
        GuidString = guidString;
        Name = name;
        TypeName = typeName;
        FeeType = feeType;
        AssignableTypeNames = assignableTypeNames;
    }

    public string Id { get; }

    public string GuidString { get; }

    public string Name { get; }

    public string TypeName { get; }

    public string FeeType { get; }

    /// <summary>CLR type names including all base classes.</summary>
    public IReadOnlyCollection<string> AssignableTypeNames { get; }
}

/// <summary>Kind of non-draggable FEE object used to colour the generation plan.</summary>
public enum VisualFeeContainerObjectKind
{
    Logic,
    Cabinet,
    CabinetElement,
}

/// <summary>
/// Lightweight identity of an existing logic or cabinet object. These objects
/// are deliberately kept separate from draggable SimObjects.
/// </summary>
public sealed record VisualFeeContainerObject(
    string GuidString,
    string Name,
    VisualFeeContainerObjectKind Kind,
    string Definition);

public enum VisualFeeNodePresenceKind
{
    Found,
    Planned,
    Ambiguous,
}

/// <summary>Presence result for one logic/cabinet node in the visual tree.</summary>
public sealed record VisualFeeNodePresence(
    string NodeId,
    VisualFeeNodePresenceKind Kind,
    string Description);

/// <summary>Read-only identity of an existing FEE interface selectable by the user.</summary>
public sealed class VisualFeeInterface
{
    public VisualFeeInterface(
        string guidString,
        string name,
        string providerName,
        int signalCount)
    {
        GuidString = guidString;
        Name = name;
        ProviderName = providerName;
        SignalCount = signalCount;
    }

    public string GuidString { get; }

    public string Name { get; }

    public string ProviderName { get; }

    public int SignalCount { get; }
}

/// <summary>Read-only identity of one signal found in an existing FEE interface.</summary>
public sealed record VisualFeeSignal(
    string GuidString,
    string InterfaceGuidString,
    string InterfaceName,
    string Tag,
    string Address,
    string Path,
    string DataType,
    string Usage)
{
    public string Location => string.IsNullOrWhiteSpace(Path) ? Address : Path;
}

/// <summary>A typed drop target declared by the unchanged legacy container.</summary>
public sealed class VisualSimObjectTarget
{
    internal VisualSimObjectTarget(
        string id,
        string containerId,
        string displayName,
        string allowedTypeName,
        bool allowMultiSelect)
    {
        Id = id;
        ContainerId = containerId;
        DisplayName = displayName;
        AllowedTypeName = allowedTypeName;
        AllowMultiSelect = allowMultiSelect;
    }

    public string Id { get; }

    public string ContainerId { get; }

    public string DisplayName { get; }

    public string AllowedTypeName { get; }

    public bool AllowMultiSelect { get; }

    public bool CanAssign(VisualFeeObject feeObject) =>
        feeObject is not null &&
        feeObject.AssignableTypeNames.Contains(AllowedTypeName, StringComparer.Ordinal);
}

/// <summary>Persistent assignment from one plan target to an existing FEE object.</summary>
public sealed record VisualAssignment(
    string TargetId,
    string FeeObjectId,
    string FeeObjectName,
    string FeeObjectTypeName);

/// <summary>Explicit user-approved binding of a plan signal to an existing FEE variable.</summary>
public sealed record VisualSignalAssignment(
    string SignalNodeId,
    string FeeSignalGuid,
    string FeeSignalTag,
    string FeeInterfaceName);

/// <summary>
/// A signal explicitly added to one container by drag/drop. It is stored in
/// the sidecar and projected into the effective ContainerFile at execution
/// time; the imported source XML is never modified implicitly.
/// </summary>
public sealed record VisualAddedSignal(
    string NodeId,
    string ContainerId,
    string SignalGroupId,
    string FeeSignalGuid,
    string FeeSignalTag,
    string FeeInterfaceGuid,
    string FeeInterfaceName,
    string Address,
    string Path,
    string DataType,
    string Usage);

/// <summary>
/// User-selected replacement for a slot from the imported ContainerFile. The
/// source XML remains untouched; execution and provenance use the effective
/// slot from this override.
/// </summary>
public sealed record VisualSlotOverride(string SignalNodeId, string Slot);

/// <summary>
/// Per-container override for creating a missing default simulation object.
/// Missing entries mean <c>true</c>; only opt-outs are persisted.
/// </summary>
public sealed record VisualCreationRequest(string ContainerId, bool IsRequested);

/// <summary>
/// Explicit per-container generation state. Entries are persisted only for
/// deselected containers so plans created before this feature continue to
/// generate every supported container.
/// </summary>
public sealed record VisualGenerationSelection(string ContainerId, bool IsSelected);

/// <summary>
/// Selects whether signals are created for a container. Missing entries mean
/// <c>true</c> so plans from older versions retain their previous behavior.
/// </summary>
public sealed record VisualSignalCreationSelection(string ContainerId, bool CreateSignals);

/// <summary>Persistent reference to the interface used when existing signals are reused.</summary>
public sealed record VisualExistingInterfaceSelection(string InterfaceGuid, string InterfaceName);

/// <summary>
/// Complete immutable-facing plan. Mutations are restricted to the plan
/// service so every change remains validated and undoable.
/// </summary>
public sealed class VisualPlan
{
    private readonly List<VisualAssignment> _assignments;
    private readonly List<VisualCreationRequest> _creationRequests;
    private readonly List<VisualGenerationSelection> _generationSelections;
    private readonly List<VisualSignalCreationSelection> _signalCreationSelections;
    private readonly List<VisualSignalAssignment> _signalAssignments;
    private readonly List<VisualAddedSignal> _addedSignals;
    private readonly List<VisualSlotOverride> _slotOverrides;
    private readonly HashSet<string> _removedSignalNodeIds;
    private readonly List<VisualEdge> _edges;
    private readonly List<VisualNode> _nodes;
    private readonly HashSet<string> _sourceNodeIds;

    internal VisualPlan(
        string sourceXmlPath,
        string sidecarPath,
        string sourceFingerprint,
        IReadOnlyList<VisualNode> nodes,
        IReadOnlyList<VisualNode> roots,
        IReadOnlyList<VisualEdge> edges,
        IReadOnlyList<VisualSimObjectTarget> targets,
        IReadOnlyList<VisualAssignment>? assignments,
        IReadOnlyList<VisualCreationRequest>? creationRequests,
        IReadOnlyList<VisualGenerationSelection>? generationSelections,
        IReadOnlyList<VisualSignalCreationSelection>? signalCreationSelections,
        IReadOnlyList<VisualSignalAssignment>? signalAssignments,
        IReadOnlyList<VisualAddedSignal>? addedSignals,
        IReadOnlyList<VisualSlotOverride>? slotOverrides,
        IReadOnlyList<string>? removedSignalNodeIds,
        VisualExistingInterfaceSelection? existingInterfaceSelection,
        IReadOnlyList<VisualIssue> issues)
    {
        SourceXmlPath = sourceXmlPath;
        SidecarPath = sidecarPath;
        SourceFingerprint = sourceFingerprint;
        _nodes = [.. nodes];
        _sourceNodeIds = nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        Roots = roots;
        _edges = [.. edges];
        Targets = targets;
        _assignments = assignments is null ? [] : [.. assignments];
        _creationRequests = creationRequests is null ? [] : [.. creationRequests];
        _generationSelections = generationSelections is null ? [] : [.. generationSelections];
        _signalCreationSelections = signalCreationSelections is null ? [] : [.. signalCreationSelections];
        _signalAssignments = signalAssignments is null ? [] : [.. signalAssignments];
        _addedSignals = [];
        _slotOverrides = slotOverrides is null ? [] : [.. slotOverrides];
        _removedSignalNodeIds = removedSignalNodeIds is null
            ? new(StringComparer.Ordinal)
            : new(removedSignalNodeIds, StringComparer.Ordinal);
        ReplaceAddedSignals(addedSignals ?? []);
        ExistingInterfaceSelection = existingInterfaceSelection;
        Issues = issues;
    }

    public string SourceXmlPath { get; }

    public string SidecarPath { get; internal set; }

    public string SourceFingerprint { get; }

    public IReadOnlyList<VisualNode> Nodes => _nodes;

    public IReadOnlyList<VisualNode> Roots { get; }

    public IReadOnlyList<VisualEdge> Edges => _edges;

    public IReadOnlyList<VisualSimObjectTarget> Targets { get; }

    public IReadOnlyList<VisualAssignment> Assignments => _assignments;

    public IReadOnlyList<VisualCreationRequest> CreationRequests => _creationRequests;

    public IReadOnlyList<VisualGenerationSelection> GenerationSelections => _generationSelections;

    public IReadOnlyList<VisualSignalCreationSelection> SignalCreationSelections =>
        _signalCreationSelections;

    public IReadOnlyList<VisualSignalAssignment> SignalAssignments => _signalAssignments;

    public IReadOnlyList<VisualAddedSignal> AddedSignals => _addedSignals;

    public IReadOnlyList<VisualSlotOverride> SlotOverrides => _slotOverrides;

    public IReadOnlySet<string> RemovedSignalNodeIds => _removedSignalNodeIds;

    public VisualExistingInterfaceSelection? ExistingInterfaceSelection { get; private set; }

    public IReadOnlyList<VisualIssue> Issues { get; }

    public VisualNode? FindNode(string id) =>
        Nodes.FirstOrDefault(node => string.Equals(node.Id, id, StringComparison.Ordinal));

    public bool IsAddedSignal(string nodeId) =>
        _addedSignals.Any(item => string.Equals(item.NodeId, nodeId, StringComparison.Ordinal));

    public bool IsSignalRemoved(string nodeId) => _removedSignalNodeIds.Contains(nodeId);

    public VisualSimObjectTarget? FindTarget(string id) =>
        Targets.FirstOrDefault(target => string.Equals(target.Id, id, StringComparison.Ordinal));

    public bool IsCreationRequested(string containerId) =>
        !_creationRequests.Any(request =>
            !request.IsRequested &&
            string.Equals(request.ContainerId, containerId, StringComparison.Ordinal));

    public bool IsGenerationSelected(string containerId) =>
        !_generationSelections.Any(selection =>
            !selection.IsSelected &&
            string.Equals(selection.ContainerId, containerId, StringComparison.Ordinal));

    public bool ShouldCreateSignals(string containerId) =>
        !_signalCreationSelections.Any(selection =>
            !selection.CreateSignals &&
            string.Equals(selection.ContainerId, containerId, StringComparison.Ordinal));

    public string GetEffectiveSlot(VisualNode node) =>
        _slotOverrides.LastOrDefault(item =>
            string.Equals(item.SignalNodeId, node.Id, StringComparison.Ordinal))?.Slot ??
        node.Slot ?? string.Empty;

    internal void ReplaceAssignments(IEnumerable<VisualAssignment> assignments)
    {
        var replacement = assignments.ToArray();
        _assignments.Clear();
        _assignments.AddRange(replacement);
        RebuildAssignmentEdges();
    }

    internal void ReplaceCreationRequests(IEnumerable<VisualCreationRequest> requests)
    {
        var replacement = requests.Where(request => !request.IsRequested).ToArray();
        _creationRequests.Clear();
        _creationRequests.AddRange(replacement);
    }

    internal void ReplaceGenerationSelections(IEnumerable<VisualGenerationSelection> selections)
    {
        var replacement = selections.Where(selection => !selection.IsSelected).ToArray();
        _generationSelections.Clear();
        _generationSelections.AddRange(replacement);
    }

    internal void ReplaceSignalCreationSelections(IEnumerable<VisualSignalCreationSelection> selections)
    {
        var replacement = selections.Where(selection => !selection.CreateSignals).ToArray();
        _signalCreationSelections.Clear();
        _signalCreationSelections.AddRange(replacement);
    }

    internal void ReplaceSignalAssignments(IEnumerable<VisualSignalAssignment> assignments)
    {
        var replacement = assignments.ToArray();
        _signalAssignments.Clear();
        _signalAssignments.AddRange(replacement);
    }

    internal void ReplaceAddedSignals(IEnumerable<VisualAddedSignal> signals)
    {
        var replacement = signals
            .Where(item => !_sourceNodeIds.Contains(item.NodeId))
            .DistinctBy(item => item.NodeId, StringComparer.Ordinal)
            .ToArray();
        _addedSignals.Clear();
        _addedSignals.AddRange(replacement);
        _nodes.RemoveAll(node => !_sourceNodeIds.Contains(node.Id));
        _nodes.AddRange(replacement.Select(item => new VisualNode(
            item.NodeId,
            item.SignalGroupId,
            item.ContainerId,
            VisualNodeKind.Signal,
            item.FeeSignalTag,
            item.DataType,
            string.Empty,
            isTechnical: false,
            sourceLocation: string.IsNullOrWhiteSpace(item.Path) ? item.Address : item.Path)));
    }

    internal void ReplaceSlotOverrides(IEnumerable<VisualSlotOverride> overrides)
    {
        var replacement = overrides.ToArray();
        _slotOverrides.Clear();
        _slotOverrides.AddRange(replacement);
    }

    internal void ReplaceRemovedSignalNodeIds(IEnumerable<string> nodeIds)
    {
        _removedSignalNodeIds.Clear();
        foreach (var nodeId in nodeIds.Where(nodeId =>
                     _sourceNodeIds.Contains(nodeId) &&
                     FindNode(nodeId)?.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal))
            _removedSignalNodeIds.Add(nodeId);
    }

    internal void SetExistingInterfaceSelection(VisualExistingInterfaceSelection? selection) =>
        ExistingInterfaceSelection = selection;

    internal void RebuildAssignmentEdges()
    {
        _edges.RemoveAll(edge => edge.Kind == VisualEdgeKind.SimObjectAssignment);
        _edges.AddRange(_assignments.Select(assignment => new VisualEdge(
            $"edge:assignment:{StableId.Encode(assignment.TargetId)}:{StableId.Encode(assignment.FeeObjectId)}",
            assignment.TargetId,
            assignment.FeeObjectId,
            VisualEdgeKind.SimObjectAssignment,
            "FEE-Zuordnung")));
    }
}

public sealed record VisualPlanLoadResult(
    bool Success,
    VisualPlan? Plan,
    IReadOnlyList<VisualIssue> Issues,
    string Message);

public sealed record VisualAssignmentResult(
    bool Success,
    string Message,
    VisualAssignment? Assignment,
    IReadOnlyList<VisualIssue> Issues);

public sealed record VisualValidationResult(
    bool IsValid,
    IReadOnlyList<VisualIssue> Issues);

public sealed record VisualExecutionResult(
    bool Success,
    string Message,
    IReadOnlyList<VisualIssue> Issues);

public sealed record VisualSignalAssignmentResult(
    bool Success,
    string Message,
    VisualSignalAssignment? Assignment,
    IReadOnlyList<VisualIssue> Issues);

public sealed record VisualGenerationProgress(int Percent, string Message);

public sealed class VisualPlanChangedEventArgs(VisualPlan plan) : EventArgs
{
    public VisualPlan Plan { get; } = plan;
}

internal static class StableId
{
    public static string Encode(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "empty";

        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16]
            .ToLowerInvariant();
    }
}
