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
        CancellationToken cancellationToken,
        IReadOnlyList<FeeAbstractObject>? sceneObjects = null,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>? requiredSlots = null,
        Func<Guid, string, Task<IReadOnlyList<(string SceneObjectGuid, string[] SlotNames)>>>? readSlotLinks = null,
        IEnumerable<Guid>? variableGuids = null)
    {
        readSlotLinks ??= async (guid, slot) => (await Services.ApiInstance.Interface.GetSlotSlotAssignmentAsync(guid, slot) ?? []).ToArray();
        var candidates = objects.Where(item => item.Guid != Guid.Empty)
            .DistinctBy(item => item.Guid)
            .ToArray();
        var variables = (variableGuids ?? []).ToHashSet();
        var links = new FeeSlotSnapshot(sceneObjects ?? candidates, variables).ObjectLinks(candidates).ToList();
        var failures = 0;
        var resolvedSlotCount = 0;
        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slots = requiredSlots is null ? GetRelevantSlotNames(item) :
                (item.Slots?.Keys ?? Enumerable.Empty<string>()).Concat(requiredSlots.GetValueOrDefault(item.Guid) ?? [])
                    .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var slotName in slots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Slots is { } populatedSlots && populatedSlots.TryGetValue(slotName, out var assignedGuid) && variables.Contains(assignedGuid))
                    continue; // Variable assignments are read by signal discovery, not as object links.
                if (links.Any(link => string.Equals(link.ObjectGuidString, item.GuidString, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(link.SlotName, slotName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(link.LinkedSlotName)))
                    continue; // The current XML snapshot already contains both real endpoints of this group.
                // AssignedGuid in the XML can identify a slot group rather
                // than its final scene-object endpoints. Resolve populated
                // slots too; otherwise the list sees a link but the tree
                // cannot find the actual container logic behind that group.
                try
                {
                    var assignments = await readSlotLinks(item.Guid, slotName);
                    if (assignments is null)
                        continue;
                    var resolvedLinks = new List<VisualFeeObjectLink>();
                    foreach (var (linkedGuid, linkedSlots) in assignments)
                    {
                        if (!Guid.TryParse(linkedGuid, out var parsedGuid) || parsedGuid == Guid.Empty)
                            continue;
                        foreach (var linkedSlot in linkedSlots ?? [])
                        {
                            resolvedLinks.Add(new VisualFeeObjectLink(
                                item.Guid.ToString("D"),
                                slotName,
                                parsedGuid.ToString("D"),
                                linkedSlot ?? string.Empty));
                        }
                    }
                    if (resolvedLinks.Count > 0)
                    {
                        links.RemoveAll(link => string.Equals(link.ObjectGuidString, item.GuidString, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(link.SlotName, slotName, StringComparison.OrdinalIgnoreCase));
                        links.AddRange(resolvedLinks);
                        resolvedSlotCount++;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    failures++;
                }
            }
        }

        var distinct = links.Distinct().ToArray();
        if (failures > 0)
            logger.Warning($"{failures} FEE-SimObject-Slot(s) konnten nicht rückgelesen werden.");
        logger.Information(
            $"{distinct.Length} vorhandene SimObject-Slotverknüpfung(en) gelesen; " +
            $"{resolvedSlotCount} Slot(s) einschließlich gruppierter Endpunkte aufgelöst; XML-Zuordnungen bleiben bei nicht rücklesbaren Slots als Rückfall erhalten.");
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
                     "SURFACE" => ["InVelocityX", "Velocity"],
                     "PICKANDPLACE" => ["Feedback", "Pick", "Drop"],
                     _ => Array.Empty<string>(),
                 })
            result.Add(slot);
        return result;
    }
}
