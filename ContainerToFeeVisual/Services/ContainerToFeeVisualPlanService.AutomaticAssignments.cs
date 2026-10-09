using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFeeVisual;

public sealed partial class ContainerToFeeVisualPlanService
{
    private HashSet<string>? _selectedSimObjectRootGuids;
    private IReadOnlyList<FeeAbstractObject>? _scopedSceneCache;
    private ExistingContainerLogicResolver? _logicResolverCache;
    private HashSet<string>? _scopedFeeObjectIds;

    private IReadOnlyList<FeeAbstractObject> ScopedSceneObjects() => _scopedSceneCache ??=
        _selectedSimObjectRootGuids is null ? _runtimeSceneObjects : _runtimeSceneObjects
            .Where(item => _selectedSimObjectRootGuids.Contains(FeeSimObjectDiscovery.ResolveRoot(item).GuidString)).ToArray();

    private ExistingContainerLogicResolver ScopedLogicResolver => _logicResolverCache ??= new(ScopedSceneObjects());

    private void InvalidateRootScope()
    {
        _scopedSceneCache = null;
        _logicResolverCache = null;
        _scopedFeeObjectIds = null;
    }

    private bool IsAssignedObjectInSelectedRoots(VisualAssignment assignment) =>
        (_scopedFeeObjectIds ??= _feeObjects.Where(IsFeeObjectInSelectedRoots).Select(item => item.Id).ToHashSet(StringComparer.Ordinal))
            .Contains(assignment.FeeObjectId);

    private IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> CreateLinkReadSlots()
    {
        var slots = new Dictionary<Guid, HashSet<string>>();
        void Add(Guid guid, string slot)
        {
            if (!slots.TryGetValue(guid, out var names)) slots[guid] = names = new(StringComparer.OrdinalIgnoreCase);
            names.Add(slot);
        }
        if (CurrentPlan is not { } plan) return new Dictionary<Guid, IReadOnlyCollection<string>>();
        foreach (var container in plan.Nodes.Where(node => node.Kind == VisualNodeKind.Container))
        {
            if (!ContainerMetadataCatalog.TryGet(container.TypeName, out var descriptor)) continue;
            var logics = FindExistingContainerLogics(container.Id);
            var runtime = descriptor.Factory();
            var signalSlots = plan.Nodes.Where(node => node.ContainerId == container.Id &&
                    node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal && !plan.IsSignalRemoved(node.Id))
                .Select(node => ContainerSignalSlotMap.RuntimeSlot(runtime, container.TypeName, plan.GetEffectiveSlot(node))).ToArray();
            foreach (var logic in logics)
                foreach (var slot in signalSlots) Add(Guid.Parse(logic.GuidString), slot);
            foreach (var target in plan.Targets.Where(target => target.ContainerId == container.Id))
            {
                var assigned = plan.Assignments.Where(item => item.TargetId == target.Id && IsAssignedObjectInSelectedRoots(item)).ToArray();
                for (var index = 0; index < assigned.Length; index++)
                {
                    if (!_runtimeObjects.TryGetValue(assigned[index].FeeObjectId, out var item)) continue;
                    foreach (var link in SimObjectLinkMap.Create(container.TypeName, item, index))
                    {
                        Add(item.Guid, link.ObjectSlot);
                        foreach (var logic in logics) Add(Guid.Parse(logic.GuidString), link.LogicSlot);
                    }
                    if (string.IsNullOrWhiteSpace(descriptor.ExpectedLogicName))
                        foreach (var slot in signalSlots) Add(item.Guid, slot);
                }
            }
        }
        foreach (var helper in ScopedSceneObjects().Where(FeeSlotSnapshot.IsRoutingHelper))
        {
            foreach (var slot in FeeSlotSnapshot.RoutingSlots(helper, "Output 01")) Add(helper.Guid, slot);
            Add(helper.Guid, "Output 01");
        }
        return slots.ToDictionary(item => item.Key, item => (IReadOnlyCollection<string>)item.Value);
    }

