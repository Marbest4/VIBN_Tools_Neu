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
        }
        // Calculate the original provenance identities before assigning editor
        // IDs. Empty/repeated XML IDs must never serve as association keys.
        ContainerFileXml.EnsureUniqueContainerIds(snapshot.ContainerDocument);
        var stored = new List<FeeContainerObjectAssociation>();
        foreach (var container in containers)
        {
            var id = container.Attribute("id")!.Value;
            var name = container.Element("Component")?.Value ?? "";
            // Explicit manual associations survive even when names differ.
            foreach (var item in ContainerFileXml.Objects(container))
                if (Guid.TryParse(item.Element("Guid")?.Value, out var guid) && live.TryGetValue(guid, out var actual) &&
                    ContainerFileXml.CanRetainObjectAssociation(item, actual.Name, name))
                    stored.Add(new FeeContainerObjectAssociation(guid, actual.Name, actual.FeeType,
                        Guid.Empty, id, item.Attribute("assignment")?.Value == "Manual" ? "Manuelle Zuordnung aus Container-Provenienz" : "Gespeicherte Zuordnung mit gleichem Namen",
                        item.Element("Role")?.Value ?? "SimObject", IsManual: item.Attribute("assignment")?.Value == "Manual"));
        }
        var storedGuids = stored.Select(item => item.ObjectGuid).ToHashSet();
        foreach (var group in stored.GroupBy(item => item.ObjectGuid))
            if (group.Select(item => item.ContainerId).Distinct(StringComparer.Ordinal).Count() == 1)
                associations.Add(group.FirstOrDefault(item => item.IsManual) ?? group.First());
        foreach (var group in reconstructed.ObjectAssociations.GroupBy(item => item.ContainerId))
        {
            var source = reconstructed.Snapshot.ContainerDocument.Descendants("Container").FirstOrDefault(item => item.Attribute("id")?.Value == group.Key);
            if (source is null) continue;
            var provenanceId = group.First().ProvenanceContainerId;
            var matches = containers.Where(item => !string.IsNullOrWhiteSpace(provenanceId) && stableIds[item] == provenanceId).ToArray();
            if (matches.Length == 0)
                matches = containers.Where(item =>
                    ContainerFileXml.HasMatchingObjectName(item.Element("Component")?.Value, source.Element("Component")?.Value) &&
                    string.Equals(item.Element("Type")?.Value, source.Element("Type")?.Value, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) continue;
            foreach (var item in group)
                if (!storedGuids.Contains(item.ObjectGuid) && !associations.Any(association => association.ObjectGuid == item.ObjectGuid) &&
                    live.TryGetValue(item.ObjectGuid, out var actual) &&
                    (item.IsManual || ContainerFileXml.HasMatchingObjectName(actual.Name, matches[0].Element("Component")?.Value)))
                    associations.Add(item with { ContainerId = matches[0].Attribute("id")!.Value,
                        ObjectName = actual.Name, ObjectType = actual.FeeType });
        }
        foreach (var container in containers)
        {
            container.Element("SimObjects")?.Remove();
            container.Add(new XElement("SimObjects", associations.Where(item => item.ContainerId == container.Attribute("id")!.Value)
                .Select(item => ContainerFileXml.Object(item.ObjectGuid.ToString("D"), item.ObjectName, item.ObjectType, item.Role,
                    slots: live.GetValueOrDefault(item.ObjectGuid)?.Slots?.Select(slot =>
                        new VIBN_Tools.ContainerGeneration.BusinessLogic.ContainerData.ContainerFeeSlot
                        { Name = slot.Key, AssignedGuid = slot.Value.ToString("D") }), assignmentKind: item.IsManual ? "Manual" : "Automatic"))));
        }
        return associations;
    }
}
