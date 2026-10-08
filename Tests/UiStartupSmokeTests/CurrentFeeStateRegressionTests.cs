using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Xml.Linq;
using VIBN_Tools.Application.View;
using VIBN_Tools.Application.VM;
using VIBN_Tools.ContainerGeneration.Models;
using VIBN_Tools.ContainerToFee;
using VIBN_Tools.ContainerToFee.General;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.UiStartup.SmokeTests;

internal static partial class Program
{
    private static void VerifyCurrentFeeStateRegressions()
    {
        VerifyFeeRootReadFailures();
        VerifyAutomaticSignalsAndRootScope();
        VerifyLiveStructureOverridesHistory();
        VerifyExistingHelperLinksAreSkipped();
        VerifyReverseSignalCentersContainer();
    }

    private static XElement CurrentContainer(string name, string signal, string address) => new("Container",
        new XAttribute("id", name), new XElement("Component", name), new XElement("Type", "Button"),
        new XElement("DataList", new XElement("Entry", new XElement("ID", name), new XElement("Signal", signal),
            new XElement("Address", address), new XElement("DataType", "Bool"), new XElement("Slot", "PLC_IN_NO"), new XElement("Note", ""))));

    private static void VerifyAutomaticSignalsAndRootScope()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vibn-current-fee-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Current.xml");
            ContainerFileXml.Document([CurrentContainer("A", "Equal", "%I0.0"), CurrentContainer("B", "Equal", "%I0.1"),
                CurrentContainer("Ambiguous", "Duplicate", "%I0.2"), CurrentContainer("WrongType", "Typed", "%I0.3")]).Save(path);
            var service = new ContainerToFeeVisualPlanService();
            var plan = service.LoadXmlAsync(path).GetAwaiter().GetResult().Plan!;
            var firstInterface = new VisualFeeInterface(Guid.NewGuid().ToString("D"), "PLC A", "Test", 4);
            var secondInterface = new VisualFeeInterface(Guid.NewGuid().ToString("D"), "PLC B", "Test", 1);
            VisualFeeSignal Signal(VisualFeeInterface parent, string tag, string address, string type = "Bool") =>
                new(Guid.NewGuid().ToString("D"), parent.GuidString, parent.Name, tag, address, "", type, "Write");
            var first = Signal(firstInterface, "Equal", "I0.0");
            var second = Signal(firstInterface, "Equal", "%I0.1");
            var signals = new[] { first, second, Signal(firstInterface, "Duplicate", "%I0.2"),
                Signal(secondInterface, "Duplicate", "%I0.2"), Signal(firstInterface, "Typed", "%I0.3", "Int") };
            SetPrivateField(service, "_feeSignals", signals);
            SetPrivateField(service, "_feeInterfaces", new[] { firstInterface, secondInterface });
            SetPrivateField(service, "_hasDiscoveredFeeInterfaces", true);
            service.SetExistingInterfaces([firstInterface, secondInterface]);
            var vm = new ContainerToFeeVisualPageVM(service);
            if (!vm.AutoAssignCommand.CanExecute(null) || service.AutoAssignMatches() != 2 || plan.SignalAssignments.Count != 2)
                throw new InvalidOperationException("Signal-only discovery cannot automatically assign unique sources or accepts ambiguous/type-conflicting signals.");
            var a = plan.Nodes.Single(node => node.Kind == VisualNodeKind.Container && node.Name == "A");
            var b = plan.Nodes.Single(node => node.Kind == VisualNodeKind.Container && node.Name == "B");
            string AssignedGuid(string id) => plan.SignalAssignments.Single(item => plan.FindNode(item.SignalNodeId)!.ContainerId == id).FeeSignalGuid;
            if (AssignedGuid(a.Id) != first.GuidString || AssignedGuid(b.Id) != second.GuidString)
                throw new InvalidOperationException("Equal tags with different source addresses were assigned to the wrong signal.");
            var rootA = Guid.NewGuid().ToString("D"); var rootB = Guid.NewGuid().ToString("D");
            var firstObject = ScopedButton("A", rootA, "Root A"); var secondObject = ScopedButton("A", rootB, "Root B");
            SetPrivateField(service, "_feeObjects", new[] { firstObject, secondObject });
            SetPrivateField(service, "_runtimeObjects", new Dictionary<string, FeeAbstractObject>
            {
                [firstObject.Id] = new FeeButton { Guid = Guid.Parse(firstObject.GuidString), Name = "A" },
                [secondObject.Id] = new FeeButton { Guid = Guid.Parse(secondObject.GuidString), Name = "A" },
            });
            SetPrivateField(service, "_hasDiscoveredFeeObjects", true);
            service.SetSimObjectRoots([rootA]);
            if (service.AutoAssignMatches() != 1 || plan.Assignments.Single().FeeObjectId != firstObject.Id)
                throw new InvalidOperationException("Automatic assignment included a SimObject from an unselected root.");
            SetPrivateField(service, "_hasDiscoveredFeeSignalLinks", true);
            SetPrivateField(service, "_hasDiscoveredFeeSimObjectLinks", true);
            var node = plan.Nodes.Single(item => item.Kind == VisualNodeKind.Signal && item.ContainerId == a.Id);
            SetPrivateField(service, "_feeSignalLinks", new[] { new VisualFeeSignalLink(first.GuidString,
                firstObject.GuidString, "Button", "PressedInverted", false) });
            if (service.GetSignalConnectionState(node.Id, first.GuidString).IsVerified || service.FindFullyVerifiedContainerIds().Contains(a.Id))
                throw new InvalidOperationException("A signal connected to the wrong slot certified the container.");
            SetPrivateField(service, "_feeSignalLinks", new[] { new VisualFeeSignalLink(first.GuidString,
                firstObject.GuidString, "Button", "Pressed", false) });
            var verified = service.FindFullyVerifiedContainerIds();
            if (!verified.Contains(a.Id) || service.DeselectVerifiedContainers(verified) != 1 || plan.IsGenerationSelected(a.Id))
                throw new InvalidOperationException("A fully conforming live container was not deselected.");
            var rootsVm = new ContainerToFeeVisualPageVM(service);
            typeof(ContainerToFeeVisualPageVM).GetMethod("RefreshFeeRootProjection", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(rootsVm, null);
            rootsVm.AvailableFeeRoots.Single(root => root.GuidString == rootB).IsSelected = false;
            if (rootsVm.FeeObjectsView.Cast<ContainerToFeeVisualFeeObjectVM>().Any(item => item.Model.RootGuidString != rootA))
                throw new InvalidOperationException("The SimObject list ignored root checkboxes.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static VisualFeeObject ScopedButton(string name, string root, string rootName)
    {
        var guid = Guid.NewGuid().ToString("D");
        return (VisualFeeObject)typeof(VisualFeeObject).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke(["fee:" + guid, guid, name, typeof(FeeButton).FullName!, "Button", new[] { nameof(FeeButton), typeof(FeeButton).FullName! },
                root, rootName, false, "", false, root, rootName]);
    }

    private static void VerifyLiveStructureOverridesHistory()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var unrelated = Guid.NewGuid();
        var currentSignal = Guid.NewGuid(); var oldSignal = Guid.NewGuid();
        var result = FeeContainerLiveReconstructor.Reconstruct(Guid.NewGuid(), "Current",
            [new(first, "Renamed A", "Button", ProvenanceContainerId: "old-owner", ProvenanceContainerType: "Cylinder"),
             new(second, "Renamed B", "LogicObject", "Grob_Sensor", ProvenanceContainerId: "old-owner", ProvenanceContainerType: "Cylinder"),
             new(unrelated, "Old A", "MotionJoint", ProvenanceContainerId: "old-owner", ProvenanceContainerType: "Cylinder")],
            [new(currentSignal, "New Tag", "%I1.0", "", "Bool", "Current"), new(oldSignal, "Old Tag", "%I0.0", "", "Bool", "Historical")],
            [new(currentSignal, first, "Pressed")]);
        var containers = result.Snapshot.ContainerDocument.Descendants("Container").ToArray();
        if (containers.Length != 2 || containers.Single(item => item.Element("Component")!.Value == "Renamed A").Element("Type")!.Value != "Button" ||
            containers.Single(item => item.Element("Component")!.Value == "Renamed B").Element("Type")!.Value != "Sensor" ||
            result.Snapshot.SignalBindings.Single().VariableGuid != currentSignal ||
            result.Snapshot.ContainerDocument.Descendants("Signal").Any(item => item.Value == "Old Tag") ||
            result.ObjectAssociations.Any(item => item.ObjectGuid == unrelated))
            throw new InvalidOperationException("Historical type/owner tags resurrected old signals, merged renamed containers, or assigned a differently named object.");
        var removed = FeeContainerLiveReconstructor.Reconstruct(Guid.NewGuid(), "Removed", [], [], []);
        if (removed.Snapshot.ContainerCount != 0 || removed.Snapshot.SignalCount != 0)
            throw new InvalidOperationException("An empty live structure resurrected historical containers.");
    }

    private static void VerifyExistingHelperLinksAreSkipped()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vibn-helper-skip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Helper.xml");
            var element = CurrentContainer("Helper", "Enable", "%Q0.0"); element.Element("Type")!.Value = "ReturnCircuit";
            element.Descendants("Slot").Single().Value = "PLC_OUT_Signal";
            ContainerFileXml.Document([element]).Save(path);
            var plan = new ContainerToFeeVisualPlanService().LoadXmlAsync(path).GetAwaiter().GetResult().Plan!;
            var node = plan.Nodes.Single(item => item.Kind == VisualNodeKind.Container);
            var runtime = new SimpleNot_Container(); runtime.StoreContainerInformation(element, "Helper");
            var signal = runtime.EnumerateAssignedSignals().Single();
            var request = new SignalResolutionRequest(node.Id, "Helper", signal,
                plan.Nodes.Single(item => item.Kind == VisualNodeKind.Signal).Id, "PLC_OUT_Signal");
            var helper = new FeeSimpleNot { Name = "Helper" };
            var links = new[] { new VisualFeeSignalLink(signal.GuidString, helper.GuidString, "BoolNot", "Input 01", false) };
            var assembly = typeof(VisualPlan).Assembly;
            var linkerType = assembly.GetType("VIBN_Tools.ContainerToFeeVisual.ExistingSignalEndpointLinker")!;
            var linker = linkerType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single()
                .Invoke([links, new FeeAbstractObject[] { helper }, Array.Empty<VisualFeeContainerObject>(),
                    new HashSet<Guid> { signal.Guid }, Activator.CreateInstance(assembly.GetType("VIBN_Tools.ContainerToFeeVisual.VisualPlanLogger")!, nonPublic: true)!]);
            var boundType = assembly.GetType("VIBN_Tools.ContainerToFeeVisual.BoundVisualContainer")!;
            var bound = boundType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(constructor => constructor.GetParameters().Length == 2).Invoke([runtime, node]);
            var issues = new List<VisualIssue>();
            var linkMethod = linkerType.GetMethod("LinkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (var index = 0; index < 2; index++)
                ((Task)linkMethod.Invoke(linker, [bound, new[] { request }, new FeeAbstractObject[] { helper }, issues, CancellationToken.None])!).GetAwaiter().GetResult();
            if ((int)linkerType.GetProperty("LinkedCount")!.GetValue(linker)! != 0 ||
                (int)linkerType.GetProperty("SkippedCount")!.GetValue(linker)! != 2 || issues.Count != 0)
                throw new InvalidOperationException("Existing helper endpoints were rewritten or treated as errors.");
            // A missing helper must warn and return before any SDK access.
            ((VIBN_Tools.GlobalClasses.Interfaces.ISimObjectOwner)runtime).AssignSignalsAsync(new FeeInterface()).GetAwaiter().GetResult();
            var move = new SimpleMove_Container { Signal_PlcOutSignal = signal };
            ((VIBN_Tools.GlobalClasses.Interfaces.ISimObjectOwner)move).AssignSignalsAsync(new FeeInterface()).GetAwaiter().GetResult();
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void VerifyReverseSignalCentersContainer()
    {
        var root = RootFixture("Center", 180);
        var vm = new Fee2ContainerPageVM { SelectedRoot = root };
        vm.Roots.Add(root);
        var page = new Fee2ContainerPage { DataContext = vm };
        var window = new Window { Content = page, Width = 1050, Height = 650 };
        try
        {
            window.Show(); PumpDispatcher(TimeSpan.FromMilliseconds(100));
            var grid = FindVisualChildren<DataGrid>(page).Single(item =>
                BindingOperations.GetBinding(item, ItemsControl.ItemsSourceProperty)?.Path?.Path == nameof(Fee2ContainerPageVM.FoundContainersView));
            var selected = vm.FoundSignals[120];
            vm.SelectedFoundSignal = selected;
            PumpDispatcher(TimeSpan.FromMilliseconds(200));
            var target = vm.FoundContainers.Single(item => item.Id == selected.ContainerId);
            var row = grid.ItemContainerGenerator.ContainerFromItem(target) as DataGridRow;
            var viewer = FindVisualChildren<ScrollViewer>(grid).First();
            if (!target.IsRelatedToSelection || !ReferenceEquals(vm.ContainerRevealTarget, target) || row is null || viewer.VerticalOffset <= 0)
                throw new InvalidOperationException("Selecting a signal did not reveal its container.");
            var center = row.TransformToAncestor(viewer).Transform(new Point(0, row.ActualHeight / 2d)).Y;
            if (Math.Abs(center - viewer.ActualHeight / 2d) > row.ActualHeight * 2d)
                throw new InvalidOperationException("The related container row was revealed but not centered.");
            typeof(Fee2ContainerPageVM).GetMethod("ClearDiscoveredRoots", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
            PumpDispatcher(TimeSpan.FromMilliseconds(50));
            if (vm.Roots.Count != 0 || vm.FoundContainers.Count != 0 || vm.FoundSignals.Count != 0 || vm.NonContainerObjects.Count != 0 || vm.SelectedRoot is not null)
                throw new InvalidOperationException("Starting a new root read left old presentation data visible.");
        }
        finally { window.Close(); }
    }
}
