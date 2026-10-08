using System.Reflection;
using System.Xml.Linq;
using VIBN_Tools.Application.VM;
using VIBN_Tools.ContainerGeneration.Models;
using VIBN_Tools.ContainerToFee;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.UiStartup.SmokeTests;

internal static partial class Program
{
    private static void VerifyVisualLinkWorkflowRegressions()
    {
        VerifySlotLinkAcknowledgements();
        VerifyLinkDirectionAndInstanceGuids();
        VerifyLogicRootBindingAndSnapshotReuse();
        VerifySignalAddressAndPathMatching();
    }

    private static Task VerifySlotLinks(
        IReadOnlyList<(Guid ObjectGuid, string SlotName)> endpoints,
        Func<Guid, string, Task<IReadOnlyList<(string ObjectGuid, string[] SlotNames)>>> read,
        Func<Guid, string, Guid, string, Task<bool>> pair,
        Func<Guid[], string[], Task<bool>> group,
        CancellationToken token = default) =>
        (Task)typeof(ContainerSlotLinkService).GetMethod("AssignAndVerifyCoreAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [endpoints, "Regression", read, pair, group, token])!;

    private static void VerifySlotLinkAcknowledgements()
    {
        var source = Guid.NewGuid(); var target = Guid.NewGuid(); var retained = Guid.NewGuid();
        IReadOnlyList<(Guid ObjectGuid, string SlotName)> endpoints = [(source, "Out"), (target, "In")];
        Task<IReadOnlyList<(string ObjectGuid, string[] SlotNames)>> Reply(params (string ObjectGuid, string[] SlotNames)[] values) =>
            Task.FromResult<IReadOnlyList<(string ObjectGuid, string[] SlotNames)>>(values);
        Task<bool> UnexpectedGroup(Guid[] guids, string[] slots) => throw new InvalidOperationException("A single pair used the group SDK API.");

        // A reverse-only SDK read is enough to confirm a link, with either SDK acknowledgement.
        foreach (var accepted in new[] { true, false })
        {
            var sent = false; var writes = 0;
            Task<IReadOnlyList<(string ObjectGuid, string[] SlotNames)>> Read(Guid guid, string slot) =>
                sent && guid == target ? Reply((source.ToString("D"), ["Out"])) : Reply();
            Task<bool> Pair(Guid first, string firstSlot, Guid second, string secondSlot)
            {
                if (first != source || second != target || firstSlot != "Out" || secondSlot != "In")
                    throw new InvalidOperationException("The SDK write replaced the existing scene GUID or slot.");
                writes++; sent = true; return Task.FromResult(accepted);
            }
            VerifySlotLinks(endpoints, Read, Pair, UnexpectedGroup).GetAwaiter().GetResult();
            VerifySlotLinks(endpoints, Read, Pair, UnexpectedGroup).GetAwaiter().GetResult();
            if (writes != 1) throw new InvalidOperationException("An already confirmed reverse link was rewritten.");
        }

        // A fan-out update must include the already linked consumer.
        var groupSent = false; var groupWrites = 0;
        Task<IReadOnlyList<(string ObjectGuid, string[] SlotNames)>> ReadGroup(Guid guid, string slot) => guid == source
            ? groupSent ? Reply((retained.ToString("D"), ["OldIn"]), (target.ToString("D"), ["In"]))
                : Reply((retained.ToString("D"), ["OldIn"])) : Reply();
        Task<bool> Group(Guid[] guids, string[] slots)
        {
            if (guids.Length != 3 || !guids.Contains(retained) || !guids.Contains(target) || !slots.Contains("OldIn"))
                throw new InvalidOperationException("Adding a consumer discarded an existing scene endpoint.");
            groupWrites++; groupSent = true; return Task.FromResult(true);
        }
        VerifySlotLinks(endpoints, ReadGroup, (_, _, _, _) => throw new InvalidOperationException("Fan-out used the pair API."), Group)
            .GetAwaiter().GetResult();
        if (groupWrites != 1) throw new InvalidOperationException("The retained-endpoint group was not sent exactly once.");

        var failedWrites = 0; var failedReads = 0;
        Task<IReadOnlyList<(string ObjectGuid, string[] SlotNames)>> ReadEmpty(Guid guid, string slot) { failedReads++; return Reply(); }
        try
        {
            VerifySlotLinks(endpoints, ReadEmpty, (_, _, _, _) => { failedWrites++; return Task.FromResult(false); }, UnexpectedGroup)
                .GetAwaiter().GetResult();
            throw new InvalidOperationException("An unconfirmed SDK rejection was accepted.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("SDK-Rückgabe: False", StringComparison.Ordinal))
        {
            if (failedWrites != 1 || failedReads != 4 || !exception.Message.Contains(target.ToString("D"), StringComparison.Ordinal) ||
                !exception.Message.Contains("tatsächlich gelesen", StringComparison.Ordinal))
                throw new InvalidOperationException("A rejected link was retried unnecessarily or lost its actual endpoint diagnostics.");
        }

        using var cancellation = new CancellationTokenSource();
        var cancelledWrites = 0;
        Task<IReadOnlyList<(string ObjectGuid, string[] SlotNames)>> CancelRead(Guid guid, string slot) { cancellation.Cancel(); return Reply(); }
        try
        {
            VerifySlotLinks(endpoints, CancelRead, (_, _, _, _) => { cancelledWrites++; return Task.FromResult(true); }, UnexpectedGroup,
                cancellation.Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("Cancellation during preflight did not stop the write.");
        }
        catch (OperationCanceledException) { }
        if (cancelledWrites != 0) throw new InvalidOperationException("A cancelled link operation changed FEE.");
    }

    private static void VerifyLinkDirectionAndInstanceGuids()
    {
        var map = typeof(VisualPlan).Assembly.GetType("VIBN_Tools.ContainerToFeeVisual.SimObjectLinkMap")!;
        var logic = Guid.NewGuid();
        var sensor = new FeeSensor(); var surface = new FeeSurface();
        IReadOnlyList<(Guid ObjectGuid, string SlotName)> Endpoints(string type, FeeAbstractObject item)
        {
            var links = map.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [type, item, 0])!;
            return (IReadOnlyList<(Guid ObjectGuid, string SlotName)>)map.GetMethod("Endpoints", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [logic, links])!;
        }
        var sensorEndpoints = Endpoints("Sensor", sensor);
        var surfaceEndpoints = Endpoints("Conveyor", surface);
        if (sensorEndpoints.Count != 2 || sensorEndpoints[0] != (sensor.Guid, "Channel1") || sensorEndpoints[1].ObjectGuid != logic ||
            surfaceEndpoints.Count != 2 || surfaceEndpoints[0].ObjectGuid != logic || surfaceEndpoints[1] != (surface.Guid, "InVelocityX"))
            throw new InvalidOperationException("SimObject output/input direction or existing instance GUIDs changed.");
    }

    private static VisualFeeObject SnapshotObject(FeeAbstractObject item, FeeBasicFrame root) =>
        (VisualFeeObject)typeof(VisualFeeObject).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke(
            ["fee:" + item.GuidString, item.GuidString, item.Name, item.GetType().FullName!, item.FeeType,
             new[] { item.GetType().Name, item.GetType().FullName! }, root.GuidString, root.Name, false, "", false, root.GuidString, root.Name]);

    private static void VerifyLogicRootBindingAndSnapshotReuse()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vibn-link-roots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var element = CurrentContainer("Station", "Presence", "%I0.0");
            element.Element("Type")!.Value = "Sensor"; element.Descendants("Slot").Single().Value = "PLC_IN_PartPresent_Ch1";
            var path = Path.Combine(directory, "Roots.xml"); ContainerFileXml.Document([element]).Save(path);
            var service = new ContainerToFeeVisualPlanService(); var plan = service.LoadXmlAsync(path).GetAwaiter().GetResult().Plan!;
            var container = plan.Nodes.Single(node => node.Kind == VisualNodeKind.Container);
            var rootA = new FeeBasicFrame { Name = "Root A" }; var rootB = new FeeBasicFrame { Name = "Root B" };
            var sensor = new FeeSensor { Name = "Station", Parent = rootA };
            var first = new FeeLogic { Name = " Station ", LogicDefinitionName = "Grob_Sensor", LogicDefinitionGuid = Guid.NewGuid(), Parent = rootA };
            var second = new FeeLogic { Name = "Station", LogicDefinitionName = "Grob_Sensor", LogicDefinitionGuid = Guid.NewGuid(), Parent = rootB };
            var objects = new[] { SnapshotObject(sensor, rootA), SnapshotObject(first, rootA), SnapshotObject(second, rootB) };
            SetPrivateField(service, "_feeObjects", objects);
            SetPrivateField(service, "_runtimeObjects", objects.Zip(new FeeAbstractObject[] { sensor, first, second })
                .ToDictionary(pair => pair.First.Id, pair => pair.Second, StringComparer.Ordinal));
            SetPrivateField(service, "_runtimeSceneObjects", new FeeAbstractObject[] { rootA, rootB, sensor, first, second });
            SetPrivateField(service, "_hasDiscoveredFeeObjects", true);
            if (!service.TryAssign(plan.Targets.Single().Id, objects[0].Id).Success) throw new InvalidOperationException("Root fixture assignment failed.");
            service.SetSimObjectRoots([rootA.GuidString, rootB.GuidString]);
            if (service.FindExistingContainerLogics(container.Id).Single().GuidString != first.GuidString)
                throw new InvalidOperationException("Equal-name logics did not follow the assigned SimObject root.");
            var vm = new ContainerToFeeVisualPageVM(service);
            var treeLogic = vm.TreeRoots.SelectMany(root => root.SelfAndDescendants()).Single(node => node.Kind == VisualNodeKind.Logic);
            if (treeLogic.FeeObjectId != objects[1].Id || treeLogic.FeeObjectId == "fee:" + first.LogicDefinitionGuid)
                throw new InvalidOperationException("The logic tree used a definition GUID instead of the displayed scene instance.");
            vm.SelectedFeeObject = vm.AvailableFeeObjects.Single(item => item.Id == objects[1].Id);
            if (!ReferenceEquals(vm.SelectedTreeNode, treeLogic)) throw new InvalidOperationException("Selecting a displayed logic did not synchronize its tree node.");
            var presence = VisualFeeContainerPresenceResolver.Resolve(plan, [], service.FindExistingContainerLogics);
            if (presence[treeLogic.Id].Kind != VisualFeeNodePresenceKind.Found)
                throw new InvalidOperationException("The tree contradicted the root-resolved logic binding.");
            service.SetSimObjectRoots([rootB.GuidString]);
            if (service.FindExistingContainerLogics(container.Id).Single().GuidString != second.GuidString)
                throw new InvalidOperationException("An unselected-root logic remained cached.");

            var revision = Services.Connection?.ConnectionRevision ?? -1;
            SetPrivateField(service, "_feeObjectConnectionRevision", revision);
            SetPrivateField(service, "_feeInterfaceConnectionRevision", revision);
            SetPrivateField(service, "_hasDiscoveredFeeInterfaces", true);
            // No vendor session is needed: matching discovery revisions must reuse the displayed snapshot.
            ((Task)typeof(ContainerToFeeVisualPlanService).GetMethod("EnsureLinkSnapshotAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [true, CancellationToken.None])!).GetAwaiter().GetResult();
            service.SetSimObjectRoots([]);
            if (service.FindExistingContainerLogics(container.Id).Count != 0 ||
                !service.LinkExistingAssignmentsOnlyAsync().GetAwaiter().GetResult().Success ||
                !service.LinkExistingSignalsOnlyAsync().GetAwaiter().GetResult().Success || plan.Assignments.Count != 1)
                throw new InvalidOperationException("Deselected roots were linked, reread, or lost their stored assignments.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void VerifySignalAddressAndPathMatching()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vibn-link-sources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Signals.xml");
            ContainerFileXml.Document([CurrentContainer("Located", "Equal", "%E0.0"), CurrentContainer("Renamed", "OldTag", "%A2.1"),
                CurrentContainer("Symbolic", "PathTag", "DB1.Flag"), CurrentContainer("Ambiguous", "Duplicate", "%I4.0"),
                CurrentContainer("WrongType", "Typed", "%I5.0"), CurrentContainer("WrongSource", "Other", "%I9.0")]).Save(path);
            var service = new ContainerToFeeVisualPlanService(); var plan = service.LoadXmlAsync(path).GetAwaiter().GetResult().Plan!;
            var selected = new VisualFeeInterface(Guid.NewGuid().ToString("D"), "Selected", "Test", 7);
            var excluded = new VisualFeeInterface(Guid.NewGuid().ToString("D"), "Excluded", "Test", 1);
            VisualFeeSignal Signal(VisualFeeInterface parent, string tag, string address, string symbolic, string type = "Bool") =>
                new(Guid.NewGuid().ToString("D"), parent.GuidString, parent.Name, tag, address, symbolic, type, "Write");
            var located = Signal(selected, " Equal ", " I0.0 ", "Inputs.Switch");
            var renamed = Signal(selected, "NewTag", "%Q2.1", "Outputs.Valve", "BOOLEAN");
            var symbolic = Signal(selected, "PathTag", "", "DB1.Flag");
            SetPrivateField(service, "_feeSignals", new[] { located, renamed, symbolic, Signal(selected, "Duplicate", "%I4.0", "One"),
                Signal(selected, "Duplicate", "%I4.0", "Two"), Signal(selected, "Typed", "%I5.0", "Inputs.Typed", "Int"),
                Signal(selected, "Other", "%I8.0", "Inputs.Other"), Signal(excluded, "Equal", "%I0.0", "Inputs.Switch") });
            SetPrivateField(service, "_feeInterfaces", new[] { selected, excluded });
            SetPrivateField(service, "_hasDiscoveredFeeInterfaces", true);
            service.SetExistingInterfaces([selected]);
            if (service.AutoAssignMatches() != 3 || plan.SignalAssignments.Count != 3)
                throw new InvalidOperationException("Address/path aliases failed or ambiguous, wrong-source/type or unselected-interface signals were assigned.");
            string Assigned(string name) => plan.SignalAssignments.Single(assignment =>
                plan.FindNode(plan.FindNode(assignment.SignalNodeId)!.ContainerId!)!.Name == name).FeeSignalGuid;
            if (Assigned("Located") != located.GuidString || Assigned("Renamed") != renamed.GuidString || Assigned("Symbolic") != symbolic.GuidString)
                throw new InvalidOperationException("The current source was confused with the preferred display path.");
            var requested = new FeeInterfaceSignal { Tag = " Equal ", Address = "%E0.0", IOTypeString = "Bool", UsageString = "Write" };
            var existing = new FeeInterfaceSignal { Guid = Guid.Parse(located.GuidString), Tag = "Equal", Address = "I0.0", Path = "Inputs.Switch", IOTypeString = "Bool", UsageString = "Write" };
            var resolution = SignalResolutionPlanner.Build([new SignalResolutionRequest("container", "Located", requested)],
                [new FeeInterface { Name = "Selected", Signals = [existing] }]);
            if (!resolution.IsValid || resolution.ExistingBindings.Single().ExistingSignal.Guid != existing.Guid)
                throw new InvalidOperationException("Execution resolved a different signal than automatic assignment.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
