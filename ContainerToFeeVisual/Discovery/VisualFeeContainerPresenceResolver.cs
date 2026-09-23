namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>
/// Matches plan logic/cabinet nodes against a current FEE inventory. Matching
/// follows the same component-name and definition contract as generation, so
/// the UI does not claim that an object is missing when it will be reused.
/// </summary>
public static class VisualFeeContainerPresenceResolver
{
    public static IReadOnlyDictionary<string, VisualFeeNodePresence> Resolve(
        VisualPlan plan,
        IEnumerable<VisualFeeContainerObject> inventory)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(inventory);
        var objects = inventory.ToArray();
        var result = new Dictionary<string, VisualFeeNodePresence>(StringComparer.Ordinal);

        foreach (var node in plan.Nodes.Where(node => node.Kind == VisualNodeKind.Logic))
        {
            var container = FindContainer(plan, node);
            if (container is null)
                continue;
            var matches = objects.Where(item =>
                    item.Kind == VisualFeeContainerObjectKind.Logic &&
                    string.Equals(item.Name, container.Name, StringComparison.OrdinalIgnoreCase) &&
                    ContainerMetadataCatalog.IsSameLogicDefinition(node.Name, item.Definition))
                .ToArray();
            result[node.Id] = CreatePresence(
                node.Id,
                matches,
                $"FEE-Logik '{container.Name}' ({node.Name})");
        }

        foreach (var node in plan.Nodes.Where(node => node.Kind == VisualNodeKind.TechnicalHelper))
        {
            var container = FindContainer(plan, node);
            if (container is null || !ContainerMetadataCatalog.TryGet(container.TypeName, out var descriptor) ||
                string.IsNullOrWhiteSpace(descriptor.ExpectedCabinetElementType))
            {
                continue;
            }

            var elementMatches = objects.Where(item =>
                    item.Kind == VisualFeeContainerObjectKind.CabinetElement &&
                    string.Equals(item.Name, container.Name, StringComparison.OrdinalIgnoreCase) &&
                    SameDefinition(item.Definition, descriptor.ExpectedCabinetElementType))
                .ToArray();

            if (string.Equals(node.Name, "CabinetElement", StringComparison.OrdinalIgnoreCase))
            {
                result[node.Id] = CreatePresence(
                    node.Id,
                    elementMatches,
                    $"CabinetElement '{container.Name}' ({descriptor.ExpectedCabinetElementType})");
                continue;
            }

            if (!string.Equals(node.Name, descriptor.CabinetName, StringComparison.OrdinalIgnoreCase))
                continue;
            var cabinetMatches = objects.Where(item =>
                    item.Kind == VisualFeeContainerObjectKind.Cabinet &&
                    string.Equals(item.Name, descriptor.CabinetName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            // A matching child proves that the required cabinet hierarchy is
            // present even if a particular SDK omits Cabinet from its type query.
            var effectiveMatches = cabinetMatches.Length > 0 ? cabinetMatches : elementMatches;
            result[node.Id] = CreatePresence(
                node.Id,
                effectiveMatches,
                $"Cabinet '{descriptor.CabinetName}'");
        }

        return result;
    }

    private static VisualNode? FindContainer(VisualPlan plan, VisualNode node) =>
        node.ContainerId is null ? null : plan.FindNode(node.ContainerId);

    private static VisualFeeNodePresence CreatePresence(
        string nodeId,
        IReadOnlyCollection<VisualFeeContainerObject> matches,
        string description) => matches.Count switch
    {
        0 => new VisualFeeNodePresence(
            nodeId,
            VisualFeeNodePresenceKind.Planned,
            $"{description} ist noch nicht vorhanden und wird erzeugt."),
        1 => new VisualFeeNodePresence(
            nodeId,
            VisualFeeNodePresenceKind.Found,
            $"Vorhanden: {description} · GUID {matches.First().GuidString}"),
        _ => new VisualFeeNodePresence(
            nodeId,
            VisualFeeNodePresenceKind.Ambiguous,
            $"Mehrdeutig: {description} wurde {matches.Count}-mal gefunden."),
    };

    private static bool SameDefinition(string? actual, string? expected)
    {
        static string Normalize(string? value)
        {
            var normalizedPath = (value ?? string.Empty).Trim().Replace('/', '\\');
            var fileName = normalizedPath[(normalizedPath.LastIndexOf('\\') + 1)..];
            if (fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                fileName = fileName[..^4];
            return new string(fileName
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
        }
        return Normalize(actual) == Normalize(expected);
    }
}
