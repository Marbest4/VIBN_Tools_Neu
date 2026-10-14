using System.Xml.Linq;
using VIBN_Tools.Application.Behaviors;
using VIBN_Tools.Application.VM;
using VIBN_Tools.ContainerGeneration.Models;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.GlobalClasses.FeeObjects;
using VIBN_Tools.SpecialDevices;

namespace VIBN_Tools.UiStartup.SmokeTests;

internal static partial class Program
{
    private static void VerifyLiveSpecialDeviceAndSignalEditing()
    {
        var first = new FeeLogic { Name = "Device", LogicDefinitionName = "Grob_SimModeSiemens", LogicDefinitionGuid = Guid.NewGuid() };
        var second = new FeeLogic { Name = "Device", LogicDefinitionName = "Grob_SimModeSiemens", LogicDefinitionGuid = first.LogicDefinitionGuid };
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        first.Slots = new() { ["PLC_OUT_A"] = a, ["PLC_OUT_B"] = b, ["PLC_IN_C"] = c, ["PLC_IN_Missing"] = Guid.Empty };
        second.Slots = new() { ["PLC_OUT_A"] = a };
        var variables = new[] { new FeeContainerLiveVariable(a, "A", "%EW80", "Symbolic_Path", "Int", "", "Write"),
            new FeeContainerLiveVariable(b, "B", "E12.7", "", "Bool", "", "Write"),
            new FeeContainerLiveVariable(c, "C", "QD5", "", "DInt", "", "Read") };
        var assignments = new[] { new FeeContainerLiveAssignment(a, first.Guid, "PLC_OUT_A"),
            new FeeContainerLiveAssignment(b, first.Guid, "PLC_OUT_B"), new FeeContainerLiveAssignment(c, first.Guid, "PLC_IN_C"),
            new FeeContainerLiveAssignment(a, second.Guid, "PLC_OUT_A") };
        var devices = FeeSpecialDeviceLiveProjector.Project([first, second], variables, assignments);
        if (devices.Count != 2) throw new InvalidOperationException("Same-name device instances were collapsed.");
        var current = devices.Single(item => item.Guid == first.Guid);
        if (current.CurrentInputByte != 12 || current.CurrentOutputByte != 5 || current.CurrentSignalCount != 3 ||
            current.MissingSignalCount != 1 || current.ExpectedSignalCount != 4 || current.LinkedSignals.Single(item => item.VariableGuid == a).Address != "%EW80")
            throw new InvalidOperationException("Current device bytes, signal coverage or linked addresses were inferred incorrectly.");
        var changed = FeeSpecialDeviceLiveProjector.Project([first], variables.Select(item => item.VariableGuid == a ? item with { Address = "IW2" } : item).ToArray(), assignments).Single();
        if (changed.CurrentInputByte != 2) throw new InvalidOperationException("Changed addresses did not replace old device base bytes.");
        var reconstructed = FeeContainerLiveReconstructor.Reconstruct(Guid.NewGuid(), "Root",
            [new(first.Guid, first.Name, "LogicObject", first.LogicDefinitionName, IsSpecialDevice: true),
             new(second.Guid, second.Name, "LogicObject", second.LogicDefinitionName, IsSpecialDevice: true)], variables, assignments);
        var root = new Fee2ContainerRoot(Guid.NewGuid(), "Root", reconstructed.Snapshot, 4, 0, 4, 0, false,
            ObjectAssociations: reconstructed.ObjectAssociations, LiveVariables: variables, LiveAssignments: assignments, SpecialDevices: devices);
        var vm = new Fee2ContainerPageVM { SelectedRoot = new Fee2ContainerRootSelectionVM(root) };
        PumpDispatcher(TimeSpan.FromMilliseconds(50));
        if (vm.FoundContainers.Count != 2 || vm.LinkedUnassignedSignalCount != 0 || vm.SpecialDevicesView.Cast<object>().Count() != 2 ||
            vm.FoundContainers.Single(item => item.AssociatedObjectItems.Any(association => association.ObjectGuid == first.Guid)).InputByteText != "12" ||
            vm.FoundSignals.Where(item => item.VariableGuid == a).Any(item => item.Address != "%EW80"))
            throw new InvalidOperationException("SpecialDevice signals were omitted from container rows or reported as unassigned.");
        var signal = vm.FoundSignals.Single(item => item.VariableGuid == b);
        vm.RemoveSignalCommand.Execute(signal);
        if (vm.LinkedUnassignedSignalCount != 1 || vm.LinkedUnassignedSignals.Single().Guid != b)
            throw new InvalidOperationException("Removing a signal assignment did not preserve its live route as unassigned.");
        var target = vm.FoundContainers.Single(item => item.Id == signal.ContainerId);
        var drop = new ContainerToFeeVisualDropRequest(vm.LinkedUnassignedSignals.Single(), target);
        if (!vm.AssignObjectToContainerCommand.CanExecute(drop)) throw new InvalidOperationException("Linked signal drag/drop is disabled.");
        vm.AssignObjectToContainerCommand.Execute(drop);
        if (vm.LinkedUnassignedSignalCount != 0 || vm.FoundSignals.Count(item => item.VariableGuid == b) != 1)
            throw new InvalidOperationException("Drag/drop lost or duplicated the live variable identity.");
        var received = new Fee2SpecialDevicesPageVM(); received.LoadDevices(devices);
        if (received.Roots.Count != 2 || received.SelectedRoot is null) throw new InvalidOperationException("Snapshot transfer to FEE2SpecialDevices failed.");
        VerifyNonLinkableAndUnlinkedSignalColors();
    }

    private static void VerifyNonLinkableAndUnlinkedSignalColors()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vibn-unlinkable-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var container = CurrentContainer("Unknown", "Existing", "%I0.0"); container.Element("Type")!.Value = "Unknown";
            var path = Path.Combine(directory, "Source.xml"); ContainerFileXml.Document([container]).Save(path);
            var service = new ContainerToFeeVisualPlanService(); var plan = service.LoadXmlAsync(path).GetAwaiter().GetResult().Plan!;
            var iface = new VisualFeeInterface(Guid.NewGuid().ToString("D"), "PLC", "Test", 1);
            var signal = new VisualFeeSignal(Guid.NewGuid().ToString("D"), iface.GuidString, "PLC", "Existing", "%I0.0", "", "Bool", "Write");
            var node = plan.Nodes.Single(item => item.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal);
            SetPrivateField(service, "_feeSignals", new[] { signal }); SetPrivateField(service, "_feeInterfaces", new[] { iface });
            SetPrivateField(service, "_hasDiscoveredFeeInterfaces", true); SetPrivateField(service, "_hasDiscoveredFeeSignalLinks", true);
            service.SetExistingInterfaces([iface]);
            if (!service.TryAssignSignal(node.Id, signal.GuidString).Success) throw new InvalidOperationException("Signal fixture assignment failed.");
            var row = new ContainerToFeeVisualFeeSignalVM(signal, [signal], plan, [], []);
            if (!row.IsAssigned || row.StateBackground != "#FFE8D9F3") throw new InvalidOperationException("Assigned, unlinked existing signal is not purple.");
            var yellow = new ContainerToFeeVisualFeeSignalVM(signal, [signal], plan, [], [node.Id]);
            if (yellow.StateBackground != "#FFFFF2CC" || !yellow.IsNotLinkable || new VisualSignalConnectionState(VisualSignalConnectionKind.NotRequired, "").IsVerified)
                throw new InvalidOperationException("Non-linkable signal-only types were certified as linked.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
