using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>Resolves scene-instance GUIDs from the displayed, root-scoped snapshot. Definition GUIDs are never link endpoints.</summary>
internal sealed class ExistingContainerLogicResolver(IEnumerable<FeeAbstractObject> objects)
{
    private readonly ILookup<string, FeeLogic> _byName = objects.OfType<FeeLogic>()
        .Where(item => item.Guid != Guid.Empty).DistinctBy(item => item.Guid)
        .ToLookup(item => item.Name?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<FeeLogic> Find(VisualNode container, IEnumerable<FeeAbstractObject>? assignedObjects = null,
        IReadOnlyList<VisualFeeObjectLink>? liveLinks = null, FeeLogic? explicitlyAssignedLogic = null)
    {
        if (!ContainerMetadataCatalog.TryGet(container.TypeName, out var descriptor)) return [];
        if (explicitlyAssignedLogic is not null)
            return IsCompatible(explicitlyAssignedLogic, container.TypeName) ? [explicitlyAssignedLogic] : [];
        var matches = _byName[container.Name.Trim()].Where(logic =>
            IsCompatible(logic, container.TypeName)).ToArray();
        if (matches.Length > 1 && assignedObjects is not null)
        {
            var assigned = assignedObjects.ToArray();
            var guids = assigned.Select(item => item.GuidString).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var linked = matches.Where(logic => (liveLinks ?? []).Any(link => !string.IsNullOrWhiteSpace(link.LinkedSlotName) &&
                (string.Equals(link.ObjectGuidString, logic.GuidString, StringComparison.OrdinalIgnoreCase) && guids.Contains(link.LinkedObjectGuidString) ||
                 string.Equals(link.LinkedObjectGuidString, logic.GuidString, StringComparison.OrdinalIgnoreCase) && guids.Contains(link.ObjectGuidString)))).ToArray();
            if (linked.Length == 1) return linked;
            var roots = assigned.Select(item => FeeSimObjectDiscovery.ResolveRoot(item).GuidString)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sameRoot = matches.Where(item => roots.Contains(FeeSimObjectDiscovery.ResolveRoot(item).GuidString)).ToArray();
            if (sameRoot.Length == 1) return sameRoot;
        }
        return matches;
    }

    internal static FeeLogic? ExplicitAssignment(VisualPlan plan, string containerId,
        IReadOnlyDictionary<string, FeeAbstractObject> runtimeObjects)
    {
        var matches = plan.Assignments.Where(item => plan.FindNode(item.TargetId) is { Kind: VisualNodeKind.Logic } node && node.ContainerId == containerId)
            .Select(item => runtimeObjects.GetValueOrDefault(item.FeeObjectId)).OfType<FeeLogic>().DistinctBy(item => item.Guid).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static bool IsUsable(FeeLogic logic) => logic.LogicDefinitionGuid != Guid.Empty && logic.Slots?.Count > 0;

    internal static bool IsCompatible(FeeLogic logic, string containerType)
    {
        if (!IsUsable(logic) || !ContainerMetadataCatalog.TryGet(containerType, out var descriptor) ||
            string.IsNullOrWhiteSpace(descriptor.ExpectedLogicName)) return false;
        if (!string.IsNullOrWhiteSpace(logic.LogicDefinitionName) &&
            !string.Equals(logic.LogicDefinitionName, "UNDEFINED", StringComparison.OrdinalIgnoreCase))
            return ContainerMetadataCatalog.IsSameLogicDefinition(descriptor.ExpectedLogicName, logic.LogicDefinitionName);
        // Some SDKs resolve the component GUID but omit the definition name.
        // Require the concrete runtime slots instead of treating UNDEFINED as missing logic.
        var runtime = descriptor.Factory();
        var slots = descriptor.Slots.Select(slot => ContainerSignalSlotMap.RuntimeSlot(runtime, containerType, slot)).ToArray();
        return slots.Length > 0 && slots.All(slot => logic.Slots.Keys.Contains(slot, StringComparer.OrdinalIgnoreCase));
    }
}
