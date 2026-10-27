using System.Xml.Linq;

namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>Reconciles stored signal containers with current directed helper ownership.</summary>
internal static class FeeSignalContainerTopology
{
    internal static void Apply(XDocument document, IReadOnlyList<FeeContainerLiveVariable> variables,
        IReadOnlyList<FeeContainerLiveAssignment> routes, IReadOnlyDictionary<Guid, FeeSignalEndpoint> endpoints,
        IReadOnlyList<VisualFeeObjectLink> links, CancellationToken token)
    {
        var list = document.Root?.Element("ContainerList") ?? throw new InvalidOperationException("ContainerList fehlt.");
        var graph = new FeeSignalOwnershipGraph(endpoints, links);
        foreach (var container in list.Elements("Container").ToArray())
        {
            token.ThrowIfCancellationRequested();
            if (container.Attribute("recognitionSource")?.Value == "Manuell" ||
                !Guid.TryParse(container.Attribute("targetGuid")?.Value, out var target)) continue;
            var owners = graph.Owners(target).DistinctBy(item => item.Guid).ToArray();
            if (owners.Length != 1 || !endpoints.TryGetValue(owners[0].Guid, out var actual)) continue;
            container.SetAttributeValue("targetGuid", actual.Guid.ToString("D"));
            if (actual.Name.Length > 0) container.SetElementValue("Component", actual.Name);
            var (type, source) = FeeSignalContainerBuilder.ResolveType(actual, container.Element("Type")?.Value);
            container.SetElementValue("Type", type); container.SetAttributeValue("recognitionSource", source);
            if (target != actual.Guid && owners[0].Slot.Length > 0)
                foreach (var entry in container.Element("DataList")?.Elements("Entry") ?? [])
                    entry.SetElementValue("Slot", FeeContainerLiveReconstructor.MapRuntimeSlot(type, owners[0].Slot) ?? owners[0].Slot);
        }
        foreach (var group in list.Elements("Container").Where(item => item.Attribute("targetGuid") is not null &&
            item.Attribute("recognitionSource")?.Value != "Manuell").GroupBy(item => item.Attribute("targetGuid")!.Value, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            var target = group.First();
            foreach (var duplicate in group.Skip(1).ToArray())
            {
                var data = target.Element("DataList")!;
                foreach (var entry in duplicate.Element("DataList")?.Elements("Entry") ?? [])
                    if (!data.Elements("Entry").Any(old => old.Attribute("feeGuid")?.Value == entry.Attribute("feeGuid")?.Value &&
                        old.Element("Slot")?.Value == entry.Element("Slot")?.Value)) data.Add(new XElement(entry));
                var objects = duplicate.Element("SimObjects");
                if (objects is not null)
                {
                    if (target.Element("SimObjects") is null) target.Add(new XElement("SimObjects"));
                    target.Element("SimObjects")!.Add(objects.Elements().Select(item => new XElement(item)));
                }
                duplicate.Remove();
            }
        }
        var present = list.Descendants("Entry").Select(item => Guid.TryParse(item.Attribute("feeGuid")?.Value, out var guid) ? guid : Guid.Empty).ToHashSet();
        var byGuid = variables.DistinctBy(item => item.VariableGuid).ToDictionary(item => item.VariableGuid);
        foreach (var route in routes.Where(item => !present.Contains(item.VariableGuid) && byGuid.ContainsKey(item.VariableGuid)))
            foreach (var owner in graph.Owners(route.TargetObjectGuid))
            {
                token.ThrowIfCancellationRequested();
                var target = list.Elements("Container").FirstOrDefault(item => item.Attribute("targetGuid")?.Value == owner.Guid.ToString("D"));
                var endpoint = endpoints.GetValueOrDefault(owner.Guid);
                var (type, source) = FeeSignalContainerBuilder.ResolveType(endpoint, null);
                if (target is null)
                {
                    target = new XElement("Container", new XAttribute("id", $"signal-target:{owner.Guid:D}"), new XAttribute("targetGuid", owner.Guid.ToString("D")),
                        new XAttribute("recognitionSource", source), new XAttribute("origin", "Aktive Signalverbindung im aktuellen FEE-Stand ergänzt"),
                        new XElement("Component", string.IsNullOrWhiteSpace(endpoint?.Name) ? byGuid[route.VariableGuid].Signal : endpoint.Name),
                        new XElement("Type", type), new XElement("DataList")); list.Add(target);
                }
                var slot = owner.Slot.Length > 0 ? owner.Slot : route.TargetSlot;
                if (!target.Element("DataList")!.Elements("Entry").Any(item => item.Attribute("feeGuid")?.Value == route.VariableGuid.ToString("D") && item.Element("Slot")?.Value == slot))
                    target.Element("DataList")!.Add(FeeSignalContainerBuilder.Entry(byGuid[route.VariableGuid], slot, "Aktuell verknüpftes Signal automatisch ergänzt."));
            }
    }
}
