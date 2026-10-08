using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>Resolves scene-instance GUIDs from the displayed, root-scoped snapshot. Definition GUIDs are never link endpoints.</summary>
internal sealed class ExistingContainerLogicResolver(IEnumerable<FeeAbstractObject> objects)
{
    private readonly ILookup<string, FeeLogic> _byName = objects.OfType<FeeLogic>()
        .Where(item => item.Guid != Guid.Empty).DistinctBy(item => item.Guid)
        .ToLookup(item => item.Name?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<FeeLogic> Find(VisualNode container, IEnumerable<FeeAbstractObject>? assignedObjects = null)
    {
        if (!ContainerMetadataCatalog.TryGet(container.TypeName, out var descriptor)) return [];
        var matches = _byName[container.Name.Trim()].Where(logic =>
            ContainerMetadataCatalog.IsSameLogicDefinition(descriptor.ExpectedLogicName, logic.LogicDefinitionName)).ToArray();
        if (matches.Length > 1 && assignedObjects is not null)
        {
            var roots = assignedObjects.Select(item => FeeSimObjectDiscovery.ResolveRoot(item).GuidString)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sameRoot = matches.Where(item => roots.Contains(FeeSimObjectDiscovery.ResolveRoot(item).GuidString)).ToArray();
            if (sameRoot.Length == 1) return sameRoot;
        }
        return matches;
    }
}
