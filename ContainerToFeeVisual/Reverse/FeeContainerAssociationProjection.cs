using System.Xml.Linq;
using VIBN_Tools.ContainerGeneration.Models;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFeeVisual;

internal static class FeeContainerAssociationProjection
{
    public static IReadOnlyList<FeeContainerObjectAssociation> Apply(
        FeeContainerProvenanceSnapshot snapshot, FeeContainerReconstructionResult reconstructed,
        IReadOnlyList<FeeAbstractObject> liveObjects)
    {
        var live = liveObjects.ToDictionary(item => item.Guid);
        var containers = snapshot.ContainerDocument.Descendants("Container").ToArray();
        var associations = new List<FeeContainerObjectAssociation>();
        var stableIds = new Dictionary<XElement, string>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var container in containers)
        {
            var id = container.Attribute("id")?.Value ?? "";
            var name = container.Element("Component")?.Value ?? "";
            var type = container.Element("Type")?.Value ?? "";
            var identity = $"{id}\u001f{name}\u001f{type}";
            occurrences.TryGetValue(identity, out var occurrence); occurrences[identity] = ++occurrence;
            stableIds[container] = ContainerXmlVisualPlanParser.CreateContainerId(id, name, type, occurrence);
            // Explicit manual associations survive even when names differ.
            foreach (var item in ContainerFileXml.Objects(container))
                if (Guid.TryParse(item.Element("Guid")?.Value, out var guid) && live.TryGetValue(guid, out var actual))
                    associations.Add(new FeeContainerObjectAssociation(guid, actual.Name, actual.FeeType,
                        Guid.Empty, id, "Explizite Zuordnung aus Container-Provenienz", item.Element("Role")?.Value ?? "SimObject"));
        }
        foreach (var group in reconstructed.ObjectAssociations.GroupBy(item => item.ContainerId))
        {
            var source = reconstructed.Snapshot.ContainerDocument.Descendants("Container").FirstOrDefault(item => item.Attribute("id")?.Value == group.Key);
            if (source is null) continue;
            var provenanceId = group.First().ProvenanceContainerId;
            var matches = containers.Where(item =>
                (!string.IsNullOrWhiteSpace(provenanceId) && stableIds[item] == provenanceId) ||
                (string.Equals(item.Element("Component")?.Value, source.Element("Component")?.Value, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(item.Element("Type")?.Value, source.Element("Type")?.Value, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (matches.Length != 1) continue;
            foreach (var item in group)
                if (!associations.Any(association => association.ObjectGuid == item.ObjectGuid))
                    associations.Add(item with { ContainerId = matches[0].Attribute("id")?.Value ?? "" });
        }
        foreach (var container in containers)
        {
            container.Element("SimObjects")?.Remove();
            container.Add(new XElement("SimObjects", associations.Where(item => item.ContainerId == container.Attribute("id")?.Value)
                .Select(item => ContainerFileXml.Object(item.ObjectGuid.ToString("D"), item.ObjectName, item.ObjectType, item.Role,
                    slots: live.GetValueOrDefault(item.ObjectGuid)?.Slots?.Select(slot =>
                        new VIBN_Tools.ContainerGeneration.BusinessLogic.ContainerData.ContainerFeeSlot
                        { Name = slot.Key, AssignedGuid = slot.Value.ToString("D") })))));
        }
        return associations;
    }
}
