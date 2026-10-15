using System.Xml.Linq;
using VIBN_Tools.Application.VM;
using VIBN_Tools.ContainerGeneration.BusinessLogic.ContainerData;
using VIBN_Tools.ContainerGeneration.BusinessLogic.RequirementsXml;
using VIBN_Tools.ContainerGeneration.Models;
using VIBN_Tools.ContainerToFee;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.UiStartup.SmokeTests;

internal static partial class Program
{
    private static void VerifyReverseHierarchyAndExchanges()
    {
        VerifyReverseConnectionGraph();
        VerifyRobotInterfaceBridges();
        VerifyStandaloneSimpleLogicTypes();
        VerifyCabinetContextRoundTrip();
        VerifyReverseRequirementsGate();
    }

    private static void VerifyRobotInterfaceBridges()
    {
        var plc = Guid.NewGuid().ToString("D"); var robot = Guid.NewGuid().ToString("D"); var other = Guid.NewGuid().ToString("D");
        var move = Guid.NewGuid(); var secondMove = Guid.NewGuid(); var thirdMove = Guid.NewGuid();
        var first = new FeeContainerLiveVariable(Guid.NewGuid(), "SendToRobot1_Byte10", "%Q10.0", "", "Byte", "", "Read", plc, "PLC");
        var second = new FeeContainerLiveVariable(Guid.NewGuid(), "ReceiveFromRobot1_Byte2", "%I2.0", "", "Byte", "", "Write", plc, "PLC");
        var peer = new FeeContainerLiveVariable(Guid.NewGuid(), "ReceiveFromPLC_Byte0", "$IN[32]", "", "Byte", "", "Write", robot, "Robot");
        var unrelated = new FeeContainerLiveVariable(Guid.NewGuid(), "SendToRobot1_Byte0", "%Q0.0", "", "Byte", "", "Read", other, "Other PLC");
        FeeContainerLiveVariable[] variables = [first, second, peer, unrelated];
        FeeContainerLiveAssignment[] routes = [new(first.VariableGuid, move, "Input 01"), new(peer.VariableGuid, move, "Output 01"),
            new(second.VariableGuid, secondMove, "Input 01"), new(unrelated.VariableGuid, thirdMove, "Input 01")];
        var endpoints = new Dictionary<Guid, FeeSignalEndpoint> { [move] = new(move, "Bridge", "MoveByte"),
            [secondMove] = new(secondMove, "Return", "MoveByte"), [thirdMove] = new(thirdMove, "Other", "MoveByte") };
        var req = XDocument.Parse("<AutoCreate/>");
        var root = FeeSignalContainerBuilder.Build(variables, routes, req, endpoints: endpoints);
        var document = root.Provenance!.ContainerDocument;
        var containers = document.Descendants("Container").ToArray();
        var merged = containers.Single(item => item.Descendants("Entry").Count() == 3);
        if (containers.Length != 2 || merged.Element("Type")?.Value != FeeRobotExchangeGrouping.RobotPlcType ||
            merged.Attribute("address")?.Value != "%I2.0" || merged.Attribute("specialDevice")?.Value != "true" ||
            merged.Descendants("Entry").Any(item => item.Attribute("feeType")?.Value != "MoveByte"))
            throw new InvalidOperationException("Robot exchange grouping lost interface boundaries, endpoint types or numeric minimum addresses.");
        var selectedOnly = FeeSignalContainerBuilder.Build([first, second], routes, req, endpoints: endpoints);
        if (selectedOnly.Provenance!.SignalBindings.Count != 2 || selectedOnly.LiveAssignments!.Any(item => item.VariableGuid == peer.VariableGuid))
            throw new InvalidOperationException("An unselected interface leaked through a robot bridge.");
        endpoints[move] = endpoints[move] with { FeeType = "BoolCopy5Outputs" };
        var unproven = FeeSignalContainerBuilder.Build(variables, routes, req, endpoints: endpoints);
        if (unproven.Provenance!.ContainerDocument.Descendants("Container").Count() != 3)
            throw new InvalidOperationException("A non-MoveByte endpoint merged robot interfaces.");
        endpoints[move] = endpoints[move] with { FeeType = "MoveByte" };
        var ambiguous = unrelated with { InterfaceGuid = plc, Signal = "SendToRobot2_Byte0" };
        var ambiguousRoutes = routes.Where(item => item.VariableGuid != unrelated.VariableGuid).Append(new(ambiguous.VariableGuid, move, "Input 01")).ToArray();
        var ambiguousRoot = FeeSignalContainerBuilder.Build([first, second, peer, ambiguous], ambiguousRoutes, req, endpoints: endpoints);
        if (ambiguousRoot.Provenance!.ContainerDocument.Descendants("Container").Count() != 3)
            throw new InvalidOperationException("An ambiguous MoveByte bridge combined two different robots.");
    }

