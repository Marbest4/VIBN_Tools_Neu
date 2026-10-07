using VIBN_Tools.ContainerToFee;
using VIBN_Tools.GlobalClasses.FeeObjects;
using System.Xml.Linq;
using static VIBN_Tools.GlobalClasses.Interfaces;

namespace VIBN_Tools.ContainerToFeeVisual;

internal sealed record BoundVisualContainer(ContainerBaseClass RuntimeContainer, VisualNode PlanNode);

internal sealed record RuntimeVisualPlanBindingResult(
    bool Success,
    IReadOnlyList<BoundVisualContainer> Containers,
    IReadOnlyList<FeeInterfaceSignal> UnknownSignals,
    VisualIssue? Issue);

/// <summary>
/// Recreates the legacy runtime containers and applies the visual assignments.
/// Both full generation and link-only execution use this single mapping path.
/// </summary>
internal static class RuntimeVisualPlanBinder
{
    public static RuntimeVisualPlanBindingResult Bind(
        VisualPlan plan,
        IReadOnlyDictionary<string, FeeAbstractObject> runtimeObjects,
        IReadOnlySet<string>? excludedContainerIds = null,
        bool omitInvalidSlotEntries = false,
        bool omitInvalidObjectAssignments = false)
    {
        var effectiveDocument = CreateEffectiveDocument(plan, omitInvalidSlotEntries);
        var (containers, unknownSignals) =
            ContainerToFeeService.ReadInContainerXmlData(effectiveDocument);
        var containerNodes = plan.Nodes
            .Where(node => node.Kind == VisualNodeKind.Container &&
                           ContainerMetadataCatalog.TryGet(node.TypeName, out _))
            .ToArray();
        if (containerNodes.Length != containers.Count)
        {
            return Failure(
                "Der visuelle Plan und der bestehende Container-Parser liefern unterschiedliche " +
                "Containerzahlen. Der Vorgang wurde sicherheitshalber nicht gestartet.",
                "LEGACY_CONTAINER_COUNT_MISMATCH",
                unknownSignals);
        }

        foreach (var runtimeObject in runtimeObjects.Values.OfType<IAssignableSimObject>())
            runtimeObject.AssignedContainer = null!;

        var bound = new List<BoundVisualContainer>(containers.Count);
        for (var index = 0; index < containers.Count; index++)
        {
            var container = containers[index];
            var node = containerNodes[index];
            container.GenerationProvenanceId = node.Id;
            container.GenerationContainerType = node.TypeName;
            bound.Add(new BoundVisualContainer(container, node));

            if (excludedContainerIds?.Contains(node.Id) == true)
                continue;

            if (container is ICreatableContainer creatable)
                creatable.IsCreationRequested = plan.IsCreationRequested(node.Id);

            if (container is not ISimObjectFindOrSelect selectable)
                continue;

            var runtimeTargets = selectable.GetSimObjectTargets().ToArray();
            var visualTargets = plan.Targets
                .Where(target => target.ContainerId == node.Id)
                .ToArray();
            if (runtimeTargets.Length != visualTargets.Length)
            {
                return Failure(
                    $"Die Zielstruktur von Container '{node.Name}' hat sich gegenüber dem Plan geändert.",
                    "LEGACY_TARGET_COUNT_MISMATCH",
                    unknownSignals,
                    node.Id);
            }

            for (var targetIndex = 0; targetIndex < runtimeTargets.Length; targetIndex++)
            {
                var runtimeTarget = runtimeTargets[targetIndex];
                var visualTarget = visualTargets[targetIndex];
                var assignedRuntimeObjects = new List<FeeAbstractObject>();
                foreach (var assignment in plan.Assignments.Where(item => item.TargetId == visualTarget.Id))
                {
                    if (!runtimeObjects.TryGetValue(assignment.FeeObjectId, out var runtimeObject))
                    {
                        if (omitInvalidObjectAssignments)
                            continue;
                        return Failure(
                            $"FEE-Objekt '{assignment.FeeObjectName}' ist nicht mehr vorhanden.",
                            "ASSIGNED_FEE_OBJECT_MISSING",
                            unknownSignals,
                            visualTarget.Id);
                    }
                    if (!runtimeTarget.AllowedType.IsInstanceOfType(runtimeObject))
                    {
                        if (omitInvalidObjectAssignments)
                            continue;
                        return Failure(
                            $"FEE-Objekt '{assignment.FeeObjectName}' ist für " +
                            $"'{visualTarget.DisplayName}' nicht kompatibel.",
                            "ASSIGNED_FEE_OBJECT_INCOMPATIBLE",
                            unknownSignals,
                            visualTarget.Id);
                    }
                    assignedRuntimeObjects.Add(runtimeObject);
                }

                runtimeTarget.AssignObjects(assignedRuntimeObjects);
                foreach (var assignable in assignedRuntimeObjects.OfType<IAssignableSimObject>())
                    assignable.AssignedContainer = selectable;
            }
        }

        return new RuntimeVisualPlanBindingResult(true, bound, unknownSignals, null);
    }

