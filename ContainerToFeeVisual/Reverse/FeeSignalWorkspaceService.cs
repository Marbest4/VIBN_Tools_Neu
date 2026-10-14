using VIBN_Tools.GlobalClasses;
using VIBN_Tools.Settings;

namespace VIBN_Tools.ContainerToFeeVisual;

public sealed record FeeSignalWorkspaceSnapshot(IReadOnlyList<VisualFeeInterface> Interfaces,
    IReadOnlyList<FeeContainerLiveVariable> Variables, long ConnectionRevision);
public sealed record FeeSignalRouteSnapshot(IReadOnlyList<FeeContainerLiveAssignment> Assignments, IReadOnlyList<string> Issues);

/// <summary>Signal discovery deliberately does not enumerate scene objects or assign them to a container.</summary>
public sealed class FeeSignalWorkspaceService
{
    public async Task<FeeSignalWorkspaceSnapshot> ReadInterfacesAsync(CancellationToken token)
    {
        var revision = RequireConnection();
        var result = await new FeeInterfaceDiscovery(new VisualPlanLogger()).DiscoverAsync(token);
        CheckRevision(revision);
        var variables = result.RuntimeInterfaces.Values.SelectMany(parent => (parent.Signals ?? []).Select(signal =>
            new FeeContainerLiveVariable(signal.Guid, signal.Tag ?? "", signal.Address ?? "", signal.Path ?? "",
                signal.IOType.ToString(), signal.Comment ?? "", signal.Usage.ToString(), parent.GuidString, parent.Name ?? ""))).ToArray();
        return new(result.Interfaces, variables, revision);
    }

    public async Task<FeeSignalRouteSnapshot> ReadRoutesAsync(IEnumerable<FeeContainerLiveVariable> variables, long revision,
        CancellationToken token, IProgress<Fee2ContainerProgress>? progress = null)
    {
        CheckRevision(revision);
        var items = variables.DistinctBy(item => item.VariableGuid).ToArray();
        var routes = new List<FeeContainerLiveAssignment>();
        var issues = new List<string>();
        // The stateful vendor client can cancel overlapping interface requests.
        // Read direct endpoints serially; scene enumeration is deliberately absent.
        for (var index = 0; index < items.Length; index++)
        {
            var variable = items[index];
            token.ThrowIfCancellationRequested();
            CheckRevision(revision);
            try
            {
                token.ThrowIfCancellationRequested();
                var assignments = await Services.ApiInstance!.Interface.GetAssignedSceneObjectsAsync(variable.VariableGuid) ?? [];
                token.ThrowIfCancellationRequested();
                foreach (var (objectGuid, slots) in assignments)
                    if (objectGuid != Guid.Empty)
                        foreach (var slot in (slots ?? []).Where(slot => !string.IsNullOrWhiteSpace(slot)))
                            routes.Add(new(variable.VariableGuid, objectGuid, slot));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                issues.Add($"Signal '{variable.Signal}' / {variable.InterfaceName}: {Fee2ContainerReadPhase.Describe(exception)}");
            }
            finally
            {
                progress?.Report(new Fee2ContainerProgress((index + 1) * 90 / Math.Max(1, items.Length), $"Aktuelle Signalverbindungen {index + 1}/{items.Length} …"));
            }
        }
        CheckRevision(revision);
        return new(routes.Distinct().ToArray(), issues);
    }

    private static long RequireConnection() => Services.Connection?.CanUseFeeFeatures == true
        ? Services.Connection.ConnectionRevision : throw new InvalidOperationException(FeeConnectionService.MissingConnectionMessage);
    private static void CheckRevision(long revision)
    {
        if (RequireConnection() != revision) throw new InvalidOperationException("Die FEE-Verbindung hat sich geändert. Interfaces erneut einlesen.");
    }
}
