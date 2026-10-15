using System.Xml.Linq;
using VIBN_Tools.Application.VM;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.SpecialDevices;

namespace VIBN_Tools.UiStartup.SmokeTests;

internal static partial class Program
{
    private static void VerifySignalFirstReverseWorkflow()
    {
        var ifaceA = Guid.NewGuid().ToString("D"); var ifaceB = Guid.NewGuid().ToString("D");
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var unused = Guid.NewGuid(); var helperSignal = Guid.NewGuid();
        var primary = Guid.NewGuid(); var helper = Guid.NewGuid();
        var variables = new FeeContainerLiveVariable[] { new(first, "MotorReady", "%I0.0", "", "Bool", "", "Write", ifaceA, "PLC A"),
            new(second, "MotorReady", "%I10.0", "", "Bool", "", "Write", ifaceB, "PLC B"),
            new(unused, "Unused", "%I20.0", "", "Bool", "", "Write", ifaceA, "PLC A"),
            new(helperSignal, "CopyInput", "%Q0.0", "", "Bool", "", "Read", ifaceA, "PLC A") };
        var routes = new FeeContainerLiveAssignment[] { new(first, primary, "Ready"), new(second, Guid.NewGuid(), "Ready"), new(helperSignal, helper, "Input 01") };
        var req = XDocument.Parse("""
            <AutoCreate><Components><Component name="Motor" type="Sensor"><Slots><Slot name="PLC_IN">
                <Keygroup type="required"><KeySet><Key match="exact" keep="true">MotorReady</Key></KeySet></Keygroup>
            </Slot></Slots></Component></Components></AutoCreate>
            """);
        var generated = FeeSignalContainerBuilder.Build(variables, routes, req);
        var generatedSnapshot = generated.Provenance ?? throw new InvalidOperationException("No signal-first snapshot.");
        if (generated.ContainerCount != 3 || generatedSnapshot.SignalBindings.Any(item => item.VariableGuid == unused) ||
            generatedSnapshot.ContainerDocument.Descendants("SimObject").Any() ||
            generatedSnapshot.ContainerDocument.Descendants("Container").Count(item => item.Attribute("origin")?.Value == "Signal und Req. XML") != 2 ||
            generatedSnapshot.ContainerDocument.Descendants("Entry").Any(entry => entry.Attribute("interfaceName") is null))
            throw new InvalidOperationException("Signal-only generation mixed interfaces, included inactive variables or preassigned objects.");
        var selected = FeeSignalContainerBuilder.Build(variables.Where(item => item.InterfaceGuid == ifaceA).ToArray(), routes, req);
        if (selected.Provenance!.SignalBindings.Any(item => item.VariableGuid == second) || selected.LiveAssignments!.Any(item => item.VariableGuid == second))
            throw new InvalidOperationException("An excluded interface leaked into generation.");
        var filteredRequirements = new XDocument(req);
        filteredRequirements.Root!.Add(new XElement("FilterList", new XElement("Key", new XAttribute("match", "exact"), "CopyInput")));
        var filtered = FeeSignalContainerBuilder.Build(variables, routes, filteredRequirements);
        if (filtered.Provenance!.SignalBindings.Any(item => item.VariableGuid == helperSignal) ||
            !filtered.LiveAssignments!.Any(item => item.VariableGuid == helperSignal))
            throw new InvalidOperationException("Req-excluded live signals were recreated as fallback containers or lost their live routes.");
        var liveObjects = new FeeContainerLiveObject[] { new(primary, "MotorReady", "LogicObject", "Grob_Sensor"),
            new(helper, "CopyUnit", "BoolCopy5Outputs") };
        var scene = FeeContainerLiveReconstructor.Reconstruct(Guid.NewGuid(), "Root", liveObjects, variables, routes, requireConnectedSignal: true);
        if (!scene.Snapshot.ContainerDocument.Descendants("Container").Any(item => item.Element("Component")?.Value == "CopyUnit") ||
            !scene.Snapshot.SignalBindings.Any(item => item.VariableGuid == helperSignal))
            throw new InvalidOperationException("SimpleLogic-only live links did not create a visible container.");
        var rawRoot = new Fee2ContainerRoot(Guid.NewGuid(), "Root", scene.Snapshot, 2, 0, 2, 0, false,
            ObjectAssociations: scene.ObjectAssociations, ObjectDetails: [new(primary, "MotorReady", "LogicObject", "Root", "Grob_Sensor", "Ready"),
                new(helper, "CopyUnit", "BoolCopy5Outputs", "Root", "BoolCopy5Outputs", "Input 01")],
            LiveVariables: variables, LiveAssignments: routes.Where(item => item.VariableGuid != second).ToArray());
        var assigned = FeeSignalObjectAssigner.Assign(generated, [rawRoot]);
        if (!assigned.ObjectAssociations!.Any(item => item.ObjectGuid == helper) ||
            !assigned.Provenance!.ContainerDocument.Descendants("Container").Any(item => item.Element("Component")?.Value == "CopyUnit"))
            throw new InvalidOperationException("Separate object assignment lost the helper endpoint or its actual name.");
        var emptyScope = FeeSignalObjectAssigner.Assign(generated, []);
        if (emptyScope.ObjectAssociations!.Count != 0) throw new InvalidOperationException("Unselected roots assigned scene objects.");
        var noRequirementsAssigned = FeeSignalObjectAssigner.Assign(selected, [rawRoot]);
        if (!noRequirementsAssigned.Provenance!.ContainerDocument.Descendants("Container").Any(item =>
            item.Element("Component")?.Value == "MotorReady" && item.Element("Type")?.Value == "Sensor"))
            throw new InvalidOperationException("Current FEE object definitions did not resolve the signal-only N/A type.");
        VerifyReverseUndoAndPendingDevice(assigned);
    }

