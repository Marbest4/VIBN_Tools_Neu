using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFeeVisual;

internal sealed record VisualFeeSignalLinkDiscoveryResult(
    IReadOnlyList<VisualFeeSignalLink> Links,
    int FailedSignalCount,
    IReadOnlyList<VisualFeeObjectLink>? ObjectLinks = null);

/// <summary>
/// Reads the object-side endpoints of relevant FEE variables. Technical helper
/// routes are followed to the actual container logic slot so the UI does not
/// mistake a variable that merely exists for a completed connection.
/// </summary>
internal sealed class FeeSignalLinkDiscovery(IVisualPlanLogger logger)
{
    public async Task<VisualFeeSignalLinkDiscoveryResult> DiscoverAsync(
        IEnumerable<VisualFeeSignal> signals,
        CancellationToken cancellationToken,
        IReadOnlyList<FeeAbstractObject>? sceneObjects = null,
        IReadOnlyList<VisualFeeObjectLink>? objectLinks = null,
        IEnumerable<Guid>? variableGuids = null)
    {
        var candidates = signals
            .Where(signal => Guid.TryParse(signal.GuidString, out _))
            .DistinctBy(signal => signal.GuidString, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0)
            return new VisualFeeSignalLinkDiscoveryResult([], 0);

        var scene = sceneObjects ?? [];
        var byGuid = scene.DistinctBy(item => item.Guid).ToDictionary(item => item.Guid);
        var snapshot = new FeeSlotSnapshot(scene, variableGuids ?? candidates.Select(signal => Guid.Parse(signal.GuidString)));
        var snapshotLinks = snapshot.ObjectLinks(scene).Concat(objectLinks ?? []).Distinct().ToArray();
        var snapshotPeers = snapshotLinks.SelectMany(link => new[] {
            (Guid: link.ObjectGuidString, Slot: link.SlotName, Peer: new IndirectLink(Guid.Parse(link.LinkedObjectGuidString), link.LinkedSlotName)),
            (Guid: link.LinkedObjectGuidString, Slot: link.LinkedSlotName, Peer: new IndirectLink(Guid.Parse(link.ObjectGuidString), link.SlotName)) })
            .Where(item => !string.IsNullOrWhiteSpace(item.Slot) && !string.IsNullOrWhiteSpace(item.Peer.SlotName))
            .ToLookup(item => (Guid.Parse(item.Guid), item.Slot.Trim().ToUpperInvariant()), item => item.Peer);

        var results = new List<SignalRead>(candidates.Length);
        // Cache each helper port across variables; the SDK calls are serialized.
        var helperLinks = new Dictionary<(Guid, string), IReadOnlyList<IndirectLink>>();
        foreach (var signal in candidates)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var variableGuid = Guid.Parse(signal.GuidString);
                var endpoints = new List<RawLink>();
                var assignments = await Services.ApiInstance.Interface
                    .GetAssignedSceneObjectsAsync(variableGuid);
                if (assignments is null)
                {
                    logger.Warning(
                        $"Die FEE-API hat für Signal {signal.Tag} ({signal.GuidString}) keine " +
                        "Zuordnungsliste geliefert. Das Signal wird als unverknüpft behandelt.");
                    results.Add(new SignalRead([], null));
                    continue;
                }
                foreach (var (objectGuid, slotNames) in assignments)
                {
                    if (objectGuid == Guid.Empty) continue;
                    foreach (var slotName in (slotNames ?? []).Where(name => !string.IsNullOrWhiteSpace(name)))
                        endpoints.Add(new RawLink(signal.GuidString, objectGuid, slotName, false));
                }
                var pending = new Queue<RawLink>(endpoints);
                var visited = new HashSet<(Guid, string)>();
                while (pending.TryDequeue(out var endpoint))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!visited.Add((endpoint.ObjectGuid, endpoint.SlotName.Trim().ToUpperInvariant()))) continue;
                    var helper = byGuid.GetValueOrDefault(endpoint.ObjectGuid);
                    var routingSlots = helper is null
                        ? string.Equals(endpoint.SlotName.Trim(), "Output 01", StringComparison.OrdinalIgnoreCase) ? new[] { "Input 01" } : []
                        : FeeSlotSnapshot.RoutingSlots(helper, endpoint.SlotName);
                    foreach (var routingSlot in routingSlots)
                    {
                        var key = (endpoint.ObjectGuid, routingSlot.Trim().ToUpperInvariant());
                        if (!helperLinks.TryGetValue(key, out var resolvedLinks))
                        {
                            resolvedLinks = snapshotPeers[key].Distinct().ToArray();
                            if (resolvedLinks.Count == 0)
                            {
                                var linkedSlots = await Services.ApiInstance.Interface
                                    .GetSlotSlotAssignmentAsync(endpoint.ObjectGuid, routingSlot);
                                resolvedLinks = (linkedSlots ?? [])
                                    .SelectMany(pair => Guid.TryParse(pair.SceneObjectGuid, out var linkedGuid)
                                        ? (pair.SlotNames ?? []).Select(name => new IndirectLink(linkedGuid, name ?? string.Empty))
                                        : [])
                                    .Distinct()
                                    .ToArray();
                            }
                            helperLinks[key] = resolvedLinks;
                        }
                        foreach (var linked in resolvedLinks)
                        {
                            var indirect = new RawLink(signal.GuidString, linked.ObjectGuid, linked.SlotName, true);
                            endpoints.Add(indirect);
                            pending.Enqueue(indirect);
                        }
                    }
                }
                results.Add(new SignalRead(endpoints, null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                results.Add(new SignalRead([], exception));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var rawLinks = results.SelectMany(result => result.Links).Distinct().ToArray();
        var knownTypes = byGuid.ToDictionary(item => item.Key, item => item.Value.FeeType ?? "");
        var types = await ReadObjectTypesAsync(
            rawLinks.Select(link => link.ObjectGuid).Distinct().Where(guid => !knownTypes.ContainsKey(guid)).ToArray(),
            cancellationToken);
        var links = rawLinks.Select(link => new VisualFeeSignalLink(
                link.SignalGuid,
                link.ObjectGuid.ToString("D"),
                knownTypes.GetValueOrDefault(link.ObjectGuid, types.GetValueOrDefault(link.ObjectGuid, string.Empty)),
                link.SlotName,
                link.IsIndirect))
            .Distinct()
            .ToArray();
        var failed = results.Count(result => result.Error is not null);
        if (failed > 0)
            logger.Warning($"Für {failed} relevante FEE-Signale konnten die Objektverknüpfungen nicht gelesen werden.");
        logger.Information($"{links.Length} vorhandene Signal-Slot-Verknüpfungen für {candidates.Length} relevante FEE-Signale gelesen.");
        var routedObjectLinks = helperLinks.SelectMany(item => item.Value.Select(peer => new VisualFeeObjectLink(
            item.Key.Item1.ToString("D"), item.Key.Item2, peer.ObjectGuid.ToString("D"), peer.SlotName))).Distinct().ToArray();
        var resolved = FeeSlotSnapshot.ExpandSignalRoutes(links.Concat(snapshot.SignalLinks(candidates)), scene,
            snapshotLinks.Concat(routedObjectLinks));
        return new VisualFeeSignalLinkDiscoveryResult(resolved, failed, routedObjectLinks);
    }

    private async Task<IReadOnlyDictionary<Guid, string>> ReadObjectTypesAsync(
        IReadOnlyList<Guid> objectGuids,
        CancellationToken cancellationToken)
    {
        if (objectGuids.Count == 0)
            return new Dictionary<Guid, string>();
        try
        {
            var guidStrings = objectGuids.Select(guid => guid.ToString("D")).ToArray();
            var propertyValues = await Services.ApiInstance.Object.GetPropertiesAsync(guidStrings, "Type");
            var values = propertyValues?.ToArray() ?? [];
            cancellationToken.ThrowIfCancellationRequested();
            return objectGuids.Select((guid, index) => new
                {
                    Guid = guid,
                    Type = index < values.Length
                        ? Services.ApiInstance.XmlHelper.ConvertToString(values[index])
                        : string.Empty,
                })
                .ToDictionary(item => item.Guid, item => item.Type);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.Warning(
                $"Die Typen von {objectGuids.Count} Signalendpunkten konnten nicht gelesen werden; " +
                $"direkte Objekt-GUID-Verknüpfungen bleiben verwendbar: {exception.Message}");
            return objectGuids.ToDictionary(guid => guid, _ => string.Empty);
        }
    }

    private sealed record RawLink(
        string SignalGuid,
        Guid ObjectGuid,
        string SlotName,
        bool IsIndirect);

    private sealed record IndirectLink(Guid ObjectGuid, string SlotName);

    private sealed record SignalRead(
        IReadOnlyList<RawLink> Links,
        Exception? Error);
}
