using VIBN_Tools.ContainerToFeeVisual;

namespace VIBN_Tools.Application.VM;

public sealed partial class ContainerToFeeVisualPageVM
{
    private static string FeeIdentity(string? value)
    {
        var identity = value?.Trim() ?? "";
        if (identity.StartsWith("fee:", StringComparison.OrdinalIgnoreCase)) identity = identity[4..];
        return Guid.TryParse(identity, out var guid) ? guid.ToString("D") : identity;
    }

    private IReadOnlyList<ContainerToFeeVisualTreeNodeVM> ResolveIssueTreeNodes(VisualIssue issue)
    {
        var tree = TreeRoots.SelectMany(root => root.SelfAndDescendants()).ToArray();
        var result = new List<ContainerToFeeVisualTreeNodeVM>();
        foreach (var id in (issue.RelatedIds ?? []).Prepend(issue.NodeId ?? "").Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            var exact = tree.FirstOrDefault(node => node.Id == id);
            if (exact is not null) { result.Add(exact); continue; }
            var identity = FeeIdentity(id);
            result.AddRange(tree.Where(node => node.FeeObjectId is not null &&
                string.Equals(FeeIdentity(node.FeeObjectId), identity, StringComparison.OrdinalIgnoreCase)));
            var signal = AvailableFeeSignals.FirstOrDefault(item => string.Equals(item.GuidString, identity, StringComparison.OrdinalIgnoreCase));
            if (signal is not null)
                result.AddRange(tree.Where(node => signal.AssignedNodeIds.Contains(node.Id) ||
                    node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal && VisualSignalAssignmentMatcher.Matches(node.Model, signal.Model)));
            var feeObject = AvailableFeeObjects.FirstOrDefault(item => string.Equals(item.GuidString, identity, StringComparison.OrdinalIgnoreCase));
            if (feeObject is not null && _planService.CurrentPlan is { } plan)
            {
                var targetIds = plan.Assignments.Where(assignment => assignment.FeeObjectId == feeObject.Id).Select(assignment => assignment.TargetId)
                    .Concat(plan.Targets.Where(target => target.CanAssign(feeObject.Model) &&
                        string.Equals(plan.FindNode(target.ContainerId)?.Name.Trim(), feeObject.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                        .Select(target => target.Id)).ToHashSet(StringComparer.Ordinal);
                result.AddRange(tree.Where(node => targetIds.Contains(node.Id)));
            }
        }
        return result.DistinctBy(node => node.Id).OrderBy(node => node.Kind is VisualNodeKind.SimObject or VisualNodeKind.Signal or VisualNodeKind.Logic ? 0 : 1).ToArray();
    }

    private void SynchronizeSelectionsFromIssue(VisualIssue issue)
    {
        var nodes = ResolveIssueTreeNodes(issue);
        var identities = (issue.RelatedIds ?? []).Prepend(issue.NodeId ?? "").Select(FeeIdentity).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var objects = AvailableFeeObjects.Where(item => identities.Contains(item.GuidString) && _planService.IsFeeObjectInSelectedRoots(item.Model)).ToArray();
        var signals = AvailableFeeSignals.Where(item => identities.Contains(item.GuidString) && GetSelectedInterfaceGuids().Contains(item.Model.InterfaceGuidString)).ToArray();
        if (nodes.Count > 0) SelectRelatedTreeNode(nodes[0], objects.FirstOrDefault(), signals.FirstOrDefault(), preferredIssue: issue);
        if (nodes.Count <= 1 && objects.Length + signals.Length == 0) return;
        _isSynchronizingSelections = true;
        try
        {
            if (nodes.Count == 0) SelectedTreeNode = null;
            ClearSynchronizationMatches();
            foreach (var node in nodes)
            {
                var scope = node.Kind is VisualNodeKind.Container or VisualNodeKind.Group or VisualNodeKind.SimObjectTarget
                    ? node.SelfAndDescendants() : [node];
                foreach (var item in scope)
                {
                    if (objects.Length + signals.Length > 0 && item.Id != node.Id &&
                        !identities.Contains(FeeIdentity(item.FeeObjectId)) && !identities.Contains(item.Id)) continue;
                    item.IsSynchronizationMatch = true;
                    if (item.FeeObjectId is { } objectId)
                        foreach (var obj in AvailableFeeObjects.Where(obj => obj.Id == objectId && _planService.IsFeeObjectInSelectedRoots(obj.Model))) obj.IsSynchronizationMatch = true;
                    foreach (var signal in AvailableFeeSignals.Where(signal => signal.AssignedNodeIds.Contains(item.Id))) signal.IsSynchronizationMatch = true;
                }
            }
            foreach (var obj in objects) obj.IsSynchronizationMatch = true;
            foreach (var signal in signals) signal.IsSynchronizationMatch = true;
            foreach (var target in Targets) target.IsSynchronizationMatch = nodes.Any(node => node.Id == target.Id || node.ParentId == target.Id);
            foreach (var slot in SignalSlots) slot.IsSynchronizationMatch = slot.Assignments.Any(assignment => nodes.Any(node => node.Id == assignment.NodeId));
            EnsureSynchronizedAnchorsAreVisible(objects.FirstOrDefault(), signals.FirstOrDefault());
            SetSelectionAnchor(ref _selectedFeeObject, AvailableFeeObjects.FirstOrDefault(item => item.IsSynchronizationMatch), nameof(SelectedFeeObject), true);
            SetSelectionAnchor(ref _selectedFeeSignal, AvailableFeeSignals.FirstOrDefault(item => item.IsSynchronizationMatch), nameof(SelectedFeeSignal), true);
            SetSelectionAnchor(ref _selectedTarget, Targets.FirstOrDefault(item => item.IsSynchronizationMatch), nameof(SelectedTarget), true);
            SetSelectionAnchor(ref _selectedSignalSlot, SignalSlots.FirstOrDefault(item => item.IsSynchronizationMatch), nameof(SelectedSignalSlot), true);
        }
        finally { _isSynchronizingSelections = false; }
    }
}