    private static void VerifyStandaloneSimpleLogicTypes()
    {
        string[] types = ["BoolRo", "BoolNot", "BoolCopy", "BoolCopy5Outputs", "MoveByte"];
        var variables = types.Select(type => new FeeContainerLiveVariable(Guid.NewGuid(), type + "_Input", "%I0.0", "", "Bool", "", "Write", "PLC", "PLC")).ToArray();
        var endpoints = types.Select(type => new FeeSignalEndpoint(Guid.NewGuid(), type + " instance", type)).ToArray();
        var routes = variables.Select((variable, index) => new FeeContainerLiveAssignment(variable.VariableGuid, endpoints[index].Guid, "Input 01")).ToArray();
        var root = FeeSignalContainerBuilder.Build(variables, routes, XDocument.Parse("<AutoCreate/>"), endpoints: endpoints.ToDictionary(item => item.Guid));
        var containers = root.Provenance!.ContainerDocument.Descendants("Container").ToArray();
        if (containers.Length != types.Length || containers.Any(item => !types.Contains(item.Element("Type")?.Value ?? "")) ||
            containers.Any(item => item.Attribute("recognitionSource")?.Value != "Logik/Signal" || item.Descendants("SimObject").Any()))
            throw new InvalidOperationException("Standalone SimpleLogic endpoints lost their actual types or were assigned as scene objects too early.");
    }