    public VisualSimObjectConnectionState? GetFeeObjectPlanConnectionState(string feeObjectId)
    {
        if (CurrentPlan is not { } plan) return null;
        var states = plan.Assignments.Where(item => item.FeeObjectId == feeObjectId)
            .Select(item => GetSimObjectConnectionState(item.TargetId, feeObjectId)).ToList();
        var feeObject = FindFeeObject(feeObjectId);
        if (feeObject is not null && string.Equals(feeObject.FeeType, "LogicObject", StringComparison.OrdinalIgnoreCase))
            foreach (var container in plan.Nodes.Where(node => node.Kind == VisualNodeKind.Container &&
                string.Equals(node.Name.Trim(), feeObject.Name.Trim(), StringComparison.OrdinalIgnoreCase) &&
                FindExistingContainerLogics(node.Id) is { Count: 1 } logics &&
                string.Equals(logics[0].GuidString, feeObject.GuidString, StringComparison.OrdinalIgnoreCase)))
            {
                var selected = plan.ExistingInterfaceSelections.Select(item => item.InterfaceGuid).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var signals = plan.Nodes.Where(node => node.ContainerId == container.Id &&
                    node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal && !plan.IsSignalRemoved(node.Id)).ToArray();
                var targets = plan.Targets.Where(target => target.ContainerId == container.Id).ToArray();
                var linked = signals.All(node => ResolveSignalForNode(plan, node, selected) is { } signal &&
                    GetSignalConnectionState(node.Id, signal.GuidString).IsVerified) && targets.All(target => GetSimObjectConnectionState(target.Id).IsVerified) &&
                    (signals.Length + targets.Length > 0 || GetFeeObjectConnectionSummary(feeObjectId).HasConnections);
                states.Add(new(linked ? VisualSimObjectConnectionKind.Linked : VisualSimObjectConnectionKind.LinkMissing,
                    linked ? "Alle erforderlichen Verknüpfungen dieser Containerlogik bestätigt." : "Containerlogik gefunden; erforderliche Verknüpfungen fehlen oder sind noch nicht gelesen."));
            }
        return states.FirstOrDefault(state => !state.IsVerified) ?? states.FirstOrDefault();
    }

    private IReadOnlyList<VisualFeeContainerObject> ScopedContainerObjects()
    {
        if (_selectedSimObjectRootGuids is null) return _feeContainerObjects;
        var guids = ScopedSceneObjects().Select(item => item.GuidString).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _feeContainerObjects.Where(item => guids.Contains(item.GuidString)).ToArray();
    }

    public IReadOnlyList<VisualFeeContainerObject> FindExistingContainerLogics(string containerId)
    {
        var plan = CurrentPlan;
        var container = plan?.FindNode(containerId);
        if (plan is null || container is null || !ContainerMetadataCatalog.TryGet(container.TypeName, out var descriptor)) return [];
        if (_runtimeSceneObjects.Count == 0)
            return ScopedContainerObjects().Where(item => item.Kind == VisualFeeContainerObjectKind.Logic &&
                string.Equals(item.Name.Trim(), container.Name.Trim(), StringComparison.OrdinalIgnoreCase) &&
                ContainerMetadataCatalog.IsSameLogicDefinition(descriptor.ExpectedLogicName, item.Definition)).ToArray();
        var targets = plan.Targets.Where(target => target.ContainerId == containerId).Select(target => target.Id).ToHashSet(StringComparer.Ordinal);
        var objects = plan.Assignments.Where(assignment => targets.Contains(assignment.TargetId))
            .Select(assignment => _runtimeObjects.GetValueOrDefault(assignment.FeeObjectId))
            .Where(item => item is not null && (_selectedSimObjectRootGuids is null ||
                _selectedSimObjectRootGuids.Contains(FeeSimObjectDiscovery.ResolveRoot(item).GuidString))).Cast<FeeAbstractObject>();
        return ScopedLogicResolver.Find(container, objects, _feeSimObjectLinks).Select(item => new VisualFeeContainerObject(item.GuidString,
            item.Name ?? "", VisualFeeContainerObjectKind.Logic, item.LogicDefinitionName ?? "")).ToArray();
    }

    private async Task EnsureLinkSnapshotAsync(bool includeInterfaces, CancellationToken token)
    {
        var revision = Services.Connection?.ConnectionRevision ?? -1;
        if (!_hasDiscoveredFeeObjects || _feeObjectConnectionRevision != revision)
            await DiscoverFeeObjectsAsync(token);
        if (includeInterfaces && (!_hasDiscoveredFeeInterfaces || _feeInterfaceConnectionRevision != revision))
            await DiscoverFeeInterfacesAsync(token);
        token.ThrowIfCancellationRequested();
    }

