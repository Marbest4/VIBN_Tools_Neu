using System.Reflection;
using System.Xml.Linq;
using VIBN_Tools.Application.VM;
using VIBN_Tools.ContainerGeneration.Models;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.UiStartup.SmokeTests;

internal static partial class Program
{
    private static void VerifyCachedAssignmentAndReverseDetails()
    {
        VerifyLogicAssignmentAndCachedRootSelection();
        VerifyReverseDetailsAndUnassignedSignalOverview();
        foreach (var type in new[] { "Decoration", "SceneObjectCompounder", "CADAssembly" })
            if (!FeeContainerReadPolicy.IsIgnored(type, "Keep name")) throw new InvalidOperationException("Ignored reverse type was accepted.");
        if (!FeeContainerReadPolicy.IsIgnored("MotionJoint", "j9") || FeeContainerReadPolicy.IsIgnored("MotionJoint", "j10") ||
            FeeContainerReadPolicy.IsIgnored("MotionJoint", "Station.j1") || FeeContainerReadPolicy.IsIgnored("Button", "j1"))
            throw new InvalidOperationException("Robot-axis exclusions are too broad or incomplete.");
        var special = FeeContainerLiveReconstructor.Reconstruct(Guid.NewGuid(), "Root",
            [new(Guid.NewGuid(), "Device", "LogicObject", "Grob_Sensor", IsSpecialDevice: true)], [], []);
        if (special.Snapshot.ContainerCount != 1 || special.UnmappedObjects.Count != 0 ||
            special.ObjectAssociations.Single().Role != "SpecialDevice")
            throw new InvalidOperationException("A live SpecialDevice was not retained as its own logic-typed container.");
    }