    private static void VerifyCabinetContextRoundTrip()
    {
        var cabinetA = Guid.NewGuid(); var cabinetB = Guid.NewGuid(); var elementA = Guid.NewGuid(); var elementB = Guid.NewGuid();
        var iface = Guid.NewGuid().ToString("D");
        FeeContainerLiveVariable[] variables = [new(Guid.NewGuid(), "SelectorA", "%I1.0", "", "Bool", "", "Write", iface, "PLC"),
            new(Guid.NewGuid(), "SelectorB", "%I1.1", "", "Bool", "", "Write", iface, "PLC")];
        FeeContainerLiveAssignment[] routes = [new(variables[0].VariableGuid, elementA, "NO1"), new(variables[1].VariableGuid, elementB, "NO1")];
        FeeContainerLiveObject[] objects = [new(cabinetA, "Cabinet A", "Cabinet"), new(cabinetB, "Cabinet B", "Cabinet"),
            new(elementA, "Selector", "CabinetElement", CabinetDefinition: "Grob_2PositionSwitch", ParentGuid: cabinetA),
            new(elementB, "Selector", "CabinetElement", CabinetDefinition: "Grob_2PositionSwitch", ParentGuid: cabinetB)];
        var live = FeeContainerLiveReconstructor.Reconstruct(Guid.NewGuid(), "Root", objects, variables, routes, requireConnectedSignal: true);
        FeeContainerObjectDetails[] details = [new(cabinetA, "Cabinet A", "Cabinet", "Root", "", ""), new(cabinetB, "Cabinet B", "Cabinet", "Root", "", ""),
            new(elementA, "Selector", "CabinetElement", "Cabinet A", "", "NO1", ParentGuid: cabinetA.ToString("D")),
            new(elementB, "Selector", "CabinetElement", "Cabinet B", "", "NO1", ParentGuid: cabinetB.ToString("D"))];
        var scene = new Fee2ContainerRoot(Guid.NewGuid(), "Root", live.Snapshot, 2, 0, 2, 0, false,
            ObjectAssociations: live.ObjectAssociations, ObjectDetails: details, LiveVariables: variables, LiveAssignments: routes);
        var generated = FeeSignalContainerBuilder.Build(variables, routes, XDocument.Parse("<AutoCreate/>"));
        var assigned = FeeSignalObjectAssigner.Assign(generated, [scene]);
        var vm = new Fee2ContainerPageVM(); var selected = new Fee2ContainerRootSelectionVM(assigned) { IsSelected = true };
        vm.Roots.Add(selected); vm.SelectedRoot = selected; PumpDispatcher(TimeSpan.FromMilliseconds(30));
        if (vm.FoundContainersView.Cast<object>().OfType<Fee2ContainerFoundContainerVM>().Count() != 2 ||
            vm.FoundContainers.Where(item => item.Type == "Cabinet").Any(item => item.ChildContainers.Count != 1 || item.DisplaySignalCount != 1))
            throw new InvalidOperationException("Cabinet elements were not displayed under their own parent containers.");
        var export = selected.Editor.CreateSnapshot().ContainerDocument;
        var path = Path.Combine(Path.GetTempPath(), "vibn-cabinet-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            export.Save(path);
            var loaded = ContainerFileWorkspaceReader.Read(path);
            var child = loaded.Containers.Single(item => item.ParentGuid == cabinetA.ToString("D"));
            if (child.ParentName != "Cabinet A" || child.ParentContainerId.Length == 0 || child.DataList.Single().InterfaceGuid != iface)
                throw new InvalidOperationException("ContainerGeneration import lost cabinet or interface identity.");
            var undo = WorkspaceUndoState.Capture("Cabinet", loaded.Containers, [], []);
            if (undo.Containers.Single(item => item.ParentGuid == child.ParentGuid).ParentName != child.ParentName)
                throw new InvalidOperationException("Undo lost cabinet context.");
            var runtime = ContainerToFeeService.ReadInContainerXmlData(export).Item1;
            if (runtime.Count != 4 || runtime.Count(item => item.ParentCabinetName == "Cabinet A") != 1 ||
                runtime.Count(item => item.ParentCabinetName == "Cabinet B") != 1)
                throw new InvalidOperationException("Container2FEE runtime binding collapsed distinct cabinets.");
            var plan = new ContainerToFeeVisualPlanService().LoadXmlAsync(path).GetAwaiter().GetResult().Plan!;
            var elementNodes = plan.Nodes.Where(item => item.Kind == VisualNodeKind.Container && item.TypeName == "CabinetSwitch").ToArray();
            if (elementNodes.Length != 2 || elementNodes.Any(item => plan.FindNode(item.ParentId!)?.TypeName != "Cabinet"))
                throw new InvalidOperationException("The visual plan lost the cabinet hierarchy.");
        }
        finally { File.Delete(path); }
        var firstParent = vm.FoundContainers.Single(item => item.Type == "Cabinet" && item.Component == "Cabinet A");
        vm.RemoveContainerCommand.Execute(firstParent);
        if (selected.Editor.Containers.Any(item => item.Id == firstParent.Id || item.ParentContainerId == firstParent.Id))
            throw new InvalidOperationException("Cabinet deletion left hidden child records in the export.");
        vm.UndoCommand.Execute(null);
        if (vm.FoundContainers.Count(item => item.Type == "Cabinet") != 2) throw new InvalidOperationException("Cabinet undo failed.");
    }

    private static void VerifyReverseRequirementsGate()
    {
        var previous = RequirementsProvider.RequirementsFile;
        try
        {
            RequirementsProvider.RequirementsFile = null;
            var vm = new Fee2ContainerPageVM();
            if (vm.CanGenerateSignalContainers || vm.CanExport || !vm.GenerateSignalContainersHelp.Contains("Req.xml", StringComparison.Ordinal))
                throw new InvalidOperationException("Missing Req.xml did not disable generation/export with an explanation.");
            try { FeeSignalContainerBuilder.Build([], [], null!); throw new InvalidOperationException("Signal generation accepted a missing Req.xml."); }
            catch (ArgumentNullException) { }
        }
        finally { RequirementsProvider.RequirementsFile = previous; }
        var first = new ContainerEntry { FeeGuid = Guid.NewGuid().ToString("D"), ID = "Equal", Address = "%I0.0", Signal = "Equal" };
        var second = first.Clone(); second.FeeGuid = Guid.NewGuid().ToString("D");
        var state = GenerationWorkspaceReconciler.Capture([], [first, second], []);
        if (state.Entries.Count != 2) throw new InvalidOperationException("Different live signal GUIDs collapsed during generation workspace capture.");
    }
}