    private static void VerifyReverseUndoAndPendingDevice(Fee2ContainerRoot source)
    {
        var selection = new Fee2ContainerRootSelectionVM(source) { IsSelected = true };
        var vm = new Fee2ContainerPageVM(); vm.Roots.Add(selection); vm.SelectedRoot = selection;
        PumpDispatcher(TimeSpan.FromMilliseconds(30));
        var container = vm.FoundContainers.Single(item => item.Component == "CopyUnit");
        var id = container.Id; vm.RemoveContainerCommand.Execute(container);
        if (!vm.CanUndo) throw new InvalidOperationException("Container deletion did not create an undo state.");
        vm.UndoCommand.Execute(null);
        var restored = vm.FoundContainers.Single(item => item.Id == id);
        var signal = restored.DataList.Single(); var previousAddress = signal.Address;
        signal.Address = "%Q88.0";
        vm.UndoCommand.Execute(null);
        restored = vm.FoundContainers.Single(item => item.Id == id);
        if (restored.DataList.Single().Address != previousAddress || restored.DataList.Single().InterfaceName != "PLC A")
            throw new InvalidOperationException("Inline-edit undo lost the address or interface identity.");
        vm.AddToSpecialDevicesCommand.Execute(restored);
        if (!restored.IsSpecialDevice || restored.SpecialDeviceWarning.Length == 0 || vm.SelectedResultTabIndex != 2)
            throw new InvalidOperationException("Unknown logic was not marked as a SpecialDevice candidate with a definition warning.");
        var candidate = vm.CreateSpecialDeviceCandidates().Single();
        if (candidate.IsDefinitionKnown || candidate.LogicDefinitionName != "BoolCopy5Outputs" || candidate.LinkedSignals.Count != 1)
            throw new InvalidOperationException("Pending SpecialDevice lost its current logic or signals.");
        var devices = new Fee2SpecialDevicesPageVM(); devices.LoadDevices([candidate]);
        if (devices.Issues.Count != 1 || devices.CanExport) throw new InvalidOperationException("Undefined device logic was represented as a registered exportable device.");
        var otherDevice = candidate with { Guid = Guid.NewGuid(), Name = "Other copy" };
        devices.AddDevices([otherDevice]); devices.AddDevices([candidate]);
        if (devices.Roots.Count != 2 || devices.SelectedRoot != candidate || devices.Issues.Count != 1)
            throw new InvalidOperationException("Adding a SpecialDevice candidate replaced another device or duplicated it.");
        vm.UndoCommand.Execute(null);
        if (vm.FoundContainers.Single(item => item.Id == id).IsManuallySpecialDevice)
            throw new InvalidOperationException("SpecialDevice classification could not be undone.");
    }
}
