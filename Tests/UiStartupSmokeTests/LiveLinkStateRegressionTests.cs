using System.Reflection;
using VIBN_Tools.Application.VM;
using VIBN_Tools.ContainerGeneration.Models;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.UiStartup.SmokeTests;

internal static partial class Program
{
    private static void VerifyLiveLinkStateRegressions()
    {
        VerifyXmlSlotGroupsAndHelperRoutes();
        VerifyReverseLinkDiscovery();
        VerifyScopedLiveColorsAndDiagnostics();
        VerifyManualHelperDiscovery();
        VerifyReassignmentAfterScopeChange();
    }

    private static readonly Type SlotSnapshotType = typeof(VisualPlan).Assembly.GetType(
        "VIBN_Tools.ContainerToFeeVisual.FeeSlotSnapshot")!;

    private static object SlotSnapshot(FeeAbstractObject[] objects, params Guid[] variables) =>
        Activator.CreateInstance(SlotSnapshotType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [objects, variables], null)!;

    private static IReadOnlyList<VisualFeeObjectLink> SnapshotLinks(object snapshot, FeeAbstractObject[] objects) =>
        (IReadOnlyList<VisualFeeObjectLink>)SlotSnapshotType.GetMethod("ObjectLinks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(snapshot, [objects])!;

    private static IReadOnlyList<VisualFeeSignalLink> ExpandRoutes(IEnumerable<VisualFeeSignalLink> signals,
        FeeAbstractObject[] objects, IEnumerable<VisualFeeObjectLink> links) =>
        (IReadOnlyList<VisualFeeSignalLink>)SlotSnapshotType.GetMethod("ExpandSignalRoutes", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [signals, objects, links])!;

    private static void VerifyXmlSlotGroupsAndHelperRoutes()
    {
        var variable = Guid.NewGuid(); var group = Guid.NewGuid(); var unknownGroup = Guid.NewGuid();
        var sensor = new FeeSensor { Slots = new() { ["Channel1"] = group, ["Unused"] = unknownGroup } };
        var logic = new FeeLogic { Slots = new() { ["IN_PartPresent_Ch1"] = group } };
        var otherConsumer = new FeeLogic { Slots = new() { ["Enable"] = variable } };
        var helper = new FeeSimpleNot { Name = "Manual helper", Slots = new() { ["Input 01"] = variable } };
        FeeAbstractObject[] objects = [sensor, logic, otherConsumer, helper];
        var snapshot = SlotSnapshot(objects, variable);
        var links = SnapshotLinks(snapshot, objects);
        if (links.Count != 2 || !links.Any(link => link.ObjectGuidString == sensor.GuidString &&
                link.LinkedObjectGuidString == logic.GuidString && link.LinkedSlotName == "IN_PartPresent_Ch1") ||
            links.Any(link => link.LinkedObjectGuidString == group.ToString("D") ||
                link.LinkedObjectGuidString == unknownGroup.ToString("D") || link.ObjectGuidString == helper.GuidString))
            throw new InvalidOperationException("A slot-group GUID or shared variable was mistaken for a scene-object connection.");

        var firstConsumer = new FeeSensor { Slots = new() { ["Channel1"] = logic.Guid } };
        var secondConsumer = new FeeSensor { Slots = new() { ["Channel1"] = logic.Guid } };
        FeeAbstractObject[] directScene = [firstConsumer, secondConsumer, logic];
        var unresolved = SnapshotLinks(SlotSnapshot(directScene), [firstConsumer, secondConsumer]);
        if (unresolved.Count != 2 || unresolved.Any(link => link.LinkedObjectGuidString != logic.GuidString || link.LinkedSlotName.Length > 0))
            throw new InvalidOperationException("Direct scene references were confused with slot groups or other consumers.");

        var signal = new VisualFeeSignal(variable.ToString("D"), Guid.NewGuid().ToString("D"), "PLC", "Enable", "%Q0.0", "", "Bool", "Write");
        var direct = (IReadOnlyList<VisualFeeSignalLink>)SlotSnapshotType.GetMethod("SignalLinks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(snapshot, [new[] { signal }])!;
        var secondHelper = new FeeSimpleMove();
        var routingLinks = new[] {
            new VisualFeeObjectLink(helper.GuidString, "Output 01", secondHelper.GuidString, "Input 01"),
            new VisualFeeObjectLink(secondHelper.GuidString, "Output 01", logic.GuidString, "IN_PartPresent_Ch1"),
            new VisualFeeObjectLink(secondHelper.GuidString, "Output 01", helper.GuidString, "Input 01") };
        var routed = ExpandRoutes(direct, [.. objects, secondHelper], routingLinks);
        if (!routed.Any(link => link.ObjectGuidString == logic.GuidString && link.IsIndirect) || routed.Count > 8)
            throw new InvalidOperationException("Existing helper chains were not followed or a routing cycle was not bounded.");
        var disconnected = ExpandRoutes(routed, [.. objects, secondHelper], []);
        if (disconnected.Any(link => link.IsIndirect))
            throw new InvalidOperationException("A removed helper route retained its old verified signal endpoint.");
        foreach (var gate in new FeeAbstractObject[] { new FeeSimpleAnd(), new FeeSimpleOr(), new FeeSimpleMove(), new FeeSimpleNot() })
        {
            var graph = new[] { new VisualFeeObjectLink(gate.GuidString, "Input 01", logic.GuidString, "IN_PartPresent_Ch1") };
            var reversed = ExpandRoutes([new VisualFeeSignalLink(signal.GuidString, gate.GuidString, gate.FeeType, "Output 01", false)],
                [gate, logic], graph);
            if (!reversed.Any(link => link.ObjectGuidString == logic.GuidString && link.IsIndirect))
                throw new InvalidOperationException($"A reverse signal route through {gate.FeeType} was missed.");
        }
    }

    private static void VerifyReverseLinkDiscovery()
    {
        var assembly = typeof(VisualPlan).Assembly;
        var logger = Activator.CreateInstance(assembly.GetType("VIBN_Tools.ContainerToFeeVisual.VisualPlanLogger")!, nonPublic: true)!;
        var discoveryType = assembly.GetType("VIBN_Tools.ContainerToFeeVisual.FeeSimObjectLinkDiscovery")!;
        var discovery = Activator.CreateInstance(discoveryType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [logger], null)!;
        var sensor = new FeeSensor(); var logic = new FeeLogic(); var reads = 0;
        Func<Guid, string, Task<IReadOnlyList<(string SceneObjectGuid, string[] SlotNames)>>> read = (guid, slot) =>
        {
            reads++;
            IReadOnlyList<(string SceneObjectGuid, string[] SlotNames)> result = guid == logic.Guid
                ? [(sensor.GuidString, ["Channel1"])] : [];
            return Task.FromResult(result);
        };
        var required = new Dictionary<Guid, IReadOnlyCollection<string>> {
            [sensor.Guid] = new[] { "Channel1" }, [logic.Guid] = new[] { "IN_PartPresent_Ch1" } };
        var task = (Task)discoveryType.GetMethod("DiscoverAsync")!.Invoke(discovery,
            [new FeeAbstractObject[] { sensor, logic }, CancellationToken.None,
                new FeeAbstractObject[] { sensor, logic }, required, read, Array.Empty<Guid>()])!;
        task.GetAwaiter().GetResult();
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var links = (IReadOnlyList<VisualFeeObjectLink>)result.GetType().GetProperty("Links")!.GetValue(result)!;
        if (reads != 2 || links.Single().ObjectGuidString != logic.GuidString || links.Single().LinkedObjectGuidString != sensor.GuidString)
            throw new InvalidOperationException("The logic-side-only SDK connection was not discovered using actual scene-instance GUIDs.");
    }

    private static void VerifyScopedLiveColorsAndDiagnostics()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vibn-live-colors-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var element = CurrentContainer("Station", "Presence", "%I0.0");
            element.Element("Type")!.Value = "Sensor"; element.Descendants("Slot").Single().Value = "PLC_IN_PartPresent_Ch1";
            var path = Path.Combine(directory, "Live.xml"); ContainerFileXml.Document([element]).Save(path);
            var service = new ContainerToFeeVisualPlanService(); var plan = service.LoadXmlAsync(path).GetAwaiter().GetResult().Plan!;
            var root = new FeeBasicFrame { Name = "Root" };
            var sensor = new FeeSensor { Name = "Station", Parent = root };
            var logic = new FeeLogic { Name = "Station", LogicDefinitionName = "Grob_Sensor", Parent = root };
            var otherLogic = new FeeLogic { Name = "Station", LogicDefinitionName = "Grob_Sensor", Parent = root };
            var spare = new FeeSensor { Name = "Spare", Parent = root };
            FeeAbstractObject[] runtime = [sensor, logic, otherLogic, spare];
            var objects = runtime.Select(item => SnapshotObject(item, root)).ToArray();
            var feeInterface = new VisualFeeInterface(Guid.NewGuid().ToString("D"), "PLC", "Test", 1);
            var signal = new VisualFeeSignal(Guid.NewGuid().ToString("D"), feeInterface.GuidString, "PLC", "Presence", "%I0.0", "", "Bool", "Write");
            SetPrivateField(service, "_feeObjects", objects);
            SetPrivateField(service, "_runtimeObjects", objects.Zip(runtime).ToDictionary(pair => pair.First.Id, pair => pair.Second));
            SetPrivateField(service, "_runtimeSceneObjects", new FeeAbstractObject[] { root, .. runtime });
            SetPrivateField(service, "_feeInterfaces", new[] { feeInterface });
            SetPrivateField(service, "_feeSignals", new[] { signal });
            foreach (var flag in new[] { "_hasDiscoveredFeeObjects", "_hasDiscoveredFeeInterfaces", "_hasDiscoveredFeeSimObjectLinks", "_hasDiscoveredFeeSignalLinks" })
                SetPrivateField(service, flag, true);
            service.SetSimObjectRoots([root.GuidString]); service.SetExistingInterfaces([feeInterface]);
            var target = plan.Targets.Single(); var signalNode = plan.Nodes.Single(node => node.Kind == VisualNodeKind.Signal);
            if (!service.TryAssign(target.Id, objects[0].Id).Success || !service.TryAssignSignal(signalNode.Id, signal.GuidString).Success)
                throw new InvalidOperationException("The live-color fixture could not assign its existing objects.");

            SetPrivateField(service, "_feeSimObjectLinks", new[] { new VisualFeeObjectLink(logic.GuidString, "WrongSlot", sensor.GuidString, "Channel1") });
            var missing = new ContainerToFeeVisualPageVM(service);
            var unlinkedRow = missing.AvailableFeeObjects.Single(item => item.Id == objects[0].Id);
            if (!unlinkedRow.HasLiveConnections || unlinkedRow.IsValid || unlinkedRow.StateBackground != "#FFE8D9F3" ||
                missing.AvailableFeeSignals.Single().StateBackground != "#FFE8D9F3" ||
                missing.TreeRoots.SelectMany(node => node.SelfAndDescendants()).Single(node => node.Kind == VisualNodeKind.SimObject).StateBackground != "#FFE8D9F3")
                throw new InvalidOperationException("A plan assignment without actual FEE links was colored green.");

            // This scene has no provenance: only the existing reverse link identifies the correct same-name logic.
            SetPrivateField(service, "_feeSimObjectLinks", new[] { new VisualFeeObjectLink(logic.GuidString, "SIM_Sensor", sensor.GuidString, "Channel1") });
            SetPrivateField(service, "_feeSignalLinks", new[] { new VisualFeeSignalLink(signal.GuidString, logic.GuidString, "LogicObject", "PLC_IN_PartPresent_Ch1", false) });
            var container = plan.Nodes.Single(node => node.Kind == VisualNodeKind.Container);
            if (service.FindExistingContainerLogics(container.Id).Single().GuidString != logic.GuidString)
                throw new InvalidOperationException("The actual link did not resolve same-name logics in the same root.");
            var linked = new ContainerToFeeVisualPageVM(service);
            linked.SelectedTreeNode = linked.TreeRoots.SelectMany(node => node.SelfAndDescendants()).Single(node => node.Kind == VisualNodeKind.Container);
            if (linked.AvailableFeeObjects.Single(item => item.Id == objects[0].Id).StateBackground != "#FFC6EFCE" ||
                linked.AvailableFeeObjects.Single(item => item.Id == objects[1].Id).StateBackground != "#FFC6EFCE" ||
                linked.AvailableFeeSignals.Single().StateBackground != "#FFC6EFCE" ||
                linked.TreeRoots.SelectMany(node => node.SelfAndDescendants()).Where(node => node.Kind is VisualNodeKind.SimObject or VisualNodeKind.Logic or VisualNodeKind.Signal)
                    .Any(node => node.StateBackground != "#FFC6EFCE"))
                throw new InvalidOperationException("Verified manual links were not consistently green in all views.");

            service.SetSimObjectRoots([]);
            if (service.GetSimObjectConnectionState(target.Id).Kind != VisualSimObjectConnectionKind.NotFound)
                throw new InvalidOperationException("An object from a deselected root remained found.");
            var excluded = new ContainerToFeeVisualPageVM(service);
            excluded.SelectedTreeNode = excluded.TreeRoots.SelectMany(node => node.SelfAndDescendants()).Single(node => node.Kind == VisualNodeKind.Container);
            if (excluded.Targets.Single().StateBackground != "#FFFFF2CC" ||
                excluded.TreeRoots.SelectMany(node => node.SelfAndDescendants()).Where(node => node.Kind is VisualNodeKind.SimObject or VisualNodeKind.SimObjectTarget or VisualNodeKind.Logic)
                    .Any(node => node.StateBackground != "#FFFFF2CC"))
                throw new InvalidOperationException("Objects outside selected roots retained their green/purple tree states.");
            service.SetSimObjectRoots([root.GuidString]);

            var vm = new ContainerToFeeVisualPageVM(service) { SynchronizeRelatedSelections = true };
            vm.SelectedIssue = new VisualIssue(VisualIssueSeverity.Error, "LIVE_OBJECT", "Object issue", sensor.Guid.ToString("B").ToUpperInvariant());
            if (vm.SelectedTreeNode?.FeeObjectId != objects[0].Id || vm.SelectedFeeObject?.Id != objects[0].Id ||
                vm.AvailableFeeObjects.Count(item => item.IsSynchronizationMatch) != 1 || vm.AvailableFeeSignals.Any(item => item.IsSynchronizationMatch))
                throw new InvalidOperationException("A raw-GUID diagnostic selected the container or unrelated objects.");
            vm.SelectedIssue = new VisualIssue(VisualIssueSeverity.Warning, "LIVE_SIGNAL", "Signal issue", "fee:" + signal.GuidString);
            if (vm.SelectedTreeNode?.Id != signalNode.Id || vm.SelectedFeeSignal?.GuidString != signal.GuidString ||
                vm.AvailableFeeObjects.Any(item => item.IsSynchronizationMatch))
                throw new InvalidOperationException("A signal-GUID diagnostic did not synchronize the exact signal.");
            vm.SelectedIssue = new VisualIssue(VisualIssueSeverity.Warning, "MULTIPLE_OBJECTS", "Two objects", target.Id, [objects[0].Id, objects[3].Id]);
            if (vm.AvailableFeeObjects.Count(item => item.IsSynchronizationMatch) != 2 || vm.SelectedTreeNode?.FeeObjectId != objects[0].Id)
                throw new InvalidOperationException("A multi-object diagnostic lost one affected object.");
            vm.SelectedIssue = new VisualIssue(VisualIssueSeverity.Warning, "UNASSIGNED_OBJECT", "Spare object", spare.GuidString);
            if (vm.SelectedFeeObject?.Id != objects[3].Id || vm.AvailableFeeObjects.Count(item => item.IsSynchronizationMatch) != 1)
                throw new InvalidOperationException("A diagnostic without a plan assignment could not select its FEE object.");
            var previous = vm.SelectedTreeNode; vm.SynchronizeRelatedSelections = false;
            vm.SelectedIssue = new VisualIssue(VisualIssueSeverity.Error, "SYNC_DISABLED", "Keep selection", signal.GuidString);
            if (!ReferenceEquals(vm.SelectedTreeNode, previous)) throw new InvalidOperationException("A diagnostic synchronized while synchronization was disabled.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void VerifyManualHelperDiscovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vibn-manual-helper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var element = CurrentContainer("Return", "Enable", "%Q0.0");
            element.Element("Type")!.Value = "ReturnCircuit"; element.Descendants("Slot").Single().Value = "PLC_OUT_Signal";
            var path = Path.Combine(directory, "Return.xml"); ContainerFileXml.Document([element]).Save(path);
            var service = new ContainerToFeeVisualPlanService(); var plan = service.LoadXmlAsync(path).GetAwaiter().GetResult().Plan!;
            var root = new FeeBasicFrame { Name = "Root" };
            var helper = new FeeSimpleNot { Name = "Manually named helper", Parent = root };
            var feeInterface = new VisualFeeInterface(Guid.NewGuid().ToString("D"), "PLC", "Test", 1);
            var signal = new VisualFeeSignal(Guid.NewGuid().ToString("D"), feeInterface.GuidString, "PLC", "Enable", "%Q0.0", "", "Bool", "Write");
            SetPrivateField(service, "_runtimeSceneObjects", new FeeAbstractObject[] { root, helper });
            SetPrivateField(service, "_feeContainerObjects", new[] { new VisualFeeContainerObject(helper.GuidString, helper.Name,
                VisualFeeContainerObjectKind.TechnicalHelper, "BoolNot") });
            SetPrivateField(service, "_feeSignals", new[] { signal }); SetPrivateField(service, "_feeInterfaces", new[] { feeInterface });
            SetPrivateField(service, "_hasDiscoveredFeeSignalLinks", true);
            SetPrivateField(service, "_feeSignalLinks", new[] { new VisualFeeSignalLink(signal.GuidString, helper.GuidString, "BoolNot", "Input 01", false) });
            service.SetExistingInterfaces([feeInterface]); service.SetSimObjectRoots([root.GuidString]);
            var node = plan.Nodes.Single(item => item.Kind == VisualNodeKind.Signal);
            if (!service.GetSignalConnectionState(node.Id, signal.GuidString).IsVerified)
                throw new InvalidOperationException("A manually named helper without provenance was not recognized from its actual signal route.");
            var vm = new ContainerToFeeVisualPageVM(service);
            var treeHelper = vm.TreeRoots.SelectMany(item => item.SelfAndDescendants()).Single(item => item.Kind == VisualNodeKind.TechnicalHelper);
            if (treeHelper.FeeObjectId != "fee:" + helper.GuidString || treeHelper.StateBackground != "#FFC6EFCE")
                throw new InvalidOperationException("The manually linked helper was not displayed as verified.");
            vm.SelectedIssue = new VisualIssue(VisualIssueSeverity.Warning, "MANUAL_HELPER", "Helper warning", helper.GuidString);
            if (!ReferenceEquals(vm.SelectedTreeNode, treeHelper)) throw new InvalidOperationException("A helper-GUID diagnostic could not select its tree entry.");
            service.SetSimObjectRoots([]);
            if (service.GetSignalConnectionState(node.Id, signal.GuidString).IsVerified)
                throw new InvalidOperationException("A helper outside the selected roots still certified a signal.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void VerifyReassignmentAfterScopeChange()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vibn-scope-reassignment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var element = CurrentContainer("Station", "Presence", "%I0.0");
            element.Element("Type")!.Value = "Sensor"; element.Descendants("Slot").Single().Value = "PLC_IN_PartPresent_Ch1";
            var path = Path.Combine(directory, "Scope.xml"); ContainerFileXml.Document([element]).Save(path);
            var service = new ContainerToFeeVisualPlanService(); var plan = service.LoadXmlAsync(path).GetAwaiter().GetResult().Plan!;
            var rootA = new FeeBasicFrame { Name = "Root A" }; var rootB = new FeeBasicFrame { Name = "Root B" };
            var sensorA = new FeeSensor { Name = "Station", Parent = rootA }; var sensorB = new FeeSensor { Name = "Station", Parent = rootB };
            var objects = new[] { SnapshotObject(sensorA, rootA), SnapshotObject(sensorB, rootB) };
            var interfaceA = new VisualFeeInterface(Guid.NewGuid().ToString("D"), "PLC A", "Test", 1);
            var interfaceB = new VisualFeeInterface(Guid.NewGuid().ToString("D"), "PLC B", "Test", 1);
            var signalA = new VisualFeeSignal(Guid.NewGuid().ToString("D"), interfaceA.GuidString, "PLC A", "Presence", "%I0.0", "", "Bool", "Write");
            var signalB = new VisualFeeSignal(Guid.NewGuid().ToString("D"), interfaceB.GuidString, "PLC B", "Presence", "%I0.0", "", "Bool", "Write");
            SetPrivateField(service, "_feeObjects", objects);
            SetPrivateField(service, "_runtimeObjects", new Dictionary<string, FeeAbstractObject> { [objects[0].Id] = sensorA, [objects[1].Id] = sensorB });
            SetPrivateField(service, "_feeSignals", new[] { signalA, signalB });
            SetPrivateField(service, "_feeInterfaces", new[] { interfaceA, interfaceB });
            service.SetSimObjectRoots([rootA.GuidString]); service.SetExistingInterfaces([interfaceA]);
            if (service.AutoAssignMatches() != 2 || plan.Assignments.Single().FeeObjectId != objects[0].Id ||
                plan.SignalAssignments.Single().FeeSignalGuid != signalA.GuidString)
                throw new InvalidOperationException("The initial selected scope did not resolve its objects and signal.");
            service.SetSimObjectRoots([rootB.GuidString]); service.SetExistingInterfaces([interfaceB]);
            if (service.AutoAssignMatches() != 2 || plan.Assignments.Single().FeeObjectId != objects[1].Id ||
                plan.SignalAssignments.Single().FeeSignalGuid != signalB.GuidString)
                throw new InvalidOperationException("Old off-scope assignments blocked or duplicated the new root/interface matches.");
            service.SetSimObjectRoots([]); service.SetExistingInterfaces([]);
            if (service.AutoAssignMatches() != 0 || plan.Assignments.Count != 1 || plan.SignalAssignments.Count != 1)
                throw new InvalidOperationException("Deselecting every scope discarded the saved plan.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
