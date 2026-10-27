using System.Xml.Linq;
using VIBN_Tools.ContainerGeneration.Models;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFeeVisual;

public sealed partial class FeeSignalWorkspaceService
{
    private static async Task<FeeSignalEndpointSnapshot> ReadEndpointGraphAsync(Dictionary<Guid, FeeSignalEndpoint> objects,
        List<string> issues, IReadOnlySet<Guid> variables, long revision, CancellationToken token, IProgress<Fee2ContainerProgress>? progress)
    {
        if (objects.Count == 0) return new(objects, issues, []);
        var links = new List<VisualFeeObjectLink>();
        var read = new HashSet<Guid>();
        var cachedObjects = (Services.FeeObjects?.AllFeeObjects ?? []).DistinctBy(item => item.Guid).ToDictionary(item => item.Guid);
        var definitions = new Dictionary<Guid, string>();
        try
        {
            foreach (var definition in await Services.ApiInstance.Logic.GetAllAvailableLogicDefinitionsAsync() ?? [])
                if (Guid.TryParse(definition.Guid, out var guid)) definitions[guid] = definition.Name ?? "";
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        { issues.Add($"Logikdefinitionen: {Fee2ContainerReadPhase.Describe(exception)}"); }
        while (objects.Keys.Any(guid => !read.Contains(guid)))
        {
            token.ThrowIfCancellationRequested(); CheckRevision(revision);
            var batch = objects.Keys.Where(guid => !read.Contains(guid)).Take(128).ToArray();
            string[] xml;
            try { xml = (await Services.ApiInstance.Object.GetSceneObjectsAsXmlAsync(batch.Select(guid => guid.ToString("D")).ToArray()) ?? []).ToArray(); }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                issues.Add($"Signalendpunkt-XML: {Fee2ContainerReadPhase.Describe(exception)}");
                read.UnionWith(batch); continue;
            }
            var xmlByGuid = new Dictionary<Guid, XElement>();
            foreach (var text in xml)
                try
                {
                    var value = XElement.Parse(text);
                    if (Guid.TryParse(value.Attribute("Guid")?.Value, out var objectGuid)) xmlByGuid[objectGuid] = value;
                    else issues.Add("FEE lieferte einen Signalendpunkt ohne gültige Objekt-GUID.");
                }
                catch (Exception exception) { issues.Add($"Signalendpunkt-XML konnte nicht gelesen werden: {exception.Message}"); }
            for (var index = 0; index < batch.Length; index++)
            {
                token.ThrowIfCancellationRequested(); CheckRevision(revision);
                var guid = batch[index]; read.Add(guid);
                try
                {
                    if (!xmlByGuid.TryGetValue(guid, out var element)) throw new InvalidOperationException("FEE lieferte keinen XML-Datensatz mit der angeforderten Objekt-GUID.");
                    var actual = objects[guid];
                    var definitionText = element.Descendants().FirstOrDefault(item => item.Name.LocalName is "PersistedLogicGuid" or "LogicDefinitionGuid")?.Value;
                    var type = (element.Attribute("Type")?.Value ?? (actual.FeeType.Length > 0 ? actual.FeeType : element.Name.LocalName)).Split('.').Last();
                    if (type == "LogicObject" && string.IsNullOrWhiteSpace(definitionText))
                        definitionText = await Fee2ContainerService.ReadOptionalObjectPropertyAsync(guid, "PersistedLogicGuid");
                    var definition = Guid.TryParse(definitionText, out var definitionGuid) ? definitions.GetValueOrDefault(definitionGuid) ?? "" : "";
                    if (type == "LogicObject" && definition.Length == 0)
                        issues.Add($"Logikdefinition von '{element.Attribute("Name")?.Value ?? guid.ToString("D")}' ist nicht auflösbar (Definitions-GUID: {definitionText ?? "fehlt"}).");
                    var slots = element.Descendants().Where(item => item.Name.LocalName == "SlotName").Select(item => item.Value)
                        .Where(name => !string.IsNullOrWhiteSpace(name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (type == "LogicObject" || ContainerFileXml.IsSimpleLogicType(type))
                    {
                        try { slots.UnionWith((await Services.ApiInstance.Object.GetSlotNamesAsync(guid) ?? []).Where(name => !string.IsNullOrWhiteSpace(name))); }
                        catch (Exception exception) when (!token.IsCancellationRequested)
                        { issues.Add($"Slots von '{actual.Name}': {Fee2ContainerReadPhase.Describe(exception)}"); }
                    }
                    var parent = cachedObjects.GetValueOrDefault(guid)?.Parent;
                    if (type == "CabinetElement")
                    {
                        var parents = new HashSet<Guid>();
                        while (parent is not null && parent is not FeeCabinet && parents.Add(parent.Guid)) parent = parent.Parent;
                    }
                    if (type == "CabinetElement" && parent is FeeCabinet)
                        objects.TryAdd(parent.Guid, new(parent.Guid, parent.Name ?? "", "Cabinet"));
                    var cabinet = element.Descendants().FirstOrDefault(item => item.Name.LocalName is "Definition" or "ElementType")?.Value ?? "";
                    if (type == "CabinetElement" && string.IsNullOrWhiteSpace(cabinet))
                        cabinet = await Fee2ContainerService.ReadOptionalObjectPropertyAsync(guid, "Definition") ??
                            await Fee2ContainerService.ReadOptionalObjectPropertyAsync(guid, "ElementType") ?? "";
                    objects[guid] = actual with { Name = element.Attribute("Name")?.Value ?? actual.Name, FeeType = type,
                        LogicDefinitionName = definition, SlotNames = slots.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
                        ParentGuid = parent?.GuidString ?? "", ParentName = parent?.Name ?? "", CabinetDefinition = cabinet };
                    if (!ContainerFileXml.IsSimpleLogicType(type)) continue;
                    // Inputs must be queried even when the selected interface
                    // signal is attached to an output of the helper.
                    foreach (var slot in FeeSignalOwnershipGraph.HelperSlotNames(type, slots))
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            foreach (var peer in await Services.ApiInstance.Interface.GetSlotSlotAssignmentAsync(guid, slot) ?? [])
                            {
                                if (!Guid.TryParse(peer.SceneObjectGuid, out var other) || other == Guid.Empty || other == guid || variables.Contains(other)) continue;
                                foreach (var remoteSlot in (peer.SlotNames ?? []).DefaultIfEmpty(""))
                                    links.Add(new(guid.ToString("D"), slot, other.ToString("D"), remoteSlot ?? ""));
                                objects.TryAdd(other, new(other, "", ""));
                            }
                        }
                        catch (Exception exception) when (!token.IsCancellationRequested)
                        { issues.Add($"Verknüpfung '{objects[guid].Name}/{slot}': {Fee2ContainerReadPhase.Describe(exception)}"); }
                    }
                }
                catch (Exception exception) when (!token.IsCancellationRequested)
                { issues.Add($"Signalendpunkt {guid:D}: {Fee2ContainerReadPhase.Describe(exception)}"); }
                finally { progress?.Report(new(98, $"Aktuelle Logiken und Hilfslogik-Ein-/Ausgänge: {read.Count}/{objects.Count} Endpunkte gelesen …")); }
            }
        }
        token.ThrowIfCancellationRequested(); CheckRevision(revision);
        return new(objects, issues, links.Distinct().ToArray());
    }
}