    private void ApplyConfirmedSimObjectLinks(IEnumerable<VisualFeeObjectLink> confirmed)
    {
        var links = confirmed.ToArray();
        if (links.Length == 0) return;
        _feeSimObjectLinks = _feeSimObjectLinks.Concat(links).Distinct().ToArray();
        _feeSignalLinks = FeeSlotSnapshot.ExpandSignalRoutes(_feeSignalLinks, _runtimeSceneObjects, _feeSimObjectLinks);
        _hasDiscoveredFeeSimObjectLinks = true;
    }

    public IReadOnlyDictionary<Guid, string> DiscoveredTopLevelBasicFrames => _topLevelBasicFrames;

    public void SetSimObjectRoots(IEnumerable<string> rootGuids)
    {
        _selectedSimObjectRootGuids = rootGuids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        InvalidateRootScope();
    }

    public bool IsFeeObjectInSelectedRoots(VisualFeeObject item) =>
        _selectedSimObjectRootGuids is null || _selectedSimObjectRootGuids.Contains(item.RootGuidString);

    private IReadOnlyDictionary<string, FeeAbstractObject> ScopedRuntimeObjects()
    {
        var ids = _feeObjects.Where(IsFeeObjectInSelectedRoots).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        return _runtimeObjects.Where(item => ids.Contains(item.Key)).ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
    }

    private int AddAutomaticSignalAssignments(VisualPlan plan, List<VisualSignalAssignment> assignments)
    {
        var selected = plan.ExistingInterfaceSelections.Select(item => item.InterfaceGuid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0) return 0;
        var scoped = _feeSignals.Where(signal => selected.Contains(signal.InterfaceGuidString))
            .DistinctBy(signal => signal.GuidString, StringComparer.OrdinalIgnoreCase).ToArray();
        var byName = scoped.ToLookup(signal => signal.Tag.Trim(), StringComparer.OrdinalIgnoreCase);
        var byLocation = scoped.SelectMany(signal => new[] { signal.Address, signal.Path }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => (Key: VisualSignalAssignmentMatcher.NormalizeLocation(value), Signal: signal)))
            .ToLookup(item => item.Key, item => item.Signal, StringComparer.Ordinal);
        var activeGuids = scoped.Select(signal => signal.GuidString).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assignedNodes = assignments.Where(item => activeGuids.Contains(item.FeeSignalGuid))
            .Select(item => item.SignalNodeId).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var node in plan.Nodes.Where(node => node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal &&
                     !plan.IsSignalRemoved(node.Id) && !assignedNodes.Contains(node.Id)))
        {
            var matches = byName[node.Name.Trim()].Where(signal => VisualSignalAssignmentMatcher.Matches(node, signal)).ToArray();
            if (matches.Length == 0 && !string.IsNullOrWhiteSpace(node.SourceLocation))
                matches = byLocation[VisualSignalAssignmentMatcher.NormalizeLocation(node.SourceLocation)]
                    .Where(signal => VisualSignalAssignmentMatcher.HasCompatibleType(node.TypeName, signal.DataType))
                    .DistinctBy(signal => signal.GuidString, StringComparer.OrdinalIgnoreCase).ToArray();
            if (matches.Length > 1 && _hasDiscoveredFeeSignalLinks)
                matches = matches.Where(signal => GetSignalConnectionState(node.Id, signal.GuidString).IsVerified).ToArray();
            if (matches.Length != 1) continue;
            var match = matches[0];
            assignments.RemoveAll(item => item.SignalNodeId == node.Id);
            assignments.Add(new VisualSignalAssignment(node.Id, match.GuidString, match.Tag, match.InterfaceName));
            assignedNodes.Add(node.Id);
            added++;
        }
        return added;
    }

    /// <summary>Completion is proved by the live objects and every expected endpoint, never by old XML tags.</summary>
    public IReadOnlySet<string> FindFullyVerifiedContainerIds()
    {
        var verified = new HashSet<string>(StringComparer.Ordinal);
        var plan = CurrentPlan;
        if (plan is null || !_hasDiscoveredFeeObjects || !_hasDiscoveredFeeInterfaces) return verified;
        var errors = Validate().Issues.Where(issue => issue.Severity == VisualIssueSeverity.Error).ToArray();
        if (errors.Any(issue => issue.NodeId is null)) return verified;
        var selectedInterfaces = plan.ExistingInterfaceSelections.Select(item => item.InterfaceGuid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var container in plan.Nodes.Where(node => node.Kind == VisualNodeKind.Container))
        {
            if (!ContainerMetadataCatalog.TryGet(container.TypeName, out var descriptor) ||
                errors.Any(issue => (issue.RelatedIds ?? []).Prepend(issue.NodeId ?? "").Any(id =>
                    id == container.Id || plan.FindNode(id)?.ContainerId == container.Id || plan.FindTarget(id)?.ContainerId == container.Id))) continue;
            if (!string.IsNullOrWhiteSpace(descriptor.ExpectedLogicName) && FindExistingContainerLogics(container.Id).Count != 1) continue;
            if (!string.IsNullOrWhiteSpace(descriptor.ExpectedCabinetElementType) && ScopedContainerObjects().Count(item =>
                    item.Kind == VisualFeeContainerObjectKind.CabinetElement &&
                    string.Equals(item.Name, container.Name, StringComparison.OrdinalIgnoreCase) &&
                    NormalizeToken(item.Definition) == NormalizeToken(descriptor.ExpectedCabinetElementType)) != 1) continue;
            var targets = plan.Targets.Where(target => target.ContainerId == container.Id).ToArray();
            if (targets.Any(target => !GetSimObjectConnectionState(target.Id).IsVerified)) continue;
            var targetIds = targets.Select(target => target.Id).ToHashSet(StringComparer.Ordinal);
            var objects = plan.Assignments.Where(assignment => targetIds.Contains(assignment.TargetId) && IsAssignedObjectInSelectedRoots(assignment))
                .Select(assignment => _runtimeObjects.GetValueOrDefault(assignment.FeeObjectId)).ToArray();
            if (objects.Any(item => item is FeeJoint joint && joint.ControlType.ToString() != "Position" ||
                item is FeeFloor floor && !floor.UseCollisionSlot)) continue;
            var signals = plan.Nodes.Where(node => node.ContainerId == container.Id &&
                node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal && !plan.IsSignalRemoved(node.Id)).ToArray();
            if (signals.Any(node => ResolveSignalForNode(plan, node, selectedInterfaces) is not { } signal ||
                !VisualSignalAssignmentMatcher.HasCompatibleType(node.TypeName, signal.DataType) ||
                !GetSignalConnectionState(node.Id, signal.GuidString).IsVerified)) continue;
            if (descriptor.TechnicalHelpers.Any(helper => VisualFeeTechnicalHelperResolver.IsExpectedType(helper, "BoolNot")) &&
                FindTechnicalHelpers(container.Id, "Bool-NOT-Hilfslogik").Count != 1) continue;
            if (signals.Length > 0 || targets.Length > 0 || !string.IsNullOrWhiteSpace(descriptor.ExpectedLogicName) ||
                !string.IsNullOrWhiteSpace(descriptor.ExpectedCabinetElementType)) verified.Add(container.Id);
        }
        return verified;
    }
}

