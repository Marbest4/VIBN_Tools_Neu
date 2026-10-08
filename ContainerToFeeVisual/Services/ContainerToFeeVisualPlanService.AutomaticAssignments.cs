using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFeeVisual;

public sealed partial class ContainerToFeeVisualPlanService
{
    private HashSet<string>? _selectedSimObjectRootGuids;

    public IReadOnlyDictionary<Guid, string> DiscoveredTopLevelBasicFrames => _topLevelBasicFrames;

    public void SetSimObjectRoots(IEnumerable<string> rootGuids) =>
        _selectedSimObjectRootGuids = rootGuids.ToHashSet(StringComparer.OrdinalIgnoreCase);

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
        var scoped = _feeSignals.Where(signal => selected.Contains(signal.InterfaceGuidString)).ToArray();
        var assignedNodes = assignments.Select(item => item.SignalNodeId).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var node in plan.Nodes.Where(node => node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal &&
                     !plan.IsSignalRemoved(node.Id) && !assignedNodes.Contains(node.Id)))
        {
            var matches = scoped.Where(signal => VisualSignalAssignmentMatcher.Matches(node, signal)).ToArray();
            if (matches.Length > 1 && _hasDiscoveredFeeSignalLinks)
                matches = matches.Where(signal => GetSignalConnectionState(node.Id, signal.GuidString).IsVerified).ToArray();
            if (matches.Length != 1) continue;
            var match = matches[0];
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
                errors.Any(issue => issue.NodeId == container.Id || plan.FindNode(issue.NodeId!)?.ContainerId == container.Id)) continue;
            if (!string.IsNullOrWhiteSpace(descriptor.ExpectedLogicName) && _feeContainerObjects.Count(item =>
                    item.Kind == VisualFeeContainerObjectKind.Logic &&
                    string.Equals(item.Name, container.Name, StringComparison.OrdinalIgnoreCase) &&
                    ContainerMetadataCatalog.IsSameLogicDefinition(descriptor.ExpectedLogicName, item.Definition)) != 1) continue;
            if (!string.IsNullOrWhiteSpace(descriptor.ExpectedCabinetElementType) && _feeContainerObjects.Count(item =>
                    item.Kind == VisualFeeContainerObjectKind.CabinetElement &&
                    string.Equals(item.Name, container.Name, StringComparison.OrdinalIgnoreCase) &&
                    NormalizeToken(item.Definition) == NormalizeToken(descriptor.ExpectedCabinetElementType)) != 1) continue;
            var targets = plan.Targets.Where(target => target.ContainerId == container.Id).ToArray();
            if (targets.Any(target => !GetSimObjectConnectionState(target.Id).IsVerified)) continue;
            var targetIds = targets.Select(target => target.Id).ToHashSet(StringComparer.Ordinal);
            var objects = plan.Assignments.Where(assignment => targetIds.Contains(assignment.TargetId))
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
        (string.IsNullOrWhiteSpace(node.SourceLocation) || SameLocation(node.SourceLocation, signal.Location));

    internal static bool SameLocation(string left, string right) => NormalizeLocation(left) == NormalizeLocation(right);

    internal static bool HasCompatibleType(string left, string right)
    {
        static string Normalize(string value) => value.Trim().ToUpperInvariant() switch
        { "BOOLEAN" or "BIT" => "BOOL", "SINGLE" => "REAL", "DOUBLE" => "LREAL", var type => type };
        return Normalize(left) == Normalize(right);
    }

    private static string NormalizeLocation(string value) => new(value.Where(character =>
        !char.IsWhiteSpace(character) && character != '%').Select(char.ToUpperInvariant).ToArray());
}
