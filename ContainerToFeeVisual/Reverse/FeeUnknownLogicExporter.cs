using System.Text;
using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using VIBN_Tools.ContainerGeneration.Models;

namespace VIBN_Tools.ContainerToFeeVisual;

public sealed record FeeUnknownLogicSignal(Guid Guid, string SignalName, string Address, string DataType,
    string InterfaceGuid, string InterfaceName, bool ViaHelper, bool IncludedInContainerFile, string Slot = "");
public sealed record FeeUnknownLogicConnection(Guid Guid, string ObjectName, string FeeType, string Slot);
public sealed record FeeUnknownLogicSlot(string Name, IReadOnlyList<FeeUnknownLogicSignal> Signals, IReadOnlyList<FeeUnknownLogicConnection> ObjectConnections);
public sealed record FeeUnknownLogicObject(Guid Guid, string ObjectName, string FeeType, string LogicDefinitionName,
    IReadOnlyList<FeeUnknownLogicSlot> Slots, string Note);
public sealed record FeeUnknownLogicContainer(string Root, string ContainerId, string ContainerName, string Type,
    IReadOnlyList<FeeUnknownLogicObject> Logics, IReadOnlyList<FeeUnknownLogicSignal> Signals, string Note);
public sealed record FeeUnknownLogicDocument(string Schema, DateTimeOffset ReadoutExportedAt, IReadOnlyList<FeeUnknownLogicContainer> Containers);

