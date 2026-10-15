using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using VIBN_Tools.Application.VM;
using VIBN_Tools.ContainerToFeeVisual;

namespace VIBN_Tools.UiStartup.SmokeTests;

internal static partial class Program
{
    private static void VerifyReverseConnectionGraph()
    {
        VerifyLiveLogicOwnershipAndUnknownExport();
        VerifySimpleLogicInterfaceOwnership();
        VerifyCabinetElementVisibility();
    }

    private static void VerifyLiveLogicOwnershipAndUnknownExport()
    {
        var a = Guid.NewGuid().ToString("D"); var b = Guid.NewGuid().ToString("D");
        var logic = Guid.NewGuid(); var helper = Guid.NewGuid(); var other = Guid.NewGuid();
        FeeContainerLiveVariable[] variables =
        [
            new(Guid.NewGuid(), "Ready", "%I12.7", "old/generated/path", "Bool", "Ready", "Write", a, "PLC A"),
            new(Guid.NewGuid(), "ReadyMirror", "%I3.6", "", "Bool", "", "Write", b, "PLC B"),
            new(Guid.NewGuid(), "Reset", "%Q8.6", "", "Bool", "", "Read", a, "PLC A"),
            new(Guid.NewGuid(), "Inactive", "%I0.0", "", "Bool", "", "Write", a, "PLC A"),
            new(Guid.NewGuid(), "Ready", "%I0.1", "", "Bool", "", "Write", b, "PLC B")
        ];
        FeeContainerLiveAssignment[] routes = [new(variables[0].VariableGuid, logic, "PLC_IN_Ready"),
            new(variables[1].VariableGuid, helper, "Output 02"), new(variables[2].VariableGuid, logic, "PLC_OUT_Reset"),
            new(variables[4].VariableGuid, other, "PLC_IN_Ready")];
        VisualFeeObjectLink[] links = [new(helper.ToString("D"), "Input 01", logic.ToString("D"), "PLC_IN_Ready")];
        var endpoints = new Dictionary<Guid, FeeSignalEndpoint>
        {
            [logic] = new(logic, "Scanner", "LogicObject", "CustomScanner", ["PLC_IN_Ready", "PLC_OUT_Reset", "PLC_IN_Unconnected"]),
            [other] = new(other, "Scanner", "LogicObject", "OtherScanner", ["PLC_IN_Ready"]),
            [helper] = new(helper, "Copy", "BoolCopy5Outputs", SlotNames: ["Input 01", "Output 02"])
        };
        var req = XDocument.Parse("""
            <AutoCreate><Components><Component name="Sensor" type="Sensor"><Slots><Slot name="PLC_IN">
              <Keygroup type="required"><KeySet><Key match="exact" keep="true">Ready</Key></KeySet></Keygroup>
            </Slot></Slots></Component></Components></AutoCreate>
            """);
        var generated = FeeSignalContainerBuilder.Build(variables, routes, req, endpoints: endpoints, objectLinks: links);
        var snapshot = generated.Provenance ?? throw new InvalidOperationException("No signal workspace snapshot.");
        var containers = snapshot.ContainerDocument.Descendants("Container").ToArray();
        if (containers.Length != 2 || containers.Any(item => item.Element("Type")?.Value != "N/A" || item.Attribute("recognitionSource")?.Value != "Logik/Signal") ||
            snapshot.SignalBindings.Any(item => item.VariableGuid == variables[3].VariableGuid) || containers.SelectMany(item => item.Descendants("SimObject")).Any())
            throw new InvalidOperationException("Req.xml overrode current unknown logic, equal names merged different objects, or inactive signals/early object assignments leaked in.");
        var owner = containers.Single(item => item.Attribute("targetGuid")?.Value == logic.ToString("D"));
        if (owner.Descendants("Entry").Count() != 3 || owner.Descendants("Entry").Select(item => item.Attribute("interfaceGuid")?.Value).Distinct().Count() != 2)
            throw new InvalidOperationException("A helper input did not transfer its interface signals to the upstream logic container.");
        var graph = new FeeSignalOwnershipGraph(endpoints, links);
        var details = graph.CreateDetails(routes);
        if (!details.Single(item => item.Guid == helper).ConnectionText.Contains("Input 01", StringComparison.Ordinal) ||
            !details.Single(item => item.Guid == helper).ConnectionText.Contains("Output 02", StringComparison.Ordinal))
            throw new InvalidOperationException("SimpleLogic details omitted their input or output connection.");
        var scene = new Fee2ContainerRoot(Guid.NewGuid(), "Current scene", null, 0, 0, 0, 0,
            ObjectDetails: details, LiveVariables: variables, LiveAssignments: routes, ObjectLinks: links);
        var assigned = FeeSignalObjectAssigner.Assign(generated, [scene]);
        if (assigned.NonContainerObjects!.Any() || !assigned.ObjectAssociations!.Any(item => item.ObjectGuid == helper &&
            item.ContainerObjectGuid == logic && item.Role == "TechnicalHelper"))
            throw new InvalidOperationException("A signal-connected helper or primary object was left without a container.");
        var editor = new Fee2ContainerRootEditor(assigned);
        var row = editor.Containers.Single(item => item.SignalTargetGuid == logic.ToString("D"));
        if (row.LogicDefinitionName != "CustomScanner" || row.InputByteText != "3" || row.OutputByteText != "8" || row.LinkedUnassignedSignalCount != 0)
            throw new InvalidOperationException("Current logic definition, minimum E/A byte addresses, or complete signal ownership is missing.");
        var document = FeeUnknownLogicExporter.Create([assigned]);
        var exported = document.Containers.Single(item => item.ContainerId == row.Id);
        var exportedLogic = exported.Logics.Single(item => item.Guid == logic);
        var ready = exportedLogic.Slots.Single(item => item.Name == "PLC_IN_Ready");
        if (exportedLogic.LogicDefinitionName != "CustomScanner" || ready.Signals.Count != 2 ||
            ready.Signals.Single(item => item.Guid == variables[1].VariableGuid) is not { Address: "%I3.6", ViaHelper: true, InterfaceName: "PLC B" } ||
            exportedLogic.Slots.Single(item => item.Name == "PLC_IN_Unconnected").Signals.Count != 0 ||
            exported.Logics.Single(item => item.Guid == helper).Slots.Single(item => item.Name == "Input 01").ObjectConnections.Single().Guid != logic)
            throw new InvalidOperationException("N/A export lost logic slots, indirect signals, current addresses, empty slots, or helper input peers.");
        var path = Path.Combine(Path.GetTempPath(), "vibn-na-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Task.Run(() => FeeUnknownLogicExporter.SaveAsync(document, path)).GetAwaiter().GetResult();
            var before = File.ReadAllText(path);
            using var json = JsonDocument.Parse(before);
            if (json.RootElement.GetProperty("schema").GetString() != "VIBN_Tools.FEE2Container.UnknownLogic.v1" ||
                json.RootElement.GetProperty("containers").GetArrayLength() != 2)
                throw new InvalidOperationException("The N/A diagnostic file is not a complete structured JSON document.");
            try
            {
                Task.Run(() => FeeUnknownLogicExporter.SaveAsync(document, path, new CancellationToken(true))).GetAwaiter().GetResult();
                throw new InvalidOperationException("Canceled N/A export completed.");
            }
            catch (OperationCanceledException) { }
            if (File.ReadAllText(path) != before) throw new InvalidOperationException("A canceled export replaced the existing JSON.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void VerifySimpleLogicInterfaceOwnership()
    {
        var helper = Guid.NewGuid(); var primary = Guid.NewGuid(); var second = Guid.NewGuid();
        var a = Guid.NewGuid().ToString("D"); var b = Guid.NewGuid().ToString("D");
        FeeContainerLiveVariable[] variables = [new(Guid.NewGuid(), "PlainInput", "%Q4.1", "", "Byte", "", "Read", a, "PLC"),
            new(Guid.NewGuid(), "PlainOutput", "$IN[16]", "", "Byte", "", "Write", b, "Robot")];
        FeeContainerLiveAssignment[] routes = [new(variables[0].VariableGuid, helper, "Input 01"), new(variables[1].VariableGuid, helper, "Output 01")];
        var endpoints = new Dictionary<Guid, FeeSignalEndpoint> { [helper] = new(helper, "Bridge", "MoveByte"),
            [primary] = new(primary, "Downstream", "LogicObject"), [second] = new(second, "Next", "BoolOr") };
        var generated = FeeSignalContainerBuilder.Build(variables, routes, XDocument.Parse("<AutoCreate/>"), endpoints: endpoints);
        if (generated.ContainerCount != 1 || generated.SignalCount != 2 || generated.Provenance!.ContainerDocument.Descendants("Type").Single().Value != "MoveByte")
            throw new InvalidOperationException("A SimpleLogic connection split two interfaces into separate containers without a robot-name pattern.");
        var selected = FeeSignalContainerBuilder.Build([variables[0]], routes, XDocument.Parse("<AutoCreate/>"), endpoints: endpoints);
        if (selected.SignalCount != 1 || selected.LiveAssignments!.Any(route => route.VariableGuid == variables[1].VariableGuid))
            throw new InvalidOperationException("Cross-interface grouping imported a signal from an unselected interface.");
        var chained = new Dictionary<Guid, FeeSignalEndpoint>(endpoints) { [second] = new(second, "Byte mirror", "MoveByte") };
        var robotSignals = new[] { variables[0] with { Signal = "SendToRobot1_Byte0" }, variables[1] with { Signal = "ReceiveFromPLC_Byte0" } };
        var robotRoutes = new[] { routes[0], routes[1] with { TargetObjectGuid = second } };
        var bridge = FeeSignalContainerBuilder.Build(robotSignals, robotRoutes, XDocument.Parse("<AutoCreate/>"), endpoints: chained,
            objectLinks: [new(second.ToString("D"), "Input 01", helper.ToString("D"), "Output 01")]);
        if (bridge.ContainerCount != 1 || bridge.Provenance!.ContainerDocument.Descendants("Type").Single().Value != FeeRobotExchangeGrouping.RobotPlcType)
            throw new InvalidOperationException("A current MoveByte chain did not join the PLC and robot interfaces.");
        var downstream = new FeeSignalOwnershipGraph(endpoints, [new(helper.ToString("D"), "Output 01", primary.ToString("D"), "PLC_OUT_Target")]);
        if (downstream.Owners(helper).Single().Guid != helper)
            throw new InvalidOperationException("A helper was incorrectly assigned to an object on its output side.");
        var upstream = new FeeSignalOwnershipGraph(endpoints, [new(helper.ToString("D"), "Input 01", primary.ToString("D"), "PLC_IN_Ready")]);
        if (upstream.Owners(helper).Single().Guid != primary)
            throw new InvalidOperationException("A helper's upstream object was not its owner.");
        var cycle = new FeeSignalOwnershipGraph(endpoints, [new(helper.ToString("D"), "Input 01", second.ToString("D"), "Output 01"),
            new(second.ToString("D"), "Input 01", helper.ToString("D"), "Output 01")]);
        if (cycle.Owners(helper).Single().Guid != cycle.Owners(second).Single().Guid ||
            !FeeSignalOwnershipGraph.HelperSlotNames("BoolOr").Contains("Input 02") ||
            !FeeSignalOwnershipGraph.HelperSlotNames("BoolCopy5Outputs").Contains("Output 05"))
            throw new InvalidOperationException("Helper cycles fail to terminate consistently or relevant input/output ports were omitted.");
    }

    private static void VerifyCabinetElementVisibility()
    {
        var cabinet = Guid.NewGuid(); var element = Guid.NewGuid();
        var a = Guid.NewGuid().ToString("D"); var b = Guid.NewGuid().ToString("D");
        FeeContainerLiveVariable[] variables = [new(Guid.NewGuid(), "SwitchA", "%I1.7", "", "Bool", "", "Write", a, "PLC A"),
            new(Guid.NewGuid(), "SwitchB", "%I2.4", "", "Bool", "", "Write", b, "PLC B")];
        FeeContainerLiveAssignment[] routes = [new(variables[0].VariableGuid, element, "NO1"), new(variables[1].VariableGuid, element, "NO2")];
        var endpoints = new Dictionary<Guid, FeeSignalEndpoint> { [cabinet] = new(cabinet, "Panel", "Cabinet"),
            [element] = new(element, "Switch", "CabinetElement", ParentGuid: cabinet.ToString("D"), ParentName: "Panel", CabinetDefinition: "CabinetSwitch") };
        var generated = FeeSignalContainerBuilder.Build(variables, routes, XDocument.Parse("<AutoCreate/>"), endpoints: endpoints);
        var details = new FeeSignalOwnershipGraph(endpoints, []).CreateDetails(routes);
        var scene = new Fee2ContainerRoot(Guid.NewGuid(), "Root", null, 0, 0, 0, 0,
            ObjectDetails: details, LiveVariables: variables, LiveAssignments: routes);
        var assigned = FeeSignalObjectAssigner.Assign(generated, [scene]);
        var doc = new XDocument(assigned.Provenance!.ContainerDocument);
        var child = doc.Descendants("Container").Single(item => item.Element("Type")?.Value == "CabinetSwitch");
        var duplicate = new XElement(child); duplicate.SetAttributeValue("id", "legacy-copy");
        doc.Root!.Element("ContainerList")!.Add(duplicate);
        var root = assigned with { Provenance = assigned.Provenance! with { ContainerDocument = doc } };
        var vm = new Fee2ContainerPageVM();
        var selection = new Fee2ContainerRootSelectionVM(root) { IsSelected = true };
        vm.Roots.Add(selection); vm.SelectedRoot = selection; PumpDispatcher(TimeSpan.FromMilliseconds(40));
        var top = vm.FoundContainersView.Cast<Fee2ContainerFoundContainerVM>().ToArray();
        if (top.Length != 1 || top[0].Type != "Cabinet" || top[0].ChildContainers.Count != 1 ||
            top[0].ChildContainers[0].Type != "CabinetSwitch" || top[0].DisplaySignalCount != 2 ||
            vm.NonContainerObjects.Any(item => item.Guid == element) || selection.Editor.Containers.Any(item => item.Type == "CabinetElement"))
            throw new InvalidOperationException("Cabinet elements were duplicated at top level, left unmapped, or lost their specific child type/signals.");
        var exported = selection.CreateEditedRoot().Provenance!.ContainerDocument;
        if (exported.Descendants("Container").Count() != 2 || exported.Descendants("Container").Single(item => item.Element("Type")?.Value == "CabinetSwitch")
            .Attribute("parentContainerId")?.Value != top[0].Id)
            throw new InvalidOperationException("Cabinet hierarchy was not retained in the ContainerFile export.");
    }
}
