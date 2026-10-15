using System.Xml.Linq;
using VIBN_Tools.ContainerGeneration.Models;

namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>Cabinet elements are typed children; only their Cabinet is a top-level container.</summary>
public static class FeeCabinetContainerGrouping
{
    internal static Fee2ContainerRoot Normalize(Fee2ContainerRoot root)
    {
        if (root.Provenance is not { } snapshot) return root;
        var elementGuids = (root.ObjectDetails ?? []).Where(item => item.FeeType.Split('.').Last() == "CabinetElement").Select(item => item.Guid).ToHashSet();
        if (!snapshot.ContainerDocument.Descendants("Container").Any(container => container.Element("Type")?.Value == "CabinetElement" ||
            ContainerFileXml.Objects(container).Any(item => item.Element("FeeType")?.Value?.Split('.').Last() == "CabinetElement") ||
            Guid.TryParse(container.Attribute("targetGuid")?.Value, out var target) && elementGuids.Contains(target))) return root;
        var document = new XDocument(snapshot.ContainerDocument);
        var originals = document.Descendants("Container").ToArray();
        foreach (var binding in snapshot.SignalBindings)
            if (binding.ContainerIndex >= 0 && binding.ContainerIndex < originals.Length)
                originals[binding.ContainerIndex].Element("DataList")?.Elements("Entry").ElementAtOrDefault(binding.EntryIndex)?.SetAttributeValue("feeGuid", binding.VariableGuid.ToString("D"));
        var associations = (root.ObjectAssociations ?? []).ToList();
        var originalIds = originals.ToDictionary(item => item, item => item.Attribute("id")?.Value ?? "");
        ContainerFileXml.EnsureUniqueContainerIds(document);
        for (var index = 0; index < associations.Count; index++)
        {
            var association = associations[index];
            var matches = originals.Where(item => originalIds[item] == association.ContainerId).ToArray();
            if (matches.Length > 1) matches = matches.Where(item => ContainerFileXml.Objects(item).Any(obj => obj.Element("Guid")?.Value == association.ObjectGuid.ToString("D"))).ToArray();
            if (matches.Length == 1) associations[index] = association with { ContainerId = matches[0].Attribute("id")!.Value };
        }
        Apply(document, root.ObjectDetails ?? [], associations);
        var containers = document.Descendants("Container").ToArray();
        var bindings = containers.SelectMany((container, ci) => (container.Element("DataList")?.Elements("Entry") ?? []).Select((entry, ei) => (entry, ci, ei)))
            .Where(item => Guid.TryParse(item.entry.Attribute("feeGuid")?.Value, out var guid) && guid != Guid.Empty)
            .Select(item => new FeeContainerSignalBinding(item.ci, item.ei, Guid.Parse(item.entry.Attribute("feeGuid")!.Value))).ToArray();
        return root with { Provenance = snapshot with { ContainerDocument = document, SignalBindings = bindings, ContainerCount = containers.Length,
                SignalCount = containers.Sum(container => (container.Element("DataList")?.Elements("Entry") ?? []).Count(entry => !string.IsNullOrWhiteSpace(entry.Element("Signal")?.Value))) },
            ObjectAssociations = associations };
    }

