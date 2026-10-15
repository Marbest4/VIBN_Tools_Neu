using VIBN_Tools.GlobalClasses;
using VIBN_Tools.Settings;

namespace VIBN_Tools.ContainerToFeeVisual;

public sealed record FeeSignalWorkspaceSnapshot(IReadOnlyList<VisualFeeInterface> Interfaces,
    IReadOnlyList<FeeContainerLiveVariable> Variables, long ConnectionRevision);
public sealed record FeeSignalRouteSnapshot(IReadOnlyList<FeeContainerLiveAssignment> Assignments, IReadOnlyList<string> Issues);
public sealed record FeeSignalEndpoint(Guid Guid, string Name, string FeeType);
public sealed record FeeSignalEndpointSnapshot(IReadOnlyDictionary<Guid, FeeSignalEndpoint> Objects, IReadOnlyList<string> Issues);

/// <summary>Signal discovery deliberately does not enumerate scene objects or assign them to a container.</summary>
public sealed class FeeSignalWorkspaceService
{
    public async Task<FeeSignalEndpointSnapshot> ReadEndpointsAsync(IEnumerable<FeeContainerLiveAssignment> routes, long revision,
        CancellationToken token, IProgress<Fee2ContainerProgress>? progress = null)
    {
        var guids = routes.Select(item => item.TargetObjectGuid).Where(guid => guid != Guid.Empty).Distinct().ToArray();
        var result = new Dictionary<Guid, FeeSignalEndpoint>(); var issues = new List<string>();
        foreach (var batch in guids.Chunk(128))
        {
            CheckRevision(revision); token.ThrowIfCancellationRequested();
            progress?.Report(new(90 + result.Count * 8 / Math.Max(1, guids.Length), $"Signalendpunkte / SimObject-Typen: {result.Count}/{guids.Length} gelesen …"));
            try
            {
                var text = batch.Select(guid => guid.ToString("D")).ToArray();
                var types = (await Services.ApiInstance!.Object.GetPropertiesAsync(text, "Type") ?? []).ToArray();
                token.ThrowIfCancellationRequested();
                var names = (await Services.ApiInstance!.Object.GetPropertiesAsync(text, "Name") ?? []).ToArray();
                for (var index = 0; index < batch.Length; index++)
                    result[batch[index]] = new(batch[index], index < names.Length ? Services.ApiInstance.XmlHelper.ConvertToString(names[index]) ?? "" : "",
                        index < types.Length ? (Services.ApiInstance.XmlHelper.ConvertToString(types[index]) ?? "").Split('.').Last() : "");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) { issues.Add($"Signalendpunkt-Typen konnten nicht vollständig gelesen werden: {Fee2ContainerReadPhase.Describe(exception)}"); }
        }
        CheckRevision(revision); token.ThrowIfCancellationRequested();
        progress?.Report(new(98, $"Signalendpunkte / SimObject-Typen: {result.Count}/{guids.Length} gelesen."));
        return new(result, issues);
    }

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
