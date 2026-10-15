using System.Xml.Linq;
using VIBN_Tools.ContainerGeneration.Models;

namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>Flat schema-compatible container records retain explicit parent identities; the UI presents the hierarchy.</summary>
public static class FeeCabinetContainerGrouping
{
    public static void Apply(XDocument document, IReadOnlyList<FeeContainerObjectDetails> details,
        List<FeeContainerObjectAssociation> associations, CancellationToken token = default)
    {
        var byGuid = details.ToDictionary(item => item.Guid);
        var list = document.Root?.Element("ContainerList") ?? throw new InvalidOperationException("ContainerList fehlt.");
        foreach (var child in list.Elements("Container").Where(item => item.Element("Type")?.Value != "Cabinet").ToArray())
        {
            token.ThrowIfCancellationRequested();
            var id = child.Attribute("id")?.Value ?? "";
            var elements = associations.Where(item => item.ContainerId == id && item.ObjectType.Split('.').Last() == "CabinetElement").ToArray();
            var parents = elements.Select(item => byGuid.GetValueOrDefault(item.ObjectGuid)).Where(item => item is not null &&
                Guid.TryParse(item.ParentGuid, out var guid) && byGuid.GetValueOrDefault(guid)?.FeeType.Split('.').Last() == "Cabinet")
                .Select(item => byGuid[Guid.Parse(item!.ParentGuid)]).DistinctBy(item => item.Guid).ToArray();
            child.SetAttributeValue("parentName", null); child.SetAttributeValue("parentGuid", null); child.SetAttributeValue("parentContainerId", null);
            if (parents.Length != 1) continue;
            var parent = parents[0];
            var parentId = "cabinet:" + parent.Guid.ToString("D");
            child.SetAttributeValue("parentName", parent.Name); child.SetAttributeValue("parentGuid", parent.Guid.ToString("D"));
            child.SetAttributeValue("parentContainerId", parentId);
            var group = list.Elements("Container").FirstOrDefault(item => item.Attribute("id")?.Value == parentId);
            if (group is null)
            {
                group = new XElement("Container", new XAttribute("id", parentId), new XAttribute("recognitionSource", "Logik"),
                    new XAttribute("origin", "Aktuelle Cabinet-Hierarchie und verknüpfte CabinetElemente"),
                    new XElement("Component", parent.Name), new XElement("Type", "Cabinet"), new XElement("DataList"),
                    new XElement("SimObjects", ContainerFileXml.Object(parent.Guid.ToString("D"), parent.Name, "Cabinet", "Primary", assignmentKind: "Linked")));
                list.Add(group);
            }
            group.SetElementValue("Component", parent.Name);
            group.SetAttributeValue("parentName", parent.ParentName);
            group.SetAttributeValue("parentGuid", parent.ParentGuid);
            var groupObjects = group.Element("SimObjects");
            if (groupObjects is null) { groupObjects = new XElement("SimObjects"); group.Add(groupObjects); }
            groupObjects.ReplaceNodes(ContainerFileXml.Object(parent.Guid.ToString("D"), parent.Name, "Cabinet", "Primary", assignmentKind: "Linked"));
            associations.RemoveAll(item => item.ContainerId == id && item.ObjectGuid == parent.Guid);
            foreach (var old in ContainerFileXml.Objects(child).Where(item => item.Element("Guid")?.Value == parent.Guid.ToString("D")).ToArray()) old.Remove();
            if (!associations.Any(item => item.ContainerId == parentId && item.ObjectGuid == parent.Guid))
                associations.Add(new(parent.Guid, parent.Name, "Cabinet", parent.Guid, parentId, "Aktuelles Parent der CabinetElemente", "Primary", ParentName: parent.ParentName));
        }
        // Reassignment with fewer roots must not retain an empty historical cabinet group.
        foreach (var group in list.Elements("Container").Where(item => item.Element("Type")?.Value == "Cabinet").ToArray())
            if (!list.Elements("Container").Any(item => item.Attribute("parentContainerId")?.Value == group.Attribute("id")?.Value))
            { associations.RemoveAll(item => item.ContainerId == group.Attribute("id")?.Value); group.Remove(); }
    }
}
