using System.Xml.Linq;
using VIBN_Tools.ContainerGeneration.Models;
using VIBN_Tools.ContainerGeneration.BusinessLogic.ContainerData;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFeeVisual;

public sealed partial class ContainerToFeeVisualPlanService
{
    internal IReadOnlyDictionary<string, FeeAbstractObject> ComparisonRuntimeObjects => _runtimeObjects;
    internal IReadOnlyList<FeeInterface> ComparisonInterfaces => _runtimeInterfaces.Values.ToArray();
    public XDocument CreateEffectiveContainerDocument()
    {
        var plan = CurrentPlan ?? throw new InvalidOperationException("Es ist kein visueller Plan geladen.");
        var document = RuntimeVisualPlanBinder.CreateEffectiveDocument(plan);
        var containers = document.Descendants("Container").ToArray();
        var nodes = plan.Nodes.Where(node => node.Kind == VisualNodeKind.Container).ToArray();
        if (containers.Length != nodes.Length) throw new InvalidOperationException("Containerstruktur hat sich geändert.");
        for (var index = 0; index < containers.Length; index++)
        {
            var node = nodes[index];
            var existing = containers[index].Element("SimObjects");
            var objects = new XElement("SimObjects");
            // Preserve explicit associations without a generator target, as well
            // as generated primary/helper identities in offline files.
            foreach (var item in existing?.Elements("SimObject") ?? [])
                if (item.Element("Role")?.Value is "Primary" or "TechnicalHelper" ||
                    string.IsNullOrWhiteSpace(item.Element("Target")?.Value) &&
                    !plan.Assignments.Any(assignment => assignment.FeeObjectId ==
                        FeeSimObjectDiscovery.CreateFeeObjectId(item.Element("Guid")?.Value ?? "")))
                    objects.Add(new XElement(item));
            foreach (var assignment in plan.Assignments.Where(assignment => plan.FindTarget(assignment.TargetId)?.ContainerId == node.Id))
            {
                var discovered = FindFeeObject(assignment.FeeObjectId);
                var source = existing?.Elements("SimObject").FirstOrDefault(item =>
                    FeeSimObjectDiscovery.CreateFeeObjectId(item.Element("Guid")?.Value ?? "") == assignment.FeeObjectId);
                var guid = discovered?.GuidString ?? source?.Element("Guid")?.Value ?? assignment.FeeObjectId.Replace("fee:", "");
                var feeType = discovered?.FeeType ?? source?.Element("FeeType")?.Value ?? "";
                var target = plan.FindTarget(assignment.TargetId)!;
                objects.Add(ContainerFileXml.Object(guid, assignment.FeeObjectName, feeType, "SimObject",
                    target.DisplayName, assignment.FeeObjectTypeName,
                    _runtimeObjects.GetValueOrDefault(assignment.FeeObjectId)?.Slots?.Select(slot => new ContainerFeeSlot
                    { Name = slot.Key, AssignedGuid = slot.Value.ToString("D") })));
            }
            foreach (var item in _feeContainerObjects.Where(item =>
                         item.ProvenanceContainerId == node.Id ||
                         string.Equals(item.Name, node.Name, StringComparison.OrdinalIgnoreCase)))
            {
                var isHelper = item.Kind == VisualFeeContainerObjectKind.TechnicalHelper;
                var expected = plan.Nodes.Where(child => child.ContainerId == node.Id &&
                    child.Kind == (isHelper ? VisualNodeKind.TechnicalHelper : VisualNodeKind.Logic)).ToArray();
                if (!isHelper && item.Kind == VisualFeeContainerObjectKind.Logic &&
                    !expected.Any(child => string.Equals(child.Name, item.Definition, StringComparison.OrdinalIgnoreCase))) continue;
                if (item.Kind == VisualFeeContainerObjectKind.Cabinet) continue;
                var feeType = item.Kind switch
                {
                    VisualFeeContainerObjectKind.Logic => "LogicObject",
                    VisualFeeContainerObjectKind.CabinetElement => "CabinetElement", _ => item.Definition
                };
                foreach (var old in objects.Elements("SimObject").Where(obj =>
                             string.Equals(obj.Element("Guid")?.Value, item.GuidString, StringComparison.OrdinalIgnoreCase)).ToArray()) old.Remove();
                var helperIsPrimary = isHelper && !plan.Nodes.Any(child => child.ContainerId == node.Id && child.Kind == VisualNodeKind.Logic) &&
                    !plan.Targets.Any(target => target.ContainerId == node.Id);
                objects.Add(ContainerFileXml.Object(item.GuidString, item.Name, feeType,
                    isHelper && !helperIsPrimary ? "TechnicalHelper" : "Primary"));
            }
            existing?.Remove();
            if (objects.HasElements) containers[index].Add(objects);
        }
        if (_hasDiscoveredFeeObjects || _hasDiscoveredFeeInterfaces)
        {
            document.Root?.Element("FeeInventory")?.Remove();
            document.Root?.Add(new XElement("FeeInventory",
                new XElement("SimObjects", _feeObjects.Select(item => ContainerFileXml.Object(
                    item.GuidString, item.Name, item.FeeType, "Available", "", item.TypeName))),
                new XElement("Signals", _feeSignals.Select(item => new XElement("Signal",
                    new XElement("Guid", item.GuidString), new XElement("InterfaceGuid", item.InterfaceGuidString),
                    new XElement("InterfaceName", item.InterfaceName), new XElement("Tag", item.Tag),
                    new XElement("Address", item.Address), new XElement("Path", item.Path),
                    new XElement("DataType", item.DataType), new XElement("Usage", item.Usage))))));
        }
        var projected = FeeContainerProvenanceCodec.Create(document, CreateEffectiveIncludedContainerIds(plan, document),
            plan.SourceFingerprint).ContainerDocument;
        return ContainerFileXml.Document(projected.Descendants("Container"), projected.Root?.Element("FeeInventory"));
    }
}