internal static class VisualSignalAssignmentMatcher
{
    internal static bool Matches(VisualNode node, VisualFeeSignal signal) =>
        string.Equals(node.Name.Trim(), signal.Tag.Trim(), StringComparison.OrdinalIgnoreCase) &&
        HasCompatibleType(node.TypeName, signal.DataType) &&
        (string.IsNullOrWhiteSpace(node.SourceLocation) || SameLocation(node.SourceLocation, signal.Address) ||
            SameLocation(node.SourceLocation, signal.Path));

    internal static bool SameLocation(string left, string right) => NormalizeLocation(left) == NormalizeLocation(right);

    internal static bool HasCompatibleType(string left, string right)
    {
        static string Normalize(string value) => value.Trim().ToUpperInvariant() switch
        { "BOOLEAN" or "BIT" => "BOOL", "SINGLE" => "REAL", "DOUBLE" => "LREAL", var type => type };
        return Normalize(left) == Normalize(right);
    }

    internal static string NormalizeLocation(string value)
    {
        var normalized = new string(value.Where(character =>
            !char.IsWhiteSpace(character) && character != '%').Select(char.ToUpperInvariant).ToArray());
        // Siemens uses E/A in German projects and I/Q in international projects.
        // Only translate address prefixes, not the first letter of symbolic paths.
        if (normalized.Length > 1 && char.IsDigit(normalized[1]))
            normalized = normalized[0] switch { 'E' => "I" + normalized[1..], 'A' => "Q" + normalized[1..], _ => normalized };
        return normalized;
    }
}
