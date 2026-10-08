using System.Xml.Linq;
using VIBN_Tools.ContainerGeneration.BusinessLogic;
using VIBN_Tools.ContainerGeneration.BusinessLogic.ContainerData;
using VIBN_Tools.ContainerGeneration.Models;

namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>Reuses ContainerGeneration grouping without re-detecting known FEE slots or losing identities.</summary>
public static class FeeContainerFileGrouping
{
    public static XDocument Group(XDocument source, ContainerGenerationSettings settings)
    {
        var rules = settings.GenerateGroupingRules();
        if (!rules.IsSuccess) throw new InvalidOperationException(rules.ErrorMessage);
        if (rules.Value.Count == 0) throw new InvalidOperationException("Mindestens eine Grouping-Regel auswählen.");
        var original = source.Descendants("Container").ToArray();
        var origins = new Dictionary<MatchingData, (XElement Container, XElement Entry)>();
        var matches = new List<MatchingData>();
        foreach (var container in original)
        {
            foreach (var entry in container.Element("DataList")?.Elements("Entry") ?? [])
            {
                if (string.IsNullOrWhiteSpace(entry.Element("Signal")?.Value)) continue;
                var data = new MatchingData(container.Element("Component")?.Value ?? "", container.Element("Type")?.Value ?? "",
                    container.Element("Component")?.Value ?? "", null, null,
                    new ContainerEntry { ID = entry.Element("ID")?.Value ?? "", Address = entry.Element("Address")?.Value ?? "" }, []);
                origins[data] = (container, entry);
                matches.Add(data);
            }
        }
        var groups = new ContainerGenerator().GroupItems(ref matches, rules.Value);
        foreach (var item in matches)
        {
            var key = $"unmatched:{origins[item].Container.Attribute("id")?.Value}:{item.ContainerName}:{item.ComponentType}";
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = [];
            group.Add(item);
        }
        var result = new List<XElement>();
        var destinations = original.ToDictionary(container => container, _ => new List<XElement>());
        var index = 0;
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var separator = group.Key.IndexOf('\u001f');
            var component = group.Value[0].ContainerName;
            var identity = component + "\u001f" + group.Value[0].ComponentType;
            if (!usedNames.Add(identity))
            {
                var suffix = separator < 0 ? (index + 1).ToString() : group.Key[(separator + 1)..];
                component += "_" + suffix;
                while (!usedNames.Add(component + "\u001f" + group.Value[0].ComponentType)) component += "_";
            }
            var target = new XElement("Container", new XAttribute("id", $"group:{++index}"),
                new XElement("Component", component), new XElement("Type", group.Value[0].ComponentType),
                new XElement("DataList", group.Value.Select(item => new XElement(origins[item].Entry))));
            result.Add(target);
            foreach (var container in group.Value.Select(item => origins[item].Container).Distinct())
                destinations[container].Add(target);
        }
        foreach (var container in original)
        {
            var targets = destinations[container];
            if (targets.Count == 0) { result.Add(new XElement(container)); continue; }
            var objects = ContainerFileXml.Objects(container).ToArray();
            if (objects.Length == 0) continue;
            if (targets.Count > 1)
            {
                // Slots do not say which physical object belongs to a split group.
                // Keep these identities together for explicit reassignment in the editor.
                var retained = new XElement(container);
                retained.SetAttributeValue("id", $"retained:{++index}");
                retained.SetAttributeValue("export", "false");
                retained.SetElementValue("Component", (container.Element("Component")?.Value ?? "") + "_FEE_Zuordnung");
                foreach (var item in ContainerFileXml.Objects(retained)) item.SetAttributeValue("assignment", "Manual");
                retained.Element("DataList")!.ReplaceNodes(new XElement("Entry", new XElement("ID", ""),
                    new XElement("Address", ""), new XElement("DataType", ""), new XElement("Signal", ""),
                    new XElement("Slot", ""), new XElement("Note", "Grouping: FEE-Objekte vor Export einer Zielgruppe manuell zuordnen.")));
                result.Add(retained);
                continue;
            }
            var targetObjects = targets[0].Element("SimObjects");
            if (targetObjects is null) { targetObjects = new XElement("SimObjects"); targets[0].Add(targetObjects); }
            foreach (var item in objects)
            {
                if (targetObjects.Elements("SimObject").Any(existing => existing.Element("Guid")?.Value == item.Element("Guid")?.Value)) continue;
                var clone = new XElement(item);
                if (!string.Equals(clone.Element("Name")?.Value, targets[0].Element("Component")?.Value, StringComparison.OrdinalIgnoreCase) &&
                    !ContainerFileXml.IsStructuralObject(clone.Element("FeeType")?.Value, clone.Element("Role")?.Value))
                    clone.SetAttributeValue("assignment", "Manual");
                targetObjects.Add(clone);
            }
        }
        // Root attribution and inventory remain intact; a grouping is scoped to one root.
        var document = new XDocument(source);
        var list = document.Root?.Element("ContainerList") ?? throw new InvalidOperationException("ContainerList fehlt.");
        list.ReplaceNodes(result);
        return document;
    }
}
