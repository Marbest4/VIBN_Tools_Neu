using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Xml.Linq;
using VIBN_Tools.Application;
using VIBN_Tools.Application.Behaviors;
using VIBN_Tools.Application.VM;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.Settings;

namespace VIBN_Tools.UiStartup.SmokeTests;

internal static partial class Program
{
    private static void VerifyFeeWorkflowRegressions()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vibn-fee-workflow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            VerifyConnectionSessionAndDisconnect(directory);
            VerifyReverseObjectBatchAssignment();
            VerifyTechnicalHelperSignalLinks(directory);
            VerifyPixelListReveal();
            VerifyPortableContainerObjects(directory);
            VerifySignalSourceWarnings();
            VerifyRootSwitchSnapshots();
            VerifyTreeVerticalReveal();
            VerifyDockedValidationAndWindowBounds();
            VerifyInvalidComparisonCanBeReviewed();
            VerifyOctoberWorkflowRegressions(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyConnectionSessionAndDisconnect(string directory)
    {
        var state = (Connected: true, Connecting: false);
        Func<(bool Connected, bool Connecting)> readState = () => state;
        var connection = (FeeConnectionService)typeof(FeeConnectionService)
            .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, [readState.GetType()], null)!
            .Invoke([readState]);
        // The test controls transitions explicitly, without a live SDK server.
        ((DispatcherTimer)typeof(FeeConnectionService).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(connection)!).Stop();
        var path = Path.Combine(directory, "Connection.xml");
        File.WriteAllText(path, "<ContainerFile><Container id='c'><Component>Axis</Component><Type>Cylinder</Type><DataList /></Container></ContainerFile>");
        var planService = new ContainerToFeeVisualPlanService();
        if (!planService.LoadXmlAsync(path).GetAwaiter().GetResult().Success)
            throw new InvalidOperationException("Connection regression fixture could not be loaded.");
        var visual = (ContainerToFeeVisualPageVM)typeof(ContainerToFeeVisualPageVM)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke([planService, connection, ApplicationLogService.Instance]);
        var reverse = (Fee2ContainerPageVM)typeof(Fee2ContainerPageVM)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke([new Fee2ContainerService(), connection]);
        var revision = connection.BeginModelValidationUpdate();
        if (visual.RefreshFeeObjectsCommand.CanExecute(null) || reverse.CanRefresh)
            throw new InvalidOperationException("A new session was enabled before Update Objects.");
        if (!connection.CompleteModelValidationUpdate(revision) ||
            !visual.RefreshFeeObjectsCommand.CanExecute(null) || !reverse.CanRefresh)
            throw new InvalidOperationException("Successful Update Objects did not enable both refresh actions.");

        var pendingRevision = connection.BeginModelValidationUpdate();
        connection.RequestIntentionalDisconnect();
        state = (false, false);
        InvokePrivate(connection, "CheckConnection");
        PumpDispatcher(TimeSpan.FromMilliseconds(50));
        if (File.Exists(planService.CurrentPlan!.SidecarPath))
            throw new InvalidOperationException("An intentional disconnect triggered automatic plan saving.");
        state = (true, false);
        InvokePrivate(connection, "CheckConnection");
        if (connection.CompleteModelValidationUpdate(pendingRevision) || reverse.CanRefresh)
            throw new InvalidOperationException("An update from the previous connection unlocked the new session.");
        revision = connection.BeginModelValidationUpdate();
        if (!connection.CompleteModelValidationUpdate(revision))
            throw new InvalidOperationException("The reconnected session could not be updated.");

        var feeSignal = new VisualFeeSignal(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            "Existing", "Signal", "%I0.0", "", "Bool", "Read");
        visual.AvailableFeeSignals.Add(new ContainerToFeeVisualFeeSignalVM(feeSignal, [], null));
        // An unexpected drop clears live state immediately and saves the plan.
        state = (false, false);
        InvokePrivate(connection, "CheckConnection");
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!File.Exists(planService.CurrentPlan.SidecarPath) && DateTime.UtcNow < deadline)
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
        if (!File.Exists(planService.CurrentPlan.SidecarPath) || visual.AvailableFeeSignals.Count != 0 ||
            visual.AvailableFeeObjects.Count != 0 || reverse.CanRefresh || visual.IsFeeSdkAborted)
            throw new InvalidOperationException("Connection loss did not clear, save and invalidate the current session.");
    }

    private static void VerifyReverseObjectBatchAssignment()
    {
        var first = new FeeContainerUnmappedObject(Guid.NewGuid(), "Axis", "MotionJoint", "Test");
        var second = first with { Guid = Guid.NewGuid() };
        var wrongName = first with { Guid = Guid.NewGuid(), Name = "Other" };
        var document = XDocument.Parse("<ContainerFile><Container id='c'><Component>Axis</Component><Type>Cylinder</Type><DataList /></Container></ContainerFile>");
        var snapshot = new FeeContainerProvenanceSnapshot(new Dictionary<string, string>(), document, [], 1, 0, "test");
        var root = new Fee2ContainerRootSelectionVM(new Fee2ContainerRoot(Guid.NewGuid(), "Root", snapshot, 0, 0, 0, 0,
            NonContainerObjects: [first, second, wrongName]));
        var viewModel = new Fee2ContainerPageVM { SelectedRoot = root };
        PumpDispatcher(TimeSpan.FromMilliseconds(30));
        var target = viewModel.FoundContainers.Single();
        var selected = viewModel.NonContainerObjects.Take(2).Cast<object>().ToArray();
        viewModel.AssignObjectToContainerCommand.Execute(new ContainerToFeeVisualDropRequest(selected, target));
        if (target.AssociatedObjectItems.Select(item => item.ObjectGuid).Distinct().Count() != 2 ||
            viewModel.NonContainerObjects.Count != 1)
            throw new InvalidOperationException("The reverse multi-selection lost a same-name object or failed to move the batch.");
        var removed = target.AssociatedObjectItems.First();
        viewModel.RemoveObjectAssociationCommand.Execute(removed);
        if (target.AssociatedObjectItems.Count != 1 ||
            !viewModel.NonContainerObjects.Any(item => item.Guid == removed.ObjectGuid))
            throw new InvalidOperationException("Removing an association did not restore its original GUID to the unmapped list.");
        selected = viewModel.NonContainerObjects.Cast<object>().ToArray();
        viewModel.AssignObjectToContainerCommand.Execute(new ContainerToFeeVisualDropRequest(selected, target));
        if (target.AssociatedObjectItems.Count != 3 || viewModel.NonContainerObjects.Count != 0)
            throw new InvalidOperationException("An explicitly assigned mixed-name batch was rejected or partially assigned.");
        if (root.CreateEditedRoot().Provenance!.ContainerDocument.Descendants("SimObject")
            .Any(item => item.Attribute("assignment")?.Value != "Manual"))
            throw new InvalidOperationException("Manual assignment intent was not persisted for a later root read.");
        var destination = new Fee2ContainerFoundContainerVM("destination", "Axis", "Cylinder", 0);
        root.Editor.Containers.Add(destination);
        viewModel.AssignObjectToContainerCommand.Execute(new ContainerToFeeVisualDropRequest(target.AssociatedObjectItems.First(), destination));
        if (target.AssociatedObjectItems.Count != 2 || destination.AssociatedObjectItems.Count != 1 ||
            root.CreateEditedRoot().ObjectAssociations!.Count != 3)
            throw new InvalidOperationException("Moving an existing association left a stale source association.");
    }

    private static void VerifyPortableContainerObjects(string directory)
    {
        var objectGuid = Guid.NewGuid(); var signalGuid = Guid.NewGuid();
        var path = Path.Combine(directory, "PortableObjects.xml");
        var container = new XElement("Container", new XAttribute("id", "button"),
            new XElement("Component", "ButtonContainer"), new XElement("Type", "Button"),
            new XElement("DataList", new XElement("Entry", new XAttribute("feeGuid", signalGuid.ToString("D")),
                new XElement("ID", "S1"), new XElement("Address", "%I0.0"), new XElement("DataType", "Bool"),
                new XElement("Signal", "Pressed"), new XElement("Slot", "PLC_IN_NO"), new XElement("Note", ""))),
            new XElement("SimObjects", VIBN_Tools.ContainerGeneration.Models.ContainerFileXml.Object(
                objectGuid.ToString("D"), "DifferentButtonName", "Button", "SimObject", "Button")));
        VIBN_Tools.ContainerGeneration.Models.ContainerFileXml.Document([container]).Save(path);
        var service = new ContainerToFeeVisualPlanService();
        var loaded = service.LoadXmlAsync(path).GetAwaiter().GetResult();
        var restored = loaded.Plan ?? throw new InvalidOperationException("Portable fixture did not create a plan.");
        if (!loaded.Success || restored.Assignments.Single().FeeObjectId != "fee:" + objectGuid.ToString("D") ||
            restored.SignalAssignments.Single().FeeSignalGuid != signalGuid.ToString("D"))
            throw new InvalidOperationException("Portable FEE GUIDs were not restored to the visual plan.");
        var exportedPath = Path.Combine(directory, "PortableExport.xml");
        service.SaveEffectiveContainerXmlAsync(exportedPath).GetAwaiter().GetResult();
        var workspace = VIBN_Tools.ContainerGeneration.Models.ContainerFileWorkspaceReader.Read(exportedPath);
        var button = workspace.Containers.Single();
        if (button.SimObjects.Single().Guid != objectGuid.ToString("D") || button.DataList.Single().FeeGuid != signalGuid.ToString("D"))
            throw new InvalidOperationException("Visual export/ContainerGeneration import dropped FEE identities.");
        var undo = VIBN_Tools.ContainerGeneration.Models.WorkspaceUndoState.Capture("test", workspace.Containers, [], []);
        button.SimObjects.Single().Name = "Edited";
        if (undo.Containers.Single().SimObjects.Single().Name != "DifferentButtonName" ||
            undo.Containers.Single().DataList.Single().FeeGuid != signalGuid.ToString("D"))
            throw new InvalidOperationException("Undo did not retain independent FEE objects and signal GUIDs.");
        var unknown = VIBN_Tools.GlobalClasses.FeeObjects.FeeObjectFactory.Create("NewSdkType", "Any", Guid.NewGuid().ToString("D"));
        if (unknown is null || unknown.FeeType != "NewSdkType")
            throw new InvalidOperationException("An unknown non-decoration SDK type was discarded.");
        var logicGuid = Guid.NewGuid(); var surfaceGuid = Guid.NewGuid(); var otherSurfaceGuid = Guid.NewGuid();
        var reconstructed = FeeContainerLiveReconstructor.Reconstruct(Guid.NewGuid(), "All objects",
            [new(logicGuid, "Axis", "LogicObject", "Grob_Cylinder"),
             new(surfaceGuid, "Axis", "Surface"), new(otherSurfaceGuid, "Axis", "Surface"),
             new(Guid.NewGuid(), "Axis", "NewSdkType"), new(Guid.NewGuid(), "Unmatched", "NewSdkType"),
             new(Guid.NewGuid(), "Axis", "Decoration")], [], []);
        if (reconstructed.InspectedObjectCount != 5 || reconstructed.ObjectAssociations.Count != 4 ||
            reconstructed.UnmappedObjects.Count != 1 ||
            reconstructed.Snapshot.ContainerDocument.Descendants("SimObject").Count() != 4)
            throw new InvalidOperationException("Reverse detection lost an unknown type, a same-name object, or included Decoration.");
    }

    private static void VerifyTechnicalHelperSignalLinks(string directory)
    {
        var path = Path.Combine(directory, "Helpers.xml");
        File.WriteAllText(path, """
            <ContainerFile><Container id="safe"><Component>Safe_1</Component><Type>ReturnCircuit</Type><DataList>
            <Entry><ID>OUT</ID><Address>%Q0.0</Address><DataType>Bool</DataType><Signal>Enable</Signal><Slot>PLC_OUT_Signal</Slot></Entry>
            </DataList></Container></ContainerFile>
            """);
        var service = new ContainerToFeeVisualPlanService();
        var plan = service.LoadXmlAsync(path).GetAwaiter().GetResult().Plan!;
        var container = plan.Nodes.Single(node => node.Kind == VisualNodeKind.Container);
        var signal = plan.Nodes.Single(node => node.Kind == VisualNodeKind.Signal);
        var helperGuid = Guid.NewGuid().ToString("D");
        var signalGuid = Guid.NewGuid().ToString("D");
        var helper = new VisualFeeContainerObject(helperGuid, "", VisualFeeContainerObjectKind.TechnicalHelper,
            "BoolNot", container.Id);
        SetPrivateField(service, "_feeContainerObjects", new[] { helper });
        SetPrivateField(service, "_feeSignalLinks", new[] { new VisualFeeSignalLink(signalGuid, helperGuid, "BoolNot", "Input 01", false) });
        SetPrivateField(service, "_hasDiscoveredFeeSignalLinks", true);
        if (!service.GetSignalConnectionState(signal.Id, signalGuid).IsVerified)
            throw new InvalidOperationException("A signal linked to its generated BoolNot helper was not recognized.");
        SetPrivateField(service, "_feeContainerObjects", new[] { helper with { ProvenanceContainerId = "another-container" } });
        if (service.GetSignalConnectionState(signal.Id, signalGuid).IsVerified)
            throw new InvalidOperationException("A BoolNot owned by another container was accepted based on its type alone.");
    }

    private static void VerifyPixelListReveal()
    {
        var list = new ListBox { ItemsSource = Enumerable.Range(0, 200).Select(index => $"Object {index}").ToArray() };
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(list, true);
        ListBoxRevealBehavior.SetIsEnabled(list, true);
        var window = new Window { Width = 400, Height = 300, Content = list, ShowInTaskbar = false };
        try
        {
            window.Show();
            ListBoxRevealBehavior.SetRevealItem(list, list.Items[170]);
            PumpDispatcher(TimeSpan.FromMilliseconds(100));
            var viewer = FindVisualChildren<ScrollViewer>(list).First();
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(170);
            var top = row.TransformToAncestor(viewer).Transform(new Point(0, 0)).Y;
            if (viewer.VerticalOffset < 1000 || Math.Abs(top - (viewer.ViewportHeight - row.ActualHeight) / 2) > row.ActualHeight * 2)
                throw new InvalidOperationException("Pixel-scrolled related items were not centered in the list.");
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset + 70);
            list.UpdateLayout();
            var offset = viewer.VerticalOffset;
            var input = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = row };
            row.RaiseEvent(input);
            list.SelectedItem = list.Items[170];
            ListBoxRevealBehavior.SetRevealItem(list, null);
            ListBoxRevealBehavior.SetRevealItem(list, list.Items[170]);
            PumpDispatcher(TimeSpan.FromMilliseconds(50));
            if (Math.Abs(viewer.VerticalOffset - offset) > 1)
                throw new InvalidOperationException("Selecting the source list changed its scroll position.");
            ListBoxRevealBehavior.SetIsEnabled(list, false);
            ListBoxRevealBehavior.SetRevealItem(list, list.Items[20]);
            PumpDispatcher(TimeSpan.FromMilliseconds(50));
            if (Math.Abs(viewer.VerticalOffset - offset) > 1)
                throw new InvalidOperationException("A disabled Sync checkbox still caused automatic scrolling.");
        }
        finally
        {
            window.Close();
        }
    }
}
