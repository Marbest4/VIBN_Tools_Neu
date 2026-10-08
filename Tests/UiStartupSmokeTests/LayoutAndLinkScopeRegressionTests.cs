using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Xml.Linq;
using VIBN_Tools.Application.Behaviors;
using VIBN_Tools.Application.View;
using VIBN_Tools.Application.VM;
using VIBN_Tools.ContainerGeneration.Models;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.UiStartup.SmokeTests;

internal static partial class Program
{
    private static void VerifyLayoutAndLinkScopeRegressions(string directory)
    {
        try { _ = new Size(-1, 2); throw new InvalidOperationException("Negative Size was accepted."); }
        catch (ArgumentException exception)
        {
            if (!WpfVirtualizationExceptionPolicy.IsRecoverable(exception) ||
                !WpfVirtualizationExceptionPolicy.IsRecoverable(new TargetInvocationException(exception)))
                throw new InvalidOperationException("A negative WPF layout size was not recoverable.");
        }
        if (WpfVirtualizationExceptionPolicy.IsRecoverable(new ArgumentException("width")))
            throw new InvalidOperationException("Unrelated argument errors were swallowed.");

        var document = ContainerFileXml.Document(Enumerable.Range(0, 110).Select(index =>
            EditingContainer("Viewport_" + index, Guid.NewGuid(), Guid.NewGuid())));
        foreach (var entry in document.Descendants("Entry")) entry.Element("Slot")!.Value = "PLC_IN_PartPresent_Ch1";
        var path = Path.Combine(directory, "Viewport.xml"); document.Save(path);
        var service = new ContainerToFeeVisualPlanService();
        _ = service.LoadXmlAsync(path).GetAwaiter().GetResult().Plan!;
        var vm = new ContainerToFeeVisualPageVM(service);
        var nodes = vm.TreeRoots.SelectMany(root => root.SelfAndDescendants()).Where(node => node.Kind == VisualNodeKind.Container).ToArray();
        if (!vm.SynchronizeRelatedSelections || nodes.Any(node => !node.IsGenerationSelected))
            throw new InvalidOperationException("Fresh tree synchronization or generation checkboxes did not start selected.");
        var rootIdentity = vm.TreeRoots.First();
        var resets = 0;
        vm.TreeRoots.CollectionChanged += (_, args) => { if (args.Action == NotifyCollectionChangedAction.Reset) resets++; };
        nodes[0].IsExpanded = false; vm.SelectedTreeNode = nodes[0];
        vm.DeselectAllCommand.Execute(null); vm.SelectAllCommand.Execute(null);
        if (nodes[0].IsExpanded || resets != 0 || !ReferenceEquals(rootIdentity, vm.TreeRoots.First()) ||
            !ReferenceEquals(nodes[0], vm.TreeRoots.SelectMany(root => root.SelfAndDescendants()).Single(node => node.Id == nodes[0].Id)))
            throw new InvalidOperationException("A selection/checkbox update expanded or recreated the tree.");

        var page = new ContainerToFeeVisualPage { DataContext = vm };
        var window = new Window { Content = page, Width = 1400, Height = 800 };
        var errors = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler handler = (_, args) => { errors.Add(args.Exception); args.Handled = true; };
        System.Windows.Application.Current.DispatcherUnhandledException += handler;
        try
        {
            window.Show(); PumpDispatcher(TimeSpan.FromMilliseconds(100));
            var tree = FindVisualChildren<TreeView>(page).Single();
            var viewer = FindVisualChildren<ScrollViewer>(tree).First();
            tree.Focus(); viewer.ScrollToVerticalOffset(viewer.ScrollableHeight / 2);
            PumpDispatcher(TimeSpan.FromMilliseconds(50));
            var offset = viewer.VerticalOffset;
            vm.DeselectAllCommand.Execute(null); PumpDispatcher(TimeSpan.FromMilliseconds(50));
            if (Math.Abs(viewer.VerticalOffset - offset) > 2)
                throw new InvalidOperationException("Changing checkboxes moved the tree viewport.");
            var selected = nodes[55]; vm.SelectedTreeNode = selected;
            PumpDispatcher(TimeSpan.FromMilliseconds(50));
            offset = viewer.VerticalOffset;
            var signal = selected.SelfAndDescendants().First(node => node.Kind == VisualNodeKind.Signal);
            vm.DeleteTreeNodeCommand.Execute(signal); PumpDispatcher(TimeSpan.FromMilliseconds(50));
            if (Math.Abs(viewer.VerticalOffset - offset) > 2 || !ReferenceEquals(rootIdentity, vm.TreeRoots.First()))
                throw new InvalidOperationException("Deleting a nested signal moved or rebuilt the tree.");
            for (var index = 0; index < 6; index++)
            {
                vm.TreeFilter = "Viewport_5"; PumpDispatcher(TimeSpan.FromMilliseconds(20));
                vm.TreeFilter = ""; selected.IsExpanded = !selected.IsExpanded;
                tree.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = Control.MouseDoubleClickEvent });
                PumpDispatcher(TimeSpan.FromMilliseconds(20));
            }
            if (errors.Count > 0) throw new InvalidOperationException("Filtering/collapsing/double-clicking raised a WPF layout error.", errors[0]);
        }
        finally { window.Close(); System.Windows.Application.Current.DispatcherUnhandledException -= handler; }

        vm.DeselectAllCommand.Execute(null);
        var signalResult = service.LinkExistingSignalsOnlyAsync().GetAwaiter().GetResult();
        var objectResult = service.LinkExistingAssignmentsOnlyAsync().GetAwaiter().GetResult();
        if (!signalResult.Success || signalResult.Issues.Count > 0 || !objectResult.Success || objectResult.Issues.Count > 0)
            throw new InvalidOperationException("Link-only with no selected containers touched FEE or validated unrelated containers.");

        VerifyTypeStructureReplacement(directory);
        VerifySelectedBindingIsolation(directory);
    }

    private static void VerifyTypeStructureReplacement(string directory)
    {
        var container = EditingContainer("TypeChange", Guid.NewGuid(), Guid.NewGuid());
        container.Element("Type")!.Value = "CabinetSwitch";
        container.Descendants("Entry").Single().Element("Slot")!.Value = "PLC_IN_NO1";
        var path = Path.Combine(directory, "TypeChange.xml"); ContainerFileXml.Document([container]).Save(path);
        var service = new ContainerToFeeVisualPlanService(); var plan = service.LoadXmlAsync(path).GetAwaiter().GetResult().Plan!;
        var id = plan.Nodes.Single(node => node.Kind == VisualNodeKind.Container).Id;
        var helpers = plan.Nodes.Where(node => node.Kind == VisualNodeKind.TechnicalHelper).Select(node => node.Id).ToArray();
        if (helpers.Length == 0 || !service.SetSignalOnlyContainerType(id, "Sensor") ||
            plan.Nodes.Any(node => node.Kind == VisualNodeKind.TechnicalHelper) || plan.Nodes.Count(node => node.Kind == VisualNodeKind.Logic) != 1)
            throw new InvalidOperationException("Changing CabinetSwitch retained obsolete helpers or duplicated primary logic.");
        var logic = plan.Nodes.Single(node => node.Kind == VisualNodeKind.Logic);
        if (plan.Edges.Count(edge => edge.Kind == VisualEdgeKind.SignalToSlot && edge.TargetId == logic.Id) != 1 ||
            plan.Edges.Any(edge => plan.FindNode(edge.SourceId) is null || plan.FindNode(edge.TargetId) is null))
            throw new InvalidOperationException("Changing the type retained stale edges or lost the signal-to-logic route.");
        if (!service.Undo() || !helpers.SequenceEqual(plan.Nodes.Where(node => node.Kind == VisualNodeKind.TechnicalHelper).Select(node => node.Id)) ||
            !service.Redo() || plan.Nodes.Any(node => node.Kind == VisualNodeKind.TechnicalHelper))
            throw new InvalidOperationException("Type change undo/redo did not restore or remove the original helper structure.");
    }

    private static void VerifySelectedBindingIsolation(string directory)
    {
        var good = EditingContainer("Selected", Guid.NewGuid(), Guid.NewGuid());
        good.Descendants("Entry").Single().Element("Slot")!.Value = "PLC_IN_PartPresent_Ch1";
        var bad = EditingContainer("Unselected", Guid.NewGuid(), Guid.NewGuid());
        bad.Descendants("Entry").Single().Element("Slot")!.Value = "INVALID_SLOT";
        var path = Path.Combine(directory, "LinkScope.xml"); ContainerFileXml.Document([good, bad]).Save(path);
        var service = new ContainerToFeeVisualPlanService(); var plan = service.LoadXmlAsync(path).GetAwaiter().GetResult().Plan!;
        var excluded = plan.Nodes.Single(node => node.Kind == VisualNodeKind.Container && node.Name == "Unselected");
        service.SetGenerationSelected(excluded.Id, false);
        var ids = plan.Nodes.Where(node => node.Kind == VisualNodeKind.Container && plan.IsGenerationSelected(node.Id)).Select(node => node.Id).ToHashSet();
        var binder = typeof(ContainerToFeeVisualPlanService).Assembly.GetType("VIBN_Tools.ContainerToFeeVisual.RuntimeVisualPlanBinder")!;
        var objects = new Dictionary<string, FeeAbstractObject>();
        foreach (var includeSignals in new[] { true, false })
        {
            var result = binder.GetMethod("Bind", BindingFlags.Static | BindingFlags.Public)!
                .Invoke(null, [plan, objects, null, false, false, ids, false, includeSignals])!;
            var type = result.GetType();
            if (!(bool)type.GetProperty("Success")!.GetValue(result)! ||
                ((System.Collections.IEnumerable)type.GetProperty("Containers")!.GetValue(result)!).Cast<object>().Count() != 1 ||
                ((System.Collections.IEnumerable)type.GetProperty("UnknownSignals")!.GetValue(result)!).Cast<object>().Any())
                throw new InvalidOperationException("Link binding included an unselected container or its invalid/unknown signals.");
        }
    }
}