    private static void VerifyLogicAssignmentAndCachedRootSelection()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vibn-cached-roots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var element = CurrentContainer("Station", "Presence", "%I0.0");
            element.Element("Type")!.Value = "Sensor";
            element.Descendants("Slot").Single().Value = "PLC_IN_PartPresent_Ch1";
            var path = Path.Combine(directory, "Container.xml"); ContainerFileXml.Document([element]).Save(path);
            var service = new ContainerToFeeVisualPlanService(); var plan = service.LoadXmlAsync(path).GetAwaiter().GetResult().Plan!;
            var root = new FeeBasicFrame { Name = "Root" };
            var sensor = new FeeSensor { Name = "Station", Parent = root };
            var logic = new FeeLogic { Name = "Manuell benannte Logik", Parent = root, LogicDefinitionName = "Grob_Sensor",
                LogicDefinitionGuid = Guid.NewGuid(), Slots = new() { ["PLC_IN_PartPresent_Ch1"] = Guid.Empty } };
            FeeAbstractObject[] runtime = [sensor, logic]; var objects = runtime.Select(item => SnapshotObject(item, root)).ToArray();
            var iface = new VisualFeeInterface(Guid.NewGuid().ToString("D"), "PLC", "Test", 1);
            var signal = new VisualFeeSignal(Guid.NewGuid().ToString("D"), iface.GuidString, "PLC", "Presence", "%I0.0", "", "Bool", "Write");
            SetPrivateField(service, "_feeObjects", objects);
            SetPrivateField(service, "_runtimeObjects", objects.Zip(runtime).ToDictionary(pair => pair.First.Id, pair => pair.Second));
            SetPrivateField(service, "_runtimeSceneObjects", new FeeAbstractObject[] { root, .. runtime });
            SetPrivateField(service, "_feeSignals", new[] { signal }); SetPrivateField(service, "_feeInterfaces", new[] { iface });
            SetPrivateField(service, "_topLevelBasicFrames", new Dictionary<Guid, string> { [root.Guid] = root.Name });
            foreach (var flag in new[] { "_hasDiscoveredFeeObjects", "_hasDiscoveredFeeInterfaces", "_hasDiscoveredFeeSimObjectLinks", "_hasDiscoveredFeeSignalLinks" }) SetPrivateField(service, flag, true);
            service.SetExistingInterfaces([iface]); service.SetSimObjectRoots([root.GuidString]);
            var signalLinks = new[] { new VisualFeeSignalLink(signal.GuidString, logic.GuidString, "LogicObject", "PLC_IN_PartPresent_Ch1", false) };
            var objectLinks = new[] { new VisualFeeObjectLink(logic.GuidString, "SIM_Sensor", sensor.GuidString, "Channel1") };
            SetPrivateField(service, "_feeSignalLinks", signalLinks); SetPrivateField(service, "_feeSimObjectLinks", objectLinks);
            var target = plan.Targets.Single(); var signalNode = plan.Nodes.Single(item => item.Kind == VisualNodeKind.Signal);
            if (service.TryAssign(target.Id, objects[0].Id).Success || service.TryAssignSignal(signalNode.Id, signal.GuidString).Success || service.AutoAssignMatches() != 0)
                throw new InvalidOperationException("Objects/signals were assigned before a required logic existed.");
            var vm = new ContainerToFeeVisualPageVM(service);
            if (vm.AvailableFeeObjects.Single(item => item.Id == objects[0].Id).IsValid)
                throw new InvalidOperationException("An object with live slots but no plan assignment was green.");
            var logicNode = plan.Nodes.Single(item => item.Kind == VisualNodeKind.Logic);
            var slots = logic.Slots; logic.Slots = new();
            if (service.TryAssign(logicNode.Id, objects[1].Id).Success) throw new InvalidOperationException("A logic without ports was accepted.");
            logic.Slots = slots;
            if (!service.TryAssign(logicNode.Id, objects[1].Id).Success || service.AutoAssignMatches() != 2 ||
                !service.GetSimObjectConnectionState(target.Id).IsVerified || !service.GetSignalConnectionState(signalNode.Id, signal.GuidString).IsVerified)
                throw new InvalidOperationException("An explicitly assigned scene logic was not used by signal/SimObject verification.");
            vm = new ContainerToFeeVisualPageVM(service);
            typeof(ContainerToFeeVisualPageVM).GetMethod("RefreshFeeRootProjection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
            var selectedRoot = vm.AvailableFeeRoots.Single(); selectedRoot.IsSelected = false;
            if (vm.IsBusy || service.GetSimObjectConnectionState(target.Id).Kind != VisualSimObjectConnectionKind.NotFound)
                throw new InvalidOperationException("Changing a root started an SDK refresh or retained excluded objects.");
            selectedRoot.IsSelected = true;
            if (vm.IsBusy || !service.GetSimObjectConnectionState(target.Id).IsVerified ||
                !ReferenceEquals(signalLinks, service.DiscoveredFeeSignalLinks) || !ReferenceEquals(objectLinks, service.DiscoveredFeeSimObjectLinks))
                throw new InvalidOperationException("Root re-selection discarded or reread the cached link snapshot.");
            var definition = Guid.NewGuid();
            var parsed = new FeeLogic(); parsed.StoreXmlObjectProperties(XElement.Parse($"<Object Guid='{parsed.Guid}' Name='Logic' Type='LogicObject'><LogicComponent><PersistedLogicGuid>{definition}</PersistedLogicGuid></LogicComponent></Object>"), parsed.Guid);
            if (parsed.LogicDefinitionGuid != definition) throw new InvalidOperationException("LogicComponent XML was not recognized.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void VerifyReverseDetailsAndUnassignedSignalOverview()
    {
        var cabinet = new FeeCabinet { Name = "Cabinet A" };
        var element = new FeeCabinetElement { Name = "Switch", Parent = cabinet };
        var helper = new FeeAbstractObject { Guid = Guid.NewGuid(), Name = "Counter", FeeType = "CounterSLO", Parent = cabinet };
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var variables = new[] { new FeeContainerLiveVariable(first, "Assigned", "%I0.0", "", "Bool", ""),
            new FeeContainerLiveVariable(second, "Counter reset", "%Q0.0", "", "Bool", "") };
        var assignments = new[] { new FeeContainerLiveAssignment(first, element.Guid, "Pressed"), new FeeContainerLiveAssignment(second, helper.Guid, "Reset") };
        var detailsMethod = typeof(Fee2ContainerService).GetMethod("CreateObjectDetails", BindingFlags.Static | BindingFlags.NonPublic)!;
        var details = (IReadOnlyDictionary<Guid, FeeContainerObjectDetails>)detailsMethod.Invoke(null,
            [new FeeAbstractObject[] { cabinet, element, helper }, variables, assignments,
                new VisualFeeObjectLink[] { new(element.GuidString, "Pressed", helper.GuidString, "Input 01") }])!;
        if (details[element.Guid].ParentName != "Cabinet A" || !details[element.Guid].ConnectionText.Contains("Counter/Input 01") ||
            !details[helper.Guid].ConnectionText.Contains("Signal Counter reset"))
            throw new InvalidOperationException("Cabinet/SimpleLogic peer names, slots, parents or variables are absent.");
        var doc = ContainerFileXml.Document([CurrentContainer("Switch", "Assigned", "%I0.0")]);
        var snapshot = new FeeContainerProvenanceSnapshot(new Dictionary<string, string>(), doc,
            [new FeeContainerSignalBinding(0, 0, first)], 1, 1, "");
        var id = doc.Descendants("Container").Single().Attribute("id")!.Value;
        var root = new Fee2ContainerRoot(Guid.NewGuid(), "Root", snapshot, 1, 0, 1, 0, false,
            NonContainerObjects: [new(helper.Guid, helper.Name, helper.FeeType, "Prüfen")],
            ObjectAssociations: [new(element.Guid, element.Name, element.FeeType, element.Guid, id, "Test", IsManual: true)],
            ObjectDetails: details.Values.ToArray(), LiveVariables: variables, LiveAssignments: assignments);
        var page = new Fee2ContainerPageVM { SelectedRoot = new Fee2ContainerRootSelectionVM(root) };
        PumpDispatcher(TimeSpan.FromMilliseconds(50));
        if (page.LinkedUnassignedSignalCount != 1 || page.LinkedUnassignedSignals.Single().Signal != "Counter reset" ||
            page.FoundContainers.Single().DataList.Single().Signal != "Assigned" || page.NonContainerObjects.Single().ParentName != "Cabinet A")
            throw new InvalidOperationException("Shared container rows or linked-but-unassigned signals were lost.");
        page.ObjectTypeFilters.Single().IsVisible = false;
        if (page.NonContainerObjectsView.Cast<object>().Any()) throw new InvalidOperationException("The FEE-type filter did not hide CounterSLO.");
        page.SelectedRoot!.Editor.Signals.Single().IsIncluded = false;
        if (page.LinkedUnassignedSignalCount != 2 || page.FoundContainers.Single().LinkedUnassignedSignalCount != 1)
            throw new InvalidOperationException("Signal counts were not recalculated after editing the export/assignment.");
        page.ShowLinkedSignalsCommand.Execute(page.FoundContainers.Single());
        if (page.LinkedUnassignedSignals.Single().Signal != "Assigned" || page.SelectedResultTabIndex != 3)
            throw new InvalidOperationException("The per-container linked signal button selected another scope.");
    }
}
