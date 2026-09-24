using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFeeVisual;

internal sealed record VisualFeeObjectLinkDiscoveryResult(
    IReadOnlyList<VisualFeeObjectLink> Links,
    int FailedSlotCount);

/// <summary>
/// Reads the current object-to-object links from FEE. XML slot assignments are
/// retained as a fallback, while the interface API resolves grouped multi-endpoint
/// links such as MotionJoint target and velocity connections.
/// </summary>
internal sealed class FeeSimObjectLinkDiscovery(IVisualPlanLogger logger)
{
    public async Task<VisualFeeObjectLinkDiscoveryResult> DiscoverAsync(
        IEnumerable<FeeAbstractObject> objects,
        CancellationToken cancellationToken)
    {
        var candidates = objects.Where(item => item.Guid != Guid.Empty)
            .DistinctBy(item => item.Guid)
            .ToArray();
        var links = new List<VisualFeeObjectLink>();
        var failures = 0;
        using var throttle = new SemaphoreSlim(8, 8);
        var reads = candidates.Select(async item =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                var localLinks = new List<VisualFeeObjectLink>();
                foreach (var direct in item.Slots ?? new Dictionary<string, Guid>())
                {
                    if (direct.Value != Guid.Empty)
                        localLinks.Add(new VisualFeeObjectLink(
                            item.Guid.ToString("D"), direct.Key, direct.Value.ToString("D"), string.Empty));
                }

                var slotNames = GetRelevantSlotNames(item).ToArray();
                foreach (var slotName in slotNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var assignments = await Services.ApiInstance.Interface
                            .GetSlotSlotAssignmentAsync(item.Guid, slotName);
                        if (assignments is null)
                            continue;
                        foreach (var (linkedGuid, linkedSlots) in assignments)
                        {
                            if (!Guid.TryParse(linkedGuid, out var parsedGuid))
                                continue;
                            foreach (var linkedSlot in linkedSlots ?? [])
                            {
                                localLinks.Add(new VisualFeeObjectLink(
                                    item.Guid.ToString("D"),
                                    slotName,
                                    parsedGuid.ToString("D"),
                                    linkedSlot ?? string.Empty));
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        Interlocked.Increment(ref failures);
                    }
                }
                return localLinks;
            }
            finally
            {
                throttle.Release();
            }
        });

        foreach (var result in await Task.WhenAll(reads))
            links.AddRange(result);
        var distinct = links.Distinct().ToArray();
        if (failures > 0)
            logger.Warning($"{failures} FEE-SimObject-Slot(s) konnten nicht rückgelesen werden.");
        logger.Information($"{distinct.Length} vorhandene SimObject-Slotverknüpfung(en) gelesen.");
        return new VisualFeeObjectLinkDiscoveryResult(distinct, failures);
    }

    private static IEnumerable<string> GetRelevantSlotNames(FeeAbstractObject item)
    {
        IEnumerable<string> existingSlotNames = item.Slots?.Keys ?? Enumerable.Empty<string>();
        var result = new HashSet<string>(existingSlotNames, StringComparer.OrdinalIgnoreCase);
        var type = new string((item.FeeType ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
        foreach (var slot in type.ToUpperInvariant() switch
                 {
                     "MOTIONJOINT" => ["InValue", "OutValue", "InTarget", "InVelocity"],
                     "FLOOR" => ["Collision"],
                     "SENSOR" or "SAFETYSENSOR" => ["Channel1", "Channel2"],
                     "SURFACE" => ["Velocity"],
                     "PICKANDPLACE" => ["Feedback", "Pick", "Drop"],
                     _ => Array.Empty<string>(),
                 })
            result.Add(slot);
        return result;
    }
}
