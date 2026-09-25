using System.Xml.Linq;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;
using static VIBN_Tools.GlobalClasses.Interfaces;

namespace VIBN_Tools.ContainerToFeeVisual;

internal sealed record VisualFeeDiscoveryResult(
    IReadOnlyList<VisualFeeObject> Objects,
    IReadOnlyDictionary<string, FeeAbstractObject> RuntimeObjects,
    IReadOnlyList<VisualFeeContainerObject> ContainerObjects);

/// <summary>Reads selectable FEE objects and keeps SDK instances out of the view model.</summary>
internal sealed class FeeSimObjectDiscovery(IVisualPlanLogger logger)
{
    public async Task<VisualFeeDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Reuse the canonical batched snapshot used by ModelValidation. The
        // previous parallel type queries raced the stateful vendor client and
        // could leave large projects waiting indefinitely.
        await Services.FeeObjects.UpdateFeeDataAsync();
        cancellationToken.ThrowIfCancellationRequested();
        var allObjects = Services.FeeObjects.AllFeeObjects ?? [];
        var runtimeObjects = allObjects
            .Where(item => item is IAssignableSimObject)
            .ToArray();

        var uniqueRuntimeObjects = runtimeObjects
            .Where(item => !string.IsNullOrWhiteSpace(item.GuidString))
            .GroupBy(item => item.GuidString, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.FeeType, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        // FeeObjectService already populated the slot assignments from its XML
        // snapshot, so a second project-wide XML read is unnecessary.

        var byId = new Dictionary<string, FeeAbstractObject>(StringComparer.Ordinal);
        var objects = new List<VisualFeeObject>(uniqueRuntimeObjects.Length);
        foreach (var runtimeObject in uniqueRuntimeObjects)
        {
            var id = CreateFeeObjectId(runtimeObject.GuidString);
            byId[id] = runtimeObject;
            objects.Add(new VisualFeeObject(
                id,
                runtimeObject.GuidString,
                runtimeObject.Name ?? string.Empty,
                runtimeObject.GetType().FullName ?? runtimeObject.GetType().Name,
                runtimeObject.FeeType ?? string.Empty,
                GetAssignableTypeNames(runtimeObject.GetType())));
        }

        var containerObjects = allObjects.OfType<FeeLogic>()
            .Select(item => new VisualFeeContainerObject(
                item.Guid.ToString("D"),
                item.Name ?? string.Empty,
                VisualFeeContainerObjectKind.Logic,
                item.LogicDefinitionName ?? string.Empty))
            .Concat(allObjects.OfType<FeeCabinetElement>().Select(item => new VisualFeeContainerObject(
                item.Guid.ToString("D"),
                item.Name ?? string.Empty,
                VisualFeeContainerObjectKind.CabinetElement,
                item.ElementType ?? string.Empty)))
            .Concat(allObjects.OfType<FeeCabinet>().Select(item => new VisualFeeContainerObject(
                item.Guid.ToString("D"),
                item.Name ?? string.Empty,
                VisualFeeContainerObjectKind.Cabinet,
                string.Empty)))
            .ToArray();

        logger.Information(
            $"{objects.Count} zuweisbare FEE-SimObjects und {containerObjects.Length} vorhandene Logik-/Cabinet-Objekte gelesen.");
        return new VisualFeeDiscoveryResult(objects, byId, containerObjects);
    }

    internal static string CreateFeeObjectId(string guidString) =>
        $"fee:{guidString.Trim().ToLowerInvariant()}";

    internal static Dictionary<string, Guid> ParseSlotAssignments(XElement xml)
    {
        var slots = xml.Element("Slots") ?? xml.Element("IOSlots") ??
                    xml.Descendants("Slots").FirstOrDefault() ??
                    xml.Descendants("IOSlots").FirstOrDefault();
        if (slots is null)
            return new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        return slots.Descendants("Assignment")
            .Select(item => new
            {
                Name = item.Element("SlotName")?.Value?.Trim() ?? string.Empty,
                Guid = Guid.TryParse(item.Element("AssignedGuid")?.Value, out var guid) ? guid : Guid.Empty,
            })
            .Where(item => item.Name.Length > 0 && item.Guid != Guid.Empty)
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Guid, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyCollection<string> GetAssignableTypeNames(Type runtimeType)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (Type? type = runtimeType; type is not null; type = type.BaseType)
        {
            names.Add(type.Name);
            if (type.FullName is not null)
                names.Add(type.FullName);
        }
        foreach (var interfaceType in runtimeType.GetInterfaces())
        {
            names.Add(interfaceType.Name);
            if (interfaceType.FullName is not null)
                names.Add(interfaceType.FullName);
        }
        return names;
    }

}
