using VIBN_Tools.ContainerToFeeVisual;

namespace VIBN_Tools.Application.VM;

public sealed partial class ContainerToFeeVisualPageVM
{
    private bool _pendingScopeLinkRefresh;
    private bool _scopeLinkRefreshScheduled;

    private ContainerToFeeVisualNodeState SimObjectDisplayState(VisualSimObjectConnectionState connection, string? containerId) =>
        connection.Kind == VisualSimObjectConnectionKind.NotFound
            ? containerId is not null && _planService.CurrentPlan?.IsCreationRequested(containerId) == false
                ? ContainerToFeeVisualNodeState.Missing : ContainerToFeeVisualNodeState.Planned
            : connection.IsVerified ? ContainerToFeeVisualNodeState.Verified : ContainerToFeeVisualNodeState.FoundUnlinked;

    // Scope changes use the existing scene snapshot, then read only its live links.
    // Coalesce checkbox clicks and serialize these reads with every other SDK operation.
    private async void QueueScopeLinkRefresh()
    {
        if (!HasPlan || !CanUseFeeFeatures) return;
        _pendingScopeLinkRefresh = true;
        if (_scopeLinkRefreshScheduled) return;
        _scopeLinkRefreshScheduled = true;
        try
        {
            await Task.Delay(180);
            if (IsBusy || !CanUseFeeFeatures) return;
            _pendingScopeLinkRefresh = false;
            await RunBusyAsync("Verknüpfungen der ausgewählten FEE-Roots und Interfaces werden geprüft …", async token =>
            {
                _planService.AutoAssignMatches();
                await _planService.DiscoverFeeSignalLinksAsync(token);
                await _planService.DiscoverFeeSimObjectLinksAsync(token);
                ApplyDiscoveredContainerObjectStates(_planService.DiscoveredFeeContainerObjects);
                ApplyDiscoveredSimObjectStates();
                ApplyDiscoveredSignalStates(_planService.DiscoveredFeeSignals);
                RefreshFeeObjectProjection(_planService.DiscoveredFeeObjects);
                RefreshFeeSignalProjection(_planService.DiscoveredFeeSignals);
                RefreshCompletedContainerSelection();
                StatusText = "Verknüpfungen der ausgewählten FEE-Roots und Interfaces geprüft.";
            }, usesFeeSdk: true);
        }
        finally
        {
            _scopeLinkRefreshScheduled = false;
            if (_pendingScopeLinkRefresh && !IsBusy && CanUseFeeFeatures) QueueScopeLinkRefresh();
        }
    }
}
