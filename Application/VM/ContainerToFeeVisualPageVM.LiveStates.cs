using VIBN_Tools.ContainerToFeeVisual;

namespace VIBN_Tools.Application.VM;

public sealed partial class ContainerToFeeVisualPageVM
{
    private string? ResolveLogicDropNode(ContainerToFeeVisualTreeNodeVM node) => node.Kind == VisualNodeKind.Logic ? node.Id :
        node.Kind == VisualNodeKind.Container ? _planService.CurrentPlan?.Nodes.FirstOrDefault(item =>
            item.ContainerId == node.Id && item.Kind == VisualNodeKind.Logic)?.Id : null;
    private ContainerToFeeVisualNodeState SimObjectDisplayState(VisualSimObjectConnectionState connection, string? containerId) =>
        connection.Kind == VisualSimObjectConnectionKind.NotFound
            ? containerId is not null && _planService.CurrentPlan?.IsCreationRequested(containerId) == false
                ? ContainerToFeeVisualNodeState.Missing : ContainerToFeeVisualNodeState.Planned
            : connection.IsVerified ? ContainerToFeeVisualNodeState.Verified : ContainerToFeeVisualNodeState.FoundUnlinked;

    // Root/interface checkboxes only project the last explicit refresh. No SDK
    // calls are allowed here: an unchecked root must not invalidate the snapshot.
    private void RefreshCachedLinkStates()
    {
        ApplyDiscoveredContainerObjectStates(_planService.DiscoveredFeeContainerObjects);
        ApplyDiscoveredSimObjectStates();
        ApplyDiscoveredSignalStates(_planService.DiscoveredFeeSignals);
        RefreshFeeObjectProjection(_planService.DiscoveredFeeObjects);
        RefreshFeeSignalProjection(_planService.DiscoveredFeeSignals);
        RefreshCompletedContainerSelection();
    }
}
