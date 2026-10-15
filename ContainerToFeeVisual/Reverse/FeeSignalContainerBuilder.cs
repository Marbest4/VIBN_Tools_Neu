using System.Xml.Linq;
using VIBN_Tools.ContainerGeneration.BusinessLogic;
using VIBN_Tools.ContainerGeneration.BusinessLogic.ContainerData;
using VIBN_Tools.ContainerGeneration.Models;

namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>Uses the ContainerGeneration matcher; GUIDs and interface scope remain authoritative.</summary>
public static class FeeSignalContainerBuilder
{
    public static Fee2ContainerRoot Build(IReadOnlyList<FeeContainerLiveVariable> variables,
        IReadOnlyList<FeeContainerLiveAssignment> routes, XDocument requirements, CancellationToken token = default,
        IReadOnlyDictionary<Guid, FeeSignalEndpoint>? endpoints = null, IReadOnlyList<VisualFeeObjectLink>? objectLinks = null)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        endpoints ??= new Dictionary<Guid, FeeSignalEndpoint>();
        objectLinks ??= [];
        var available = variables.Select(item => item.VariableGuid).ToHashSet();
        routes = routes.Where(item => available.Contains(item.VariableGuid) && item.TargetObjectGuid != Guid.Empty &&
            !string.IsNullOrWhiteSpace(item.TargetSlot) && (endpoints.GetValueOrDefault(item.TargetObjectGuid) is not { } endpoint ||
                !FeeContainerReadPolicy.IsIgnored(endpoint.FeeType, endpoint.Name))).Distinct().ToArray();
        var routesByVariable = routes.ToLookup(item => item.VariableGuid);
        var active = variables.Where(item => routesByVariable[item.VariableGuid].Any()).DistinctBy(item => item.VariableGuid).ToArray();
        var byGuid = active.ToDictionary(item => item.VariableGuid);
        var templates = new Dictionary<Guid, (string Component, string Type, string Slot)>();
        var filtered = new HashSet<Guid>();
        foreach (var iface in active.GroupBy(item => item.InterfaceGuid))
        {
            token.ThrowIfCancellationRequested();
            var entries = iface.Select(variable => new ContainerEntry { FeeGuid = variable.VariableGuid.ToString("D"),
                Signal = variable.Signal, Address = string.IsNullOrWhiteSpace(variable.Address) ? variable.Path : variable.Address,
                DataType = variable.DataType, ID = variable.Comment }).ToArray();
            var rules = new[] { new GroupingRule { TargetField = item => item.ContainerName, GroupOrder = 0 },
                new GroupingRule { TargetField = item => item.ComponentType, GroupOrder = 1 } };
            var generated = new ContainerGenerator().Generate(new ContainerGenerationRequest(entries, requirements, rules, null), token);
            foreach (var container in generated.Containers)
                foreach (var entry in container.DataList)
                    if (Guid.TryParse(entry.FeeGuid, out var guid)) templates.TryAdd(guid, (container.Component, container.Type, entry.Slot));
            foreach (var entry in generated.FilteredSignals.Concat(generated.UnassignedSignals.Where(entry => entry.Note.StartsWith("Excluded", StringComparison.OrdinalIgnoreCase))))
                if (Guid.TryParse(entry.FeeGuid, out var guid)) filtered.Add(guid);
        }
        var graph = new FeeSignalOwnershipGraph(endpoints, objectLinks);
        routes = routes.Concat(routes.SelectMany(route => graph.Owners(route.TargetObjectGuid)
            .Where(owner => owner.Guid != route.TargetObjectGuid && owner.Slot.Length > 0)
            .Select(owner => new FeeContainerLiveAssignment(route.VariableGuid, owner.Guid, owner.Slot, IsIndirect: true)))).Distinct().ToArray();
        var exchanges = active.Where(variable => routesByVariable[variable.VariableGuid].SelectMany(route => graph.Owners(route.TargetObjectGuid))
            .All(owner => endpoints.GetValueOrDefault(owner.Guid) is not { } endpoint || ContainerFileXml.IsSimpleLogicType(endpoint.FeeType))).ToArray();
        var containers = FeeRobotExchangeGrouping.Create(exchanges, routes, endpoints, objectLinks).ToList();
        var exchangeSignals = containers.SelectMany(item => item.Descendants("Entry"))
            .Select(item => Guid.Parse(item.Attribute("feeGuid")!.Value)).ToHashSet();
        var owned = routes.Where(route => !exchangeSignals.Contains(route.VariableGuid)).SelectMany(route => graph.Owners(route.TargetObjectGuid)
            .Select(owner => (Owner: owner.Guid, Route: route, Slot: owner.Slot.Length > 0 ? owner.Slot : route.TargetSlot)))
            .GroupBy(item => item.Owner);
        foreach (var group in owned)
        {
            token.ThrowIfCancellationRequested();
            var endpoint = endpoints.GetValueOrDefault(group.Key);
            var first = group.First();
            templates.TryGetValue(first.Route.VariableGuid, out var fallback);
            var (type, source) = ResolveType(endpoint, fallback.Type);
            var component = !string.IsNullOrWhiteSpace(endpoint?.Name) ? endpoint.Name :
                !string.IsNullOrWhiteSpace(fallback.Component) ? fallback.Component : byGuid[first.Route.VariableGuid].Signal;
            var xml = new XElement("Container", new XAttribute("id", $"signal-target:{group.Key:D}"),
                new XAttribute("targetGuid", group.Key.ToString("D")), new XAttribute("recognitionSource", source),
                new XAttribute("origin", source == "Req.xml" ? "Aktive Signalverbindung; Typvorschlag aus Req.xml" :
                    "Aktuelles Logik-/SimObject-Ziel und Signalverbindungen, unabhängig vom Interface"),
                new XElement("Component", component), new XElement("Type", type), new XElement("DataList"));
            foreach (var item in group.DistinctBy(item => (item.Route.VariableGuid, item.Slot)))
            {
                var slot = FeeContainerLiveReconstructor.MapRuntimeSlot(type, item.Slot) ?? item.Slot;
                if (!slot.StartsWith("PLC_", StringComparison.OrdinalIgnoreCase) && templates.TryGetValue(item.Route.VariableGuid, out var template) &&
                    string.Equals(template.Type, type, StringComparison.OrdinalIgnoreCase)) slot = template.Slot;
                var note = "Über aktuelle Signalverbindung erkannt; FEE-Objekte separat zuordnen.";
                if (filtered.Contains(item.Route.VariableGuid)) note += " Req.xml-Filter übergangen: das Signal ist aktuell aktiv verknüpft.";
                xml.Element("DataList")!.Add(Entry(byGuid[item.Route.VariableGuid], slot, note));
            }
            containers.Add(xml);
        }
        foreach (var entry in containers.SelectMany(container => container.Element("DataList")?.Elements("Entry") ?? []))
            if (Guid.TryParse(entry.Attribute("feeGuid")?.Value, out var guid))
                entry.SetAttributeValue("feeType", string.Join(", ", routesByVariable[guid].Select(route => endpoints.GetValueOrDefault(route.TargetObjectGuid)?.FeeType)
                    .Where(type => !string.IsNullOrWhiteSpace(type)).Distinct()));
        var document = ContainerFileXml.Document(containers);
        var details = graph.CreateDetails(routes);
        FeeCabinetContainerGrouping.Apply(document, details, [], token);
        containers = document.Descendants("Container").ToList();
        var bindings = containers.SelectMany((container, ci) => (container.Element("DataList")?.Elements("Entry") ?? []).Select((entry, ei) =>
            new FeeContainerSignalBinding(ci, ei, Guid.Parse(entry.Attribute("feeGuid")!.Value)))).ToArray();
        var snapshot = new FeeContainerProvenanceSnapshot(new Dictionary<string, string>(), document, bindings, containers.Count, bindings.Length, "");
        return new(Guid.Empty, "Signal-Container aus ausgewählten Interfaces", snapshot, active.Length, 0, routes.Count, 0, false,
            ObjectDetails: details, LiveVariables: variables, LiveAssignments: routes, SignalEndpoints: endpoints, ObjectLinks: objectLinks);
    }

    internal static (string Type, string Source) ResolveType(FeeSignalEndpoint? endpoint, string? fallback)
    {
        if (endpoint is not null)
        {
            var type = endpoint.FeeType.Split('.').Last();
            if (ContainerFileXml.IsSimpleLogicType(type)) return (type, "Logik/Signal");
            if (type == "LogicObject")
            {
                var matches = ContainerMetadataCatalog.FindXmlTypesByLogicName(endpoint.LogicDefinitionName);
                if (matches.Count > 0) return (matches.FirstOrDefault(item => string.Equals(item, fallback, StringComparison.OrdinalIgnoreCase)) ?? matches[0], "Logik/Signal");
                if (VIBN_Tools.SpecialDevices.FeeSpecialDeviceLogicCatalog.Contains(endpoint.LogicDefinitionName)) return (endpoint.LogicDefinitionName, "Logik/Signal");
                return ("N/A", "Logik/Signal");
            }
            var known = type switch { "CabinetElement" => FeeContainerLiveReconstructor.MapCabinetType(endpoint.CabinetDefinition) ?? "N/A",
                "Cabinet" => "Cabinet", "Sensor" or "SafetySensor" => "Sensor", "Button" => "Button",
                "SegmentedLamp" => "Stacklight", "Floor" => "Stop", _ => "" };
            if (known.Length > 0) return (known, "Logik/Signal");
        }
        return string.IsNullOrWhiteSpace(fallback) ? ("N/A", "Logik/Signal") : (fallback, "Req.xml");
    }

    internal static XElement Entry(FeeContainerLiveVariable variable, string slot, string note) => new("Entry",
        new XAttribute("feeGuid", variable.VariableGuid.ToString("D")), new XAttribute("interfaceGuid", variable.InterfaceGuid),
        new XAttribute("interfaceName", variable.InterfaceName), new XElement("ID", variable.Comment),
        new XElement("Address", string.IsNullOrWhiteSpace(variable.Address) ? variable.Path : variable.Address),
        new XElement("DataType", variable.DataType), new XElement("Signal", variable.Signal), new XElement("Slot", slot), new XElement("Note", note));
}
