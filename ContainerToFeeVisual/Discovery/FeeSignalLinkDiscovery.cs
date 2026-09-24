using VIBN_Tools.GlobalClasses;

namespace VIBN_Tools.ContainerToFeeVisual;

internal sealed record VisualFeeSignalLinkDiscoveryResult(
    IReadOnlyList<VisualFeeSignalLink> Links,
    int FailedSignalCount);

/// <summary>
/// Reads the object-side endpoints of relevant FEE variables. MoveBit fan-in
/// routes are followed to the actual container logic slot so the UI does not
/// mistake a variable that merely exists for a completed connection.
/// </summary>
internal sealed class FeeSignalLinkDiscovery(IVisualPlanLogger logger)
{
    public async Task<VisualFeeSignalLinkDiscoveryResult> DiscoverAsync(
        IEnumerable<VisualFeeSignal> signals,
        CancellationToken cancellationToken)
    {
        var candidates = signals
            .Where(signal => Guid.TryParse(signal.GuidString, out _))
            .DistinctBy(signal => signal.GuidString, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0)
            return new VisualFeeSignalLinkDiscoveryResult([], 0);

        using var throttle = new SemaphoreSlim(8, 8);
        var reads = candidates.Select(async signal =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var variableGuid = Guid.Parse(signal.GuidString);
                var endpoints = new List<RawLink>();
                var assignments = await Services.ApiInstance.Interface
                    .GetAssignedSceneObjectsAsync(variableGuid);
                foreach (var (objectGuid, slotNames) in assignments)
                {
                    foreach (var slotName in slotNames ?? [])
                    {
                        endpoints.Add(new RawLink(signal.GuidString, objectGuid, slotName, false));
                        if (!string.Equals(slotName, "Output 01", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var linkedSlots = await Services.ApiInstance.Interface
                            .GetSlotSlotAssignmentAsync(objectGuid, "Input 01");
                        foreach (var (linkedGuidText, names) in linkedSlots)
                        {
                            if (!Guid.TryParse(linkedGuidText, out var linkedGuid))
                                continue;
                            foreach (var name in names ?? [])
                                endpoints.Add(new RawLink(signal.GuidString, linkedGuid, name, true));
                        }
                    }
                }
                return new SignalRead(endpoints, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return new SignalRead([], exception);
            }
            finally
            {
                throttle.Release();
            }
        });

        var results = await Task.WhenAll(reads);
        cancellationToken.ThrowIfCancellationRequested();
        var rawLinks = results.SelectMany(result => result.Links).Distinct().ToArray();
        var types = await ReadObjectTypesAsync(
            rawLinks.Select(link => link.ObjectGuid).Distinct().ToArray(),
            cancellationToken);
        var links = rawLinks.Select(link => new VisualFeeSignalLink(
                link.SignalGuid,
                link.ObjectGuid.ToString("D"),
                types.GetValueOrDefault(link.ObjectGuid, string.Empty),
                link.SlotName,
                link.IsIndirect))
            .Distinct()
            .ToArray();
        var failed = results.Count(result => result.Error is not null);
        if (failed > 0)
            logger.Warning($"Für {failed} relevante FEE-Signale konnten die Objektverknüpfungen nicht gelesen werden.");
        logger.Information($"{links.Length} vorhandene Signal-Slot-Verknüpfungen für {candidates.Length} relevante FEE-Signale gelesen.");
        return new VisualFeeSignalLinkDiscoveryResult(links, failed);
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
            var values = (await Services.ApiInstance.Object.GetPropertiesAsync(guidStrings, "Type")).ToArray();
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

    private sealed record SignalRead(
        IReadOnlyList<RawLink> Links,
        Exception? Error);
}