/// <summary>Exports the read-only live snapshot needed to define unsupported container logic.</summary>
public static class FeeUnknownLogicExporter
{
    public static FeeUnknownLogicDocument Create(IEnumerable<Fee2ContainerRoot> roots)
    {
        var result = new List<FeeUnknownLogicContainer>();
        foreach (var root in roots)
        {
            var endpoints = (root.SignalEndpoints ?? new Dictionary<Guid, FeeSignalEndpoint>()).ToDictionary(item => item.Key, item => item.Value);
            foreach (var item in root.ObjectDetails ?? []) endpoints[item.Guid] = new(item.Guid, item.Name, item.FeeType,
                item.LogicDefinitionName, item.SlotNames, item.ParentGuid, item.ParentName, item.CabinetDefinition);
            var graph = new FeeSignalOwnershipGraph(endpoints, root.ObjectLinks ?? []);
            var variables = (root.LiveVariables ?? []).DistinctBy(item => item.VariableGuid).ToDictionary(item => item.VariableGuid);
            var routes = (root.LiveAssignments ?? []).ToLookup(item => item.TargetObjectGuid);
            foreach (var container in root.Provenance?.ContainerDocument.Descendants("Container").Where(item =>
                string.Equals(item.Element("Type")?.Value, "N/A", StringComparison.OrdinalIgnoreCase) && item.Attribute("export")?.Value != "false") ?? [])
            {
                var entries = (container.Element("DataList")?.Elements("Entry") ?? []).ToArray();
                var included = entries.Where(item => item.Attribute("export")?.Value != "false").Select(item =>
                    Guid.TryParse(item.Attribute("feeGuid")?.Value, out var guid) ? guid : Guid.Empty).ToHashSet();
                var guids = ContainerFileXml.Objects(container).Select(item => Guid.TryParse(item.Element("Guid")?.Value, out var guid) ? guid : Guid.Empty)
                    .Where(guid => guid != Guid.Empty).ToHashSet();
                if (Guid.TryParse(container.Attribute("targetGuid")?.Value, out var primary))
                { guids.Add(primary); guids.UnionWith(graph.Members(primary)); }
                var objects = new List<FeeUnknownLogicObject>();
                foreach (var guid in guids.OrderBy(item => item))
                {
                    var endpoint = endpoints.GetValueOrDefault(guid);
                    var stored = ContainerFileXml.Objects(container).FirstOrDefault(item => item.Element("Guid")?.Value == guid.ToString("D"));
                    var type = endpoint?.FeeType ?? stored?.Element("FeeType")?.Value ?? "";
                    var shortType = type.Split('.').Last();
                    if (shortType != "LogicObject" && !ContainerFileXml.IsSimpleLogicType(type) && guid != primary && stored?.Element("Role")?.Value != "Primary") continue;
                    var definition = endpoint?.LogicDefinitionName ?? stored?.Element("LogicDefinitionName")?.Value ?? "";
                    if (definition.Length == 0 && ContainerFileXml.IsSimpleLogicType(type)) definition = type;
                    if (definition.Length == 0 && shortType == "CabinetElement") definition = endpoint?.CabinetDefinition ?? "";
                    var links = (root.ObjectLinks ?? []).SelectMany(link => new[]
                    {
                        (Guid: link.ObjectGuidString, Slot: link.SlotName, Other: link.LinkedObjectGuidString, OtherSlot: link.LinkedSlotName),
                        (Guid: link.LinkedObjectGuidString, Slot: link.LinkedSlotName, Other: link.ObjectGuidString, OtherSlot: link.SlotName)
                    }).Where(link => string.Equals(link.Guid, guid.ToString("D"), StringComparison.OrdinalIgnoreCase)).ToArray();
                    var slots = (endpoint?.SlotNames ?? []).Concat(routes[guid].Select(item => item.TargetSlot)).Concat(links.Select(link => link.Slot))
                        .Concat(stored?.Descendants("Slot").Select(item => item.Attribute("name")?.Value ?? item.Element("Name")?.Value ?? "") ?? [])
                        .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(name => name.StartsWith("PLC_IN", StringComparison.OrdinalIgnoreCase) ? 0 : name.StartsWith("PLC_OUT", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                        .ThenBy(name => name, StringComparer.OrdinalIgnoreCase).Select(slot => new FeeUnknownLogicSlot(slot,
                            routes[guid].Where(route => string.Equals(route.TargetSlot, slot, StringComparison.OrdinalIgnoreCase) && variables.ContainsKey(route.VariableGuid))
                                .GroupBy(route => route.VariableGuid).Select(group =>
                                {
                                    var variable = variables[group.Key];
                                    return new FeeUnknownLogicSignal(variable.VariableGuid, variable.Signal,
                                        string.IsNullOrWhiteSpace(variable.Address) ? variable.Path : variable.Address, variable.DataType,
                                        variable.InterfaceGuid, variable.InterfaceName, group.All(route => route.IsIndirect), included.Contains(variable.VariableGuid), slot);
                                }).OrderBy(item => item.SignalName, StringComparer.OrdinalIgnoreCase).ToArray(),
                            links.Where(link => string.Equals(link.Slot, slot, StringComparison.OrdinalIgnoreCase) && Guid.TryParse(link.Other, out _))
                                .Select(link => new FeeUnknownLogicConnection(Guid.Parse(link.Other), endpoints.GetValueOrDefault(Guid.Parse(link.Other))?.Name ?? "",
                                    endpoints.GetValueOrDefault(Guid.Parse(link.Other))?.FeeType ?? "", link.OtherSlot)).Distinct().ToArray())).ToArray();
                    objects.Add(new(guid, endpoint?.Name ?? stored?.Element("Name")?.Value ?? "", type, definition, slots,
                        endpoint is null ? "Endpunktmetadaten fehlen. Interfaces/FEE-Roots erneut einlesen." :
                        definition.Length == 0 || string.Equals(definition, "UNDEFINED", StringComparison.OrdinalIgnoreCase) ?
                            "Keine aufgelöste Logikdefinition vorhanden; Objekt- und Slotdaten bleiben erhalten." : ""));
                }
                var containerSignals = entries.Select(entry =>
                {
                    var guid = Guid.TryParse(entry.Attribute("feeGuid")?.Value, out var parsed) ? parsed : Guid.Empty;
                    var variable = variables.GetValueOrDefault(guid);
                    return new FeeUnknownLogicSignal(guid, variable?.Signal ?? entry.Element("Signal")?.Value ?? "",
                        variable is not null ? string.IsNullOrWhiteSpace(variable.Address) ? variable.Path : variable.Address : entry.Element("Address")?.Value ?? "",
                        variable?.DataType ?? entry.Element("DataType")?.Value ?? "", variable?.InterfaceGuid ?? entry.Attribute("interfaceGuid")?.Value ?? "",
                        variable?.InterfaceName ?? entry.Attribute("interfaceName")?.Value ?? "", false, included.Contains(guid), entry.Element("Slot")?.Value ?? "");
                }).ToArray();
                result.Add(new(root.Name, container.Attribute("id")?.Value ?? "", container.Element("Component")?.Value ?? "", "N/A", objects,
                    containerSignals, objects.Count == 0 ? "Keine aktuelle Logik-/Objektzuordnung vorhanden. Die Signalübersicht bleibt erhalten." : ""));
            }
        }
        return new("VIBN_Tools.FEE2Container.UnknownLogic.v1", DateTimeOffset.UtcNow, result);
    }

    public static async Task SaveAsync(FeeUnknownLogicDocument document, string path, CancellationToken token = default)
    {
        if (document.Containers.Count == 0) throw new InvalidOperationException("Keine für den Export markierten N/A-Container vorhanden.");
        var target = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document, options), new UTF8Encoding(false), token);
            token.ThrowIfCancellationRequested(); File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