    internal static XDocument CreateEffectiveDocument(
        VisualPlan plan,
        bool omitInvalidSlotEntries = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var document = XDocument.Load(plan.SourceXmlPath, LoadOptions.None);
        var allSourceContainers = document.Descendants("Container").ToArray();
        var allPlanContainers = plan.Nodes.Where(node => node.Kind == VisualNodeKind.Container).ToArray();
        if (allSourceContainers.Length == allPlanContainers.Length)
        {
            for (var index = 0; index < allSourceContainers.Length; index++)
            {
                if (!plan.ContainerTypeOverrides.Any(item => string.Equals(
                        item.ContainerId,
                        allPlanContainers[index].Id,
                        StringComparison.Ordinal)))
                    continue;
                var typeElement = allSourceContainers[index].Element("Type");
                if (typeElement is null)
                    allSourceContainers[index].AddFirst(new XElement("Type", allPlanContainers[index].TypeName));
                else
                    typeElement.Value = allPlanContainers[index].TypeName;
            }
        }
        var supportedContainers = document.Descendants("Container")
            .Where(element => ContainerMetadataCatalog.TryGet(
                element.Element("Type")?.Value ?? string.Empty,
                out _))
            .ToArray();
        var containerNodes = plan.Nodes
            .Where(node => node.Kind == VisualNodeKind.Container &&
                           ContainerMetadataCatalog.TryGet(node.TypeName, out _))
            .ToArray();
        if (supportedContainers.Length != containerNodes.Length)
            return document;

        for (var containerIndex = 0; containerIndex < supportedContainers.Length; containerIndex++)
        {
            var entries = supportedContainers[containerIndex].Descendants("Entry").ToArray();
            var sourceSignalNodes = plan.Nodes
                .Where(node => string.Equals(
                                   node.ContainerId,
                                   containerNodes[containerIndex].Id,
                                   StringComparison.Ordinal) &&
                               node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal &&
                               !plan.IsAddedSignal(node.Id))
                .ToArray();
            if (entries.Length != sourceSignalNodes.Length)
                continue;
            for (var entryIndex = 0; entryIndex < entries.Length; entryIndex++)
            {
                var slotElement = entries[entryIndex].Element("Slot");
                if (slotElement is not null)
                    slotElement.Value = plan.GetEffectiveSlot(sourceSignalNodes[entryIndex]);
                var assignment = plan.SignalAssignments.LastOrDefault(item => item.SignalNodeId == sourceSignalNodes[entryIndex].Id);
                if (assignment is not null) entries[entryIndex].SetAttributeValue("feeGuid", assignment.FeeSignalGuid);
            }
            for (var entryIndex = entries.Length - 1; entryIndex >= 0; entryIndex--)
            {
                if (plan.IsSignalRemoved(sourceSignalNodes[entryIndex].Id))
                    entries[entryIndex].Remove();
            }

            var dataList = supportedContainers[containerIndex].Descendants("DataList").FirstOrDefault();
            if (dataList is null)
            {
                dataList = new XElement("DataList");
                supportedContainers[containerIndex].Add(dataList);
            }
            foreach (var added in plan.AddedSignals.Where(item => string.Equals(
                         item.ContainerId,
                         containerNodes[containerIndex].Id,
                         StringComparison.Ordinal)))
            {
                var addedNode = plan.FindNode(added.NodeId);
                if (addedNode is null)
                    continue;
                dataList.Add(new XElement("Entry",
                    new XAttribute("feeGuid", added.FeeSignalGuid),
                    new XElement("ID", "Manuell in Container2FEE Visual ergänzt"),
                    new XElement("Address", string.IsNullOrWhiteSpace(added.Path) ? added.Address : added.Path),
                    new XElement("DataType", added.DataType),
                    new XElement("Signal", added.FeeSignalTag),
                    new XElement("Slot", plan.GetEffectiveSlot(addedNode)),
                    new XElement("Note", $"Bestehendes Signal aus Interface '{added.FeeInterfaceName}'")));
            }

            if (omitInvalidSlotEntries &&
                ContainerMetadataCatalog.TryGet(containerNodes[containerIndex].TypeName, out var descriptor))
            {
                var validSlots = descriptor.Slots.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var invalidEntry in supportedContainers[containerIndex]
                             .Descendants("Entry")
                             .Where(entry => !validSlots.Contains(
                                 entry.Element("Slot")?.Value?.Trim() ?? string.Empty))
                             .ToArray())
                {
                    invalidEntry.Remove();
                }
            }
        }

