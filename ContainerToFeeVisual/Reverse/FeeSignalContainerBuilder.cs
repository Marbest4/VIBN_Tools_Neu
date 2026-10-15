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
        IReadOnlyDictionary<Guid, FeeSignalEndpoint>? endpoints = null)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        var available = variables.Select(item => item.VariableGuid).ToHashSet();
        routes = routes.Where(item => available.Contains(item.VariableGuid) && item.TargetObjectGuid != Guid.Empty &&
            !string.IsNullOrWhiteSpace(item.TargetSlot)).Distinct().ToArray();
        var routesByVariable = routes.ToLookup(item => item.VariableGuid);
        var connected = routes.Select(item => item.VariableGuid).ToHashSet();
        var active = variables.Where(item => connected.Contains(item.VariableGuid)).DistinctBy(item => item.VariableGuid).ToArray();
        var byGuid = active.ToDictionary(item => item.VariableGuid);
        endpoints ??= new Dictionary<Guid, FeeSignalEndpoint>();
        var containers = FeeRobotExchangeGrouping.Create(active, routes, endpoints).ToList();
        var exchangeSignals = containers.SelectMany(item => item.Descendants("Entry")).Select(item => Guid.Parse(item.Attribute("feeGuid")!.Value)).ToHashSet();
        foreach (var iface in active.GroupBy(item => item.InterfaceGuid))
        {
            token.ThrowIfCancellationRequested();
            var used = new HashSet<Guid>(exchangeSignals);
            var excluded = new HashSet<Guid>();
            if (requirements is not null)
            {
                var entries = iface.Where(variable => !exchangeSignals.Contains(variable.VariableGuid)).Select(variable => new ContainerEntry { FeeGuid = variable.VariableGuid.ToString("D"),
                    Signal = variable.Signal, Address = string.IsNullOrWhiteSpace(variable.Address) ? variable.Path : variable.Address,
                    DataType = variable.DataType, ID = variable.Comment }).ToArray();
                var rules = new[] { new GroupingRule { TargetField = item => item.ContainerName, GroupOrder = 0 },
                    new GroupingRule { TargetField = item => item.ComponentType, GroupOrder = 1 } };
                var result = new ContainerGenerator().Generate(new ContainerGenerationRequest(entries, requirements, rules, null), token);
                foreach (var entry in result.FilteredSignals.Concat(result.UnassignedSignals.Where(entry =>
                    entry.Note.StartsWith("Excluded", StringComparison.OrdinalIgnoreCase))))
                    if (Guid.TryParse(entry.FeeGuid, out var guid)) excluded.Add(guid);
                foreach (var generated in result.Containers)
                {
                    var xml = new XElement("Container", new XAttribute("id", $"signal:{iface.Key}:{containers.Count}"),
                        new XAttribute("origin", "Signal und Req. XML"), new XAttribute("recognitionSource", "Req.xml"), new XElement("Component", generated.Component),
                        new XElement("Type", generated.Type), new XElement("DataList"));
                    foreach (var entry in generated.DataList)
                        if (Guid.TryParse(entry.FeeGuid, out var guid) && byGuid.TryGetValue(guid, out var variable))
                        { xml.Element("DataList")!.Add(Entry(variable, entry.Slot, entry.Note)); used.Add(guid); }
                    if (xml.Element("DataList")!.HasElements) containers.Add(xml);
                }
            }
            var remaining = iface.Where(variable => !used.Contains(variable.VariableGuid) && !excluded.Contains(variable.VariableGuid))
                .Select(variable => variable.VariableGuid).ToHashSet();
            foreach (var target in routes.Where(route => remaining.Contains(route.VariableGuid)).GroupBy(route => route.TargetObjectGuid))
            {
                var endpoint = endpoints.GetValueOrDefault(target.Key);
                var type = ContainerFileXml.IsSimpleLogicType(endpoint?.FeeType) ? endpoint!.FeeType : "N/A";
                var xml = new XElement("Container", new XAttribute("id", $"signal-target:{iface.Key}:{target.Key:D}"),
                    new XAttribute("targetGuid", target.Key.ToString("D")), new XAttribute("recognitionSource", "Logik"),
                    new XAttribute("origin", "Aktives Signal und aktuelle FEE-Slotverbindung"),
                    new XElement("Component", string.IsNullOrWhiteSpace(endpoint?.Name) ? byGuid[target.First().VariableGuid].Signal : endpoint.Name), new XElement("Type", type),
                    new XElement("DataList", target.Distinct().Select(route => Entry(byGuid[route.VariableGuid], route.TargetSlot,
                        "Über aktuelle Signalverbindung erkannt. FEE-Root auswählen und Objekte separat zuordnen."))));
                containers.Add(xml);
            }
        }
        foreach (var entry in containers.SelectMany(container => container.Element("DataList")?.Elements("Entry") ?? []))
            if (Guid.TryParse(entry.Attribute("feeGuid")?.Value, out var guid))
                entry.SetAttributeValue("feeType", string.Join(", ", routesByVariable[guid].Select(route => endpoints.GetValueOrDefault(route.TargetObjectGuid)?.FeeType)
                    .Where(type => !string.IsNullOrWhiteSpace(type)).Distinct()));
        var document = ContainerFileXml.Document(containers);
        var bindings = containers.SelectMany((container, ci) => container.Descendants("Entry").Select((entry, ei) =>
            new FeeContainerSignalBinding(ci, ei, Guid.Parse(entry.Attribute("feeGuid")!.Value)))).ToArray();
        var snapshot = new FeeContainerProvenanceSnapshot(new Dictionary<string, string>(), document, bindings, containers.Count, bindings.Length, "");
        return new(Guid.Empty, "Signal-Container aus ausgewählten Interfaces", snapshot, active.Length, 0, routes.Count, 0, false,
            LiveVariables: variables, LiveAssignments: routes);
    }

    internal static XElement Entry(FeeContainerLiveVariable variable, string slot, string note) => new("Entry",
        new XAttribute("feeGuid", variable.VariableGuid.ToString("D")), new XAttribute("interfaceGuid", variable.InterfaceGuid),
        new XAttribute("interfaceName", variable.InterfaceName), new XElement("ID", variable.Comment),
        new XElement("Address", string.IsNullOrWhiteSpace(variable.Address) ? variable.Path : variable.Address),
        new XElement("DataType", variable.DataType), new XElement("Signal", variable.Signal), new XElement("Slot", slot), new XElement("Note", note));
}