    public static void Apply(XDocument document, IReadOnlyList<FeeContainerObjectDetails> details,
        List<FeeContainerObjectAssociation> associations, CancellationToken token = default)
    {
        var byGuid = details.DistinctBy(item => item.Guid).ToDictionary(item => item.Guid);
        var list = document.Root?.Element("ContainerList") ?? throw new InvalidOperationException("ContainerList fehlt.");
        foreach (var child in list.Elements("Container").Where(item => item.Element("Type")?.Value != "Cabinet").ToArray())
        {
            token.ThrowIfCancellationRequested();
            var id = child.Attribute("id")?.Value ?? "";
            var elements = associations.Where(item => item.ContainerId == id && item.ObjectType.Split('.').Last() == "CabinetElement")
                .Select(item => byGuid.GetValueOrDefault(item.ObjectGuid)).Where(item => item is not null).Cast<FeeContainerObjectDetails>().ToList();
            elements.AddRange(ContainerFileXml.Objects(child).Where(item => item.Element("FeeType")?.Value == "CabinetElement")
                .Select(item => Guid.TryParse(item.Element("Guid")?.Value, out var guid) ? byGuid.GetValueOrDefault(guid) : null)
                .Where(item => item is not null).Cast<FeeContainerObjectDetails>());
            if (Guid.TryParse(child.Attribute("targetGuid")?.Value, out var target) && byGuid.GetValueOrDefault(target) is { FeeType: "CabinetElement" } endpoint)
                elements.Add(endpoint);
            elements = elements.DistinctBy(item => item.Guid).ToList();
            if (elements.Count == 0 && child.Element("Type")?.Value != "CabinetElement" &&
                !ContainerFileXml.Objects(child).Any(item => item.Element("FeeType")?.Value == "CabinetElement")) continue;
            if (elements.Count == 1) child.SetAttributeValue("targetGuid", elements[0].Guid.ToString("D"));
            if (child.Element("Type")?.Value is "CabinetElement" or "N/A")
            {
                var types = elements.Select(item => FeeContainerLiveReconstructor.MapCabinetType(item.CabinetDefinition)).Where(type => !string.IsNullOrWhiteSpace(type)).Distinct().ToArray();
                child.SetElementValue("Type", types.Length == 1 ? types[0] : "N/A");
            }
            var parentGuids = elements.Select(item => item.ParentGuid).Where(value => Guid.TryParse(value, out var guid) && guid != Guid.Empty).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var parentGuid = parentGuids.Length == 1 ? parentGuids[0] : "";
            var parent = Guid.TryParse(parentGuid, out var guid) ? byGuid.GetValueOrDefault(guid) : null;
            if (parent?.FeeType.Split('.').Last() != "Cabinet") { parent = null; parentGuid = ""; }
            var parentName = parent?.Name ?? "Cabinet unbekannt – prüfen";
            var parentId = parentGuid.Length > 0 ? "cabinet:" + parentGuid : "cabinet:unresolved:" + id;
            child.SetAttributeValue("parentName", parentName); child.SetAttributeValue("parentGuid", parentGuid);
            child.SetAttributeValue("parentContainerId", parentId);
            var group = list.Elements("Container").FirstOrDefault(item => item.Attribute("id")?.Value == parentId);
            group ??= list.Elements("Container").FirstOrDefault(item => item.Element("Type")?.Value == "Cabinet" && parentGuid.Length > 0 &&
                (item.Attribute("targetGuid")?.Value == parentGuid || ContainerFileXml.Objects(item).Any(obj => obj.Element("Guid")?.Value == parentGuid)));
            if (group is not null) { parentId = group.Attribute("id")!.Value; child.SetAttributeValue("parentContainerId", parentId); }
            if (group is null)
            {
                group = new XElement("Container", new XAttribute("id", parentId), new XAttribute("recognitionSource", "Logik/Signal"),
                    new XAttribute("origin", parentGuid.Length > 0 ? "Aktuelle Cabinet-Hierarchie und verknüpfte CabinetElemente" : "Cabinet-Parent fehlt oder ist mehrdeutig; Zuordnung prüfen"),
                    new XElement("Component", parentName), new XElement("Type", "Cabinet"), new XElement("DataList"));
                list.Add(group);
            }
            group.SetElementValue("Component", parentName);
            if (parent is not null && associations.Any(item => item.ContainerId == id && item.ObjectType.Split('.').Last() == "CabinetElement"))
            {
                group.SetAttributeValue("parentName", parent.ParentName); group.SetAttributeValue("parentGuid", parent.ParentGuid);
                group.Element("SimObjects")?.Remove();
                group.Add(new XElement("SimObjects", ContainerFileXml.Object(parent.Guid.ToString("D"), parent.Name, "Cabinet", "Primary", assignmentKind: "Linked")));
                associations.RemoveAll(item => item.ContainerId == id && item.ObjectGuid == parent.Guid);
                foreach (var old in ContainerFileXml.Objects(child).Where(item => item.Element("Guid")?.Value == parent.Guid.ToString("D")).ToArray()) old.Remove();
                if (!associations.Any(item => item.ContainerId == parentId && item.ObjectGuid == parent.Guid))
                    associations.Add(new(parent.Guid, parent.Name, "Cabinet", parent.Guid, parentId, "Aktuelles Parent der CabinetElemente", "Primary", ParentName: parent.ParentName));
            }
        }
        // Consolidate old interface-specific copies of the same physical element.
        foreach (var duplicates in list.Elements("Container").Where(item => item.Attribute("parentContainerId") is not null && item.Attribute("targetGuid") is not null)
            .GroupBy(item => item.Attribute("parentContainerId")!.Value + ":" + item.Attribute("targetGuid")!.Value).ToArray())
        {
            var keep = duplicates.First();
            foreach (var duplicate in duplicates.Skip(1).ToArray())
            {
                foreach (var entry in duplicate.Element("DataList")?.Elements("Entry") ?? [])
                    if (!keep.Element("DataList")!.Elements("Entry").Any(old => old.Attribute("feeGuid")?.Value == entry.Attribute("feeGuid")?.Value && old.Element("Slot")?.Value == entry.Element("Slot")?.Value))
                        keep.Element("DataList")!.Add(new XElement(entry));
                for (var index = 0; index < associations.Count; index++)
                    if (associations[index].ContainerId == duplicate.Attribute("id")?.Value) associations[index] = associations[index] with { ContainerId = keep.Attribute("id")!.Value };
                if (duplicate.Element("SimObjects") is { } extra)
                {
                    if (keep.Element("SimObjects") is null) keep.Add(new XElement("SimObjects"));
                    foreach (var item in extra.Elements())
                        if (!ContainerFileXml.Objects(keep).Any(old => old.Element("Guid")?.Value == item.Element("Guid")?.Value))
                            keep.Element("SimObjects")!.Add(new XElement(item));
                }
                duplicate.Remove();
            }
        }
        foreach (var group in list.Elements("Container").Where(item => item.Element("Type")?.Value == "Cabinet" && item.Attribute("id")?.Value.StartsWith("cabinet:", StringComparison.Ordinal) == true).ToArray())
            if (!list.Elements("Container").Any(item => item.Attribute("parentContainerId")?.Value == group.Attribute("id")?.Value) && group.Element("DataList")?.Elements("Entry").Any() != true)
            { associations.RemoveAll(item => item.ContainerId == group.Attribute("id")?.Value); group.Remove(); }
    }
}