        return document;
    }

    internal static void AddRuntimeObjectIdentities(XDocument document, VisualPlan plan,
        IReadOnlyList<BoundVisualContainer> bindings)
    {
        var source = document.Descendants("Container").ToArray();
        var nodes = plan.Nodes.Where(node => node.Kind == VisualNodeKind.Container).ToArray();
        foreach (var binding in bindings)
        {
            var index = Array.FindIndex(nodes, node => node.Id == binding.PlanNode.Id);
            if (index < 0 || index >= source.Length) continue;
            var objects = source[index].Element("SimObjects") ?? new XElement("SimObjects");
            if (objects.Parent is null) source[index].Add(objects);
            foreach (var generated in objects.Elements("SimObject").Where(item =>
                         item.Element("Role")?.Value is "Primary" or "TechnicalHelper").ToArray()) generated.Remove();
            var runtime = binding.RuntimeContainer;
            var logic = ContainerExistingObjectReuse.GetAssignedLogic(runtime);
            var cabinet = ContainerExistingObjectReuse.GetAssignedCabinetElement(runtime);
            var primary = (FeeAbstractObject?)logic ?? cabinet;
            var targets = runtime is ISimObjectFindOrSelect selectable ? selectable.GetSimObjectTargets().ToArray() : [];
            if (primary is null && runtime is ISimObjectOwner)
                primary = targets.SelectMany(target => target.GetObjects()).FirstOrDefault() ??
                    runtime.GetType().GetProperties().Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
                        .Select(property => property.GetValue(runtime)).OfType<FeeAbstractObject>().FirstOrDefault();
            var identities = new Dictionary<Guid, XElement>();
            void Add(FeeAbstractObject item, string role, string target = "")
            {
                if (item.Guid == Guid.Empty) return;
                identities[item.Guid] = VIBN_Tools.ContainerGeneration.Models.ContainerFileXml.Object(item.GuidString,
                    item.Name ?? binding.PlanNode.Name, item.FeeType ?? "", role, target,
                    item.GetType().FullName ?? item.GetType().Name);
            }
            foreach (var target in targets)
                foreach (var item in target.GetObjects()) Add(item, ReferenceEquals(primary, item) ? "Primary" : "SimObject", target.DisplayName);
            if (primary is not null) Add(primary, "Primary", identities.GetValueOrDefault(primary.Guid)?.Element("Target")?.Value ?? "");
            foreach (var property in runtime.GetType().GetProperties().Where(property => property.CanRead && property.GetIndexParameters().Length == 0))
                if (property.GetValue(runtime) is FeeAbstractObject item && item is FeeSimpleNot or FeeSimpleMove or FeeSimpleAnd or FeeSimpleOr &&
                    !identities.ContainsKey(item.Guid)) Add(item, "TechnicalHelper");
            foreach (var item in objects.Elements("SimObject").Where(item =>
                         Guid.TryParse(item.Element("Guid")?.Value, out var guid) && identities.ContainsKey(guid)).ToArray()) item.Remove();
            objects.Add(identities.Values);
        }
    }

    private static RuntimeVisualPlanBindingResult Failure(
        string message,
        string code,
        IReadOnlyList<FeeInterfaceSignal> unknownSignals,
        string? nodeId = null) =>
        new(false, [], unknownSignals, new VisualIssue(VisualIssueSeverity.Error, code, message, nodeId));
}
