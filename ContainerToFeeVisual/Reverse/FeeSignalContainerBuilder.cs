using System.Xml.Linq;
using VIBN_Tools.ContainerGeneration.BusinessLogic;
using VIBN_Tools.ContainerGeneration.BusinessLogic.ContainerData;
using VIBN_Tools.ContainerGeneration.Models;

namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>Uses the ContainerGeneration matcher; GUIDs and interface scope remain authoritative.</summary>
public static class FeeSignalContainerBuilder
{
    public static Fee2ContainerRoot Build(IReadOnlyList<FeeContainerLiveVariable> variables,
        IReadOnlyList<FeeContainerLiveAssignment> routes, XDocument? requirements, CancellationToken token = default)
    {
        var available = variables.Select(item => item.VariableGuid).ToHashSet();
        routes = routes.Where(item => available.Contains(item.VariableGuid) && item.TargetObjectGuid != Guid.Empty &&
            !string.IsNullOrWhiteSpace(item.TargetSlot)).Distinct().ToArray();
        var connected = routes.Select(item => item.VariableGuid).ToHashSet();
        var active = variables.Where(item => connected.Contains(item.VariableGuid)).DistinctBy(item => item.VariableGuid).ToArray();
        var byGuid = active.ToDictionary(item => item.VariableGuid);
        var containers = new List<XElement>();
        foreach (var iface in active.GroupBy(item => item.InterfaceGuid))
        {
            token.ThrowIfCancellationRequested();
            var used = new HashSet<Guid>();
            var excluded = new HashSet<Guid>();
            if (requirements is not null)
            {
                var entries = iface.Select(variable => new ContainerEntry { FeeGuid = variable.VariableGuid.ToString("D"),
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
                        new XAttribute("origin", "Signal und Req. XML"), new XElement("Component", generated.Component),
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
                var xml = new XElement("Container", new XAttribute("id", $"signal-target:{iface.Key}:{target.Key:D}"),
                    new XAttribute("targetGuid", target.Key.ToString("D")), new XAttribute("origin", "Aktives Signal und aktuelle FEE-Slotverbindung; Typ noch offen"),
                    new XElement("Component", byGuid[target.First().VariableGuid].Signal), new XElement("Type", "N/A"),
                    new XElement("DataList", target.Distinct().Select(route => Entry(byGuid[route.VariableGuid], route.TargetSlot,
                        "Über aktuelle Signalverbindung erkannt. FEE-Root auswählen und Objekte separat zuordnen."))));
                containers.Add(xml);
            }
        }
        var document = ContainerFileXml.Document(containers);
        var bindings = containers.SelectMany((container, ci) => container.Descendants("Entry").Select((entry, ei) =>
            new FeeContainerSignalBinding(ci, ei, Guid.Parse(entry.Attribute("feeGuid")!.Value)))).ToArray();
        var snapshot = new FeeContainerProvenanceSnapshot(new Dictionary<string, string>(), document, bindings, containers.Count, bindings.Length, "");
        return new(Guid.Empty, "Signal-Container aus ausgewählten Interfaces", snapshot, active.Length, 0, routes.Count, 0, false,
            LiveVariables: variables, LiveAssignments: routes);
    }

    private static XElement Entry(FeeContainerLiveVariable variable, string slot, string note) => new("Entry",
        new XAttribute("feeGuid", variable.VariableGuid.ToString("D")), new XAttribute("interfaceGuid", variable.InterfaceGuid),
        new XAttribute("interfaceName", variable.InterfaceName), new XElement("ID", variable.Comment),
        new XElement("Address", string.IsNullOrWhiteSpace(variable.Address) ? variable.Path : variable.Address),
        new XElement("DataType", variable.DataType), new XElement("Signal", variable.Signal), new XElement("Slot", slot), new XElement("Note", note));
}
