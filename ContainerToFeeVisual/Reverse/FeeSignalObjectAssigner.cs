using System.Xml.Linq;
using VIBN_Tools.ContainerGeneration.Models;

namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>Applies current scene evidence to existing signal containers; never writes to FEE.</summary>
public static class FeeSignalObjectAssigner
{
    public static Fee2ContainerRoot Assign(Fee2ContainerRoot workspace, IReadOnlyList<Fee2ContainerRoot> selectedRoots, CancellationToken token = default)
    {
        var source = workspace.Provenance ?? throw new InvalidOperationException("Zuerst Container aus den ausgewählten Interfaces erstellen.");
        var document = new XDocument(source.ContainerDocument);
        var scope = (workspace.LiveVariables ?? []).Select(item => item.InterfaceGuid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var variables = selectedRoots.SelectMany(root => root.LiveVariables ?? []).Where(item => scope.Contains(item.InterfaceGuid))
            .DistinctBy(item => item.VariableGuid).ToArray();
        var currentVariables = variables.ToDictionary(item => item.VariableGuid);
        var currentGuids = currentVariables.Keys.ToHashSet();
        var routes = selectedRoots.SelectMany(root => root.LiveAssignments ?? []).Where(item => currentGuids.Contains(item.VariableGuid)).Distinct().ToArray();
        var details = selectedRoots.SelectMany(root => root.ObjectDetails ?? []).DistinctBy(item => item.Guid).ToArray();
        var byGuid = details.ToDictionary(item => item.Guid);
        var routesByVariable = routes.ToLookup(item => item.VariableGuid);
        var routesByTarget = routes.ToLookup(item => item.TargetObjectGuid);
        var sourceAssociations = selectedRoots.SelectMany(root => root.ObjectAssociations ?? []).ToArray();
        var associationsByOwner = sourceAssociations.ToLookup(item => item.ContainerObjectGuid);
        var associationsByRootContainer = selectedRoots.SelectMany(root => (root.ObjectAssociations ?? []).Select(item => (Root: root.Guid, Item: item)))
            .ToLookup(item => (item.Root, item.Item.ContainerId), item => item.Item);
        var ownerRoots = selectedRoots.SelectMany(root => (root.ObjectDetails ?? []).Select(item => (Root: root, item.Guid)))
            .DistinctBy(item => item.Guid).ToDictionary(item => item.Guid, item => item.Root);
        var sourceObjects = selectedRoots.SelectMany(root => root.Provenance?.ContainerDocument.Descendants("SimObject") ?? [])
            .Where(item => Guid.TryParse(item.Element("Guid")?.Value, out _)).GroupBy(item => Guid.Parse(item.Element("Guid")!.Value))
            .ToDictionary(group => group.Key, group => group.First());
        var candidates = selectedRoots.SelectMany(root => root.Provenance?.ContainerDocument.Descendants("Container").Select(container =>
            (Root: root, Container: container, Variables: container.Descendants("Entry").Select(entry => Guid.TryParse(entry.Attribute("feeGuid")?.Value, out var guid) ? guid : Guid.Empty).ToHashSet())) ?? [])
            .ToArray();
        var candidatesByVariable = candidates.SelectMany(candidate => candidate.Variables.Where(guid => guid != Guid.Empty).Select(guid => (Guid: guid, Candidate: candidate)))
            .ToLookup(item => item.Guid, item => item.Candidate);
        var allAssociations = new List<FeeContainerObjectAssociation>();
        foreach (var container in document.Descendants("Container"))
        {
            token.ThrowIfCancellationRequested();
            var id = container.Attribute("id")!.Value;
            var name = container.Element("Component")!.Value;
            var entryGuids = container.Descendants("Entry").Select(entry => Guid.TryParse(entry.Attribute("feeGuid")?.Value, out var guid) ? guid : Guid.Empty).ToHashSet();
            var targetGuid = Guid.TryParse(container.Attribute("targetGuid")?.Value, out var target) ? target : Guid.Empty;
            var matches = entryGuids.SelectMany(guid => candidatesByVariable[guid]).DistinctBy(candidate => (candidate.Root.Guid, candidate.Container.Attribute("id")?.Value)).ToArray();
            var preferred = matches.Where(candidate => ContainerFileXml.HasMatchingObjectName(candidate.Container.Element("Component")?.Value, name)).ToArray();
            if (preferred.Length > 0) matches = preferred;
            var members = new List<FeeContainerObjectAssociation>();
            if (FeeRobotExchangeGrouping.IsExchange(container.Element("Type")?.Value ?? ""))
            {
                foreach (var endpoint in routes.Where(route => entryGuids.Contains(route.VariableGuid)).Select(route => route.TargetObjectGuid).Distinct())
                    if (byGuid.TryGetValue(endpoint, out var actual) && ContainerFileXml.IsSimpleLogicType(actual.FeeType))
                        members.Add(new(endpoint, actual.Name, actual.FeeType, endpoint, id, "Aktuelle Signalverbindung des Signalaustauschs", "TechnicalHelper",
                            ParentName: actual.ParentName, LogicDefinitionName: actual.LogicDefinitionName, ConnectionText: actual.ConnectionText));
            }
            else if (targetGuid != Guid.Empty && byGuid.TryGetValue(targetGuid, out var detail) &&
                routesByTarget[targetGuid].Any(route => entryGuids.Contains(route.VariableGuid)))
            {
                container.SetElementValue("Component", detail.Name);
                var ownerRoot = ownerRoots.GetValueOrDefault(targetGuid);
                if (ownerRoot is not null) container.SetAttributeValue("feeRootGuid", ownerRoot.Guid.ToString("D"));
                members.Add(new(targetGuid, detail.Name, detail.FeeType, targetGuid, id,
                    "Über aktive Signal-GUID und aktuelle Slotverbindung zugeordnet", "Primary", ParentName: detail.ParentName,
                    LogicDefinitionName: detail.LogicDefinitionName, ConnectionText: detail.ConnectionText));
                members.AddRange(associationsByOwner[targetGuid].Where(item => item.ObjectGuid != targetGuid && (item.Role == "TechnicalHelper" || ContainerFileXml.HasMatchingObjectName(item.ObjectName, detail.Name)))
                    .Select(item => item with { ContainerId = id }));
                if (ContainerFileXml.IsSimpleLogicType(detail.FeeType))
                {
                    container.SetAttributeValue("origin", $"Aktives Signal an SimpleLogicObject ({detail.FeeType})");
                    if (container.Element("Type")?.Value == "N/A") container.SetElementValue("Type", detail.FeeType.Split('.').Last());
                }
                var definition = matches.Where(candidate => associationsByRootContainer[(candidate.Root.Guid, candidate.Container.Attribute("id")?.Value ?? "")].Any(item =>
                    item.ObjectGuid == targetGuid && (item.Role is "Primary" or "SpecialDevice"))).ToArray();
                if (container.Element("Type")?.Value == "N/A" && definition.Length == 1 && definition[0].Container.Element("Type")?.Value is { } actualType && actualType != "N/A")
                {
                    container.SetElementValue("Type", actualType);
                    container.SetAttributeValue("recognitionSource", definition[0].Container.Attribute("recognitionSource")?.Value ?? "Logik");
                    container.SetAttributeValue("origin", $"Aktives Signal / aktuelle FEE-Typdefinition ({actualType})");
                    foreach (var entry in container.Element("DataList")?.Elements("Entry") ?? [])
                    {
                        var variable = entry.Attribute("feeGuid")?.Value;
                        var slots = definition[0].Container.Descendants("Entry").Where(item => item.Attribute("feeGuid")?.Value == variable)
                            .Select(item => item.Element("Slot")?.Value).Where(slot => !string.IsNullOrWhiteSpace(slot)).Distinct().ToArray();
                        if (slots.Length == 1) entry.SetElementValue("Slot", slots[0]);
                    }
                }
            }
            else if (matches.Length == 1)
            {
                var match = matches[0];
                members.AddRange(associationsByRootContainer[(match.Root.Guid, match.Container.Attribute("id")?.Value ?? "")].Where(item =>
                    (item.Role is "Primary" or "SpecialDevice" or "TechnicalHelper" || ContainerFileXml.HasMatchingObjectName(item.ObjectName, name)))
                    .Select(item => item with { ContainerId = id, Reason = $"Aktive Signal-GUIDs / FEE-Root '{match.Root.Name}'" }));
                container.SetAttributeValue("feeRootGuid", match.Root.Guid.ToString("D"));
            }
            // Explicit manual choices stay intact inside the selected root scope.
            foreach (var item in ContainerFileXml.Objects(container).Where(item => item.Attribute("assignment")?.Value == "Manual"))
                if (Guid.TryParse(item.Element("Guid")?.Value, out var guid) && byGuid.TryGetValue(guid, out var actual))
                    members.Add(new(guid, actual.Name, actual.FeeType, Guid.Empty, id, "Manuelle Zuordnung beibehalten",
                        item.Element("Role")?.Value ?? "SimObject", IsManual: true, ParentName: actual.ParentName,
                        LogicDefinitionName: actual.LogicDefinitionName, ConnectionText: actual.ConnectionText));
            members = members.DistinctBy(item => item.ObjectGuid).ToList();
            container.Element("SimObjects")?.Remove();
            container.Add(new XElement("SimObjects", members.Select(item => ObjectElement(item, sourceObjects.GetValueOrDefault(item.ObjectGuid)))));
            allAssociations.AddRange(members);
        }
        FeeCabinetContainerGrouping.Apply(document, details, allAssociations, token);
        var bindingGuids = (workspace.LiveVariables ?? []).Select(item => item.VariableGuid).ToHashSet();
        foreach (var container in document.Descendants("Container"))
            foreach (var entry in container.Element("DataList")?.Elements("Entry") ?? [])
                if (Guid.TryParse(entry.Attribute("feeGuid")?.Value, out var guid))
                {
                    var types = string.Join(", ", routesByVariable[guid].Select(route => byGuid.GetValueOrDefault(route.TargetObjectGuid)?.FeeType)
                        .Where(type => !string.IsNullOrWhiteSpace(type)).Distinct());
                    if (types.Length > 0) entry.SetAttributeValue("feeType", types);
                }
        var assigned = allAssociations.Select(item => item.ObjectGuid).ToHashSet();
        var unassigned = details.Where(item => !assigned.Contains(item.Guid)).Select(item =>
            new FeeContainerUnmappedObject(item.Guid, item.Name, item.FeeType, "Keine eindeutige Zuordnung zu den Signalcontainern der ausgewählten Interfaces")).ToArray();
        document.Root?.Element("FeeInventory")?.Remove();
        document.Root?.Add(new XElement("FeeInventory",
            new XElement("SimObjects", sourceObjects.Where(item => byGuid.ContainsKey(item.Key)).Select(item => new XElement(item.Value))),
            new XElement("Signals", (workspace.LiveVariables ?? []).Select(old => currentVariables.GetValueOrDefault(old.VariableGuid) ?? old)
                .Select(variable => new XElement("Signal", new XElement("Guid", variable.VariableGuid.ToString("D")),
                    new XElement("InterfaceGuid", variable.InterfaceGuid), new XElement("InterfaceName", variable.InterfaceName),
                    new XElement("Tag", variable.Signal), new XElement("Address", variable.Address), new XElement("Path", variable.Path), new XElement("DataType", variable.DataType))))));
        var containers = document.Descendants("Container").ToArray();
        var bindings = containers.SelectMany((container, ci) => (container.Element("DataList")?.Elements("Entry") ?? []).Select((entry, ei) =>
            new { Entry = entry, Container = ci, Index = ei })).Where(item => Guid.TryParse(item.Entry.Attribute("feeGuid")?.Value, out var guid) && bindingGuids.Contains(guid))
            .Select(item => new FeeContainerSignalBinding(item.Container, item.Index, Guid.Parse(item.Entry.Attribute("feeGuid")!.Value))).ToArray();
        var snapshot = source with { ContainerDocument = document, SignalBindings = bindings, ContainerCount = containers.Length, SignalCount = bindings.Length };
        // Retain project-wide direct routes as well: helper-only variables must remain visible.
        return workspace with { Provenance = snapshot, ObjectAssociations = allAssociations, ObjectDetails = details,
            NonContainerObjects = unassigned, LiveAssignments = (workspace.LiveAssignments ?? []).Concat(routes).Distinct().ToArray(),
            LiveVariables = (workspace.LiveVariables ?? []).Select(old => currentVariables.GetValueOrDefault(old.VariableGuid) ?? old).ToArray(),
            SpecialDevices = selectedRoots.SelectMany(root => root.SpecialDevices ?? []).DistinctBy(item => item.Guid).ToArray() };
    }

    private static XElement ObjectElement(FeeContainerObjectAssociation association, XElement? source)
    {
        var kind = association.IsManual ? "Manual" : ContainerFileXml.IsStructuralObjectAssociation(association.ObjectType, association.Role) ? "Linked" : "Automatic";
        var result = source is null ? ContainerFileXml.Object(association.ObjectGuid.ToString("D"), association.ObjectName, association.ObjectType, association.Role) : new XElement(source);
        result.SetAttributeValue("assignment", kind);
        result.SetElementValue("Name", association.ObjectName); result.SetElementValue("FeeType", association.ObjectType);
        result.SetElementValue("Role", association.Role); result.SetElementValue("LogicDefinitionName", association.LogicDefinitionName);
        return result;
    }
}
