using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>Resolves scene-instance GUIDs from the displayed, root-scoped snapshot. Definition GUIDs are never link endpoints.</summary>
internal sealed class ExistingContainerLogicResolver(IEnumerable<FeeAbstractObject> objects)
{
    private readonly ILookup<string, FeeLogic> _byName = objects.OfType<FeeLogic>()
        .Where(item => item.Guid != Guid.Empty).DistinctBy(item => item.Guid)
        .ToLookup(item => item.Name?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<FeeLogic> Find(VisualNode container, IEnumerable<FeeAbstractObject>? assignedObjects = null,
        IReadOnlyList<VisualFeeObjectLink>? liveLinks = null)
    {
        if (!ContainerMetadataCatalog.TryGet(container.TypeName, out var descriptor)) return [];
        var matches = _byName[container.Name.Trim()].Where(logic =>
            ContainerMetadataCatalog.IsSameLogicDefinition(descriptor.ExpectedLogicName, logic.LogicDefinitionName)).ToArray();
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
}
