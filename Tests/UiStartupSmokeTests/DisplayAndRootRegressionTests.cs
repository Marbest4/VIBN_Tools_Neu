using System.Windows;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using System.Xml.Linq;
using VIBN_Tools.Application.Behaviors;
using VIBN_Tools.Application.View;
using VIBN_Tools.Application.VM;
using VIBN_Tools.ContainerToFeeVisual;

namespace VIBN_Tools.UiStartup.SmokeTests;

internal static partial class Program
{
    private static void VerifySignalSourceWarnings()
    {
        var signal = new VisualFeeSignal(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            "PLC", "EqualName", "%I0.0", "", "Bool", "Read");
        var sameSource = signal with { GuidString = Guid.NewGuid().ToString("D") };
        var otherInterface = sameSource with { InterfaceGuidString = Guid.NewGuid().ToString("D") };
        var otherLocation = sameSource with { Address = "%I0.1" };
        var warning = new ContainerToFeeVisualFeeSignalVM(signal, [signal, sameSource], null);
        if (warning.HasError || !warning.HasWarning || warning.StateBackground != "#FFE8D9F3")
            throw new InvalidOperationException("An equal name from the same source must only warn.");
        foreach (var unique in new[] { otherInterface, otherLocation })
        {
            var row = new ContainerToFeeVisualFeeSignalVM(signal, [signal, unique], null);
            if (row.HasError || row.HasWarning || row.StateBackground != "#FFE8D9F3")
                throw new InvalidOperationException("A unique but unassigned signal was not purple.");
            var assigned = new ContainerToFeeVisualFeeSignalVM(signal, [signal, unique], null, ["verified-node"]);
            if (!assigned.IsAssigned || assigned.StateBackground != "#FFC6EFCE")
                throw new InvalidOperationException("A unique assigned signal was not green.");
        }
    }

    private static Fee2ContainerRootSelectionVM RootFixture(string name, int count)
    {
        var containers = Enumerable.Range(0, count).Select(index => new XElement("Container",
            new XAttribute("id", index), new XElement("Component", name + index), new XElement("Type", "Sensor"),
            new XElement("DataList", new XElement("Entry", new XElement("ID", index), new XElement("Signal", "S" + index),
                new XElement("Slot", "PLC_IN"), new XElement("Address", "%I0.0"), new XElement("DataType", "Bool"), new XElement("Note", "")))));
        var document = VIBN_Tools.ContainerGeneration.Models.ContainerFileXml.Document(containers);
        var snapshot = new FeeContainerProvenanceSnapshot(new Dictionary<string, string>(), document, [], count, count, name);
        return new Fee2ContainerRootSelectionVM(new Fee2ContainerRoot(Guid.NewGuid(), name, snapshot, 0, 0, 0, 0));
    }

    private static void VerifyRootSwitchSnapshots()
    {
        var large = RootFixture("Large", 180); var small = RootFixture("Small", 1);
        var vm = new Fee2ContainerPageVM { SelectedRoot = large };
        vm.Roots.Add(large); vm.Roots.Add(small);
        var page = new Fee2ContainerPage { DataContext = vm };
        var window = new Window { Content = page, Width = 1000, Height = 650 };
        var errors = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler errorHandler = (_, args) => { errors.Add(args.Exception); args.Handled = true; };
        System.Windows.Application.Current.DispatcherUnhandledException += errorHandler;
        try
        {
            window.Show(); PumpDispatcher(TimeSpan.FromMilliseconds(100));
            var grid = FindVisualChildren<DataGrid>(page).Single(item =>
                BindingOperations.GetBinding(item, ItemsControl.ItemsSourceProperty)?.Path?.Path == nameof(Fee2ContainerPageVM.FoundContainersView));
            var oldItems = vm.FoundContainers; var oldView = vm.FoundContainersView;
            FindVisualChildren<ScrollViewer>(grid).First().ScrollToBottom();
            vm.SelectedRoot = small;
            PumpDispatcher(TimeSpan.FromMilliseconds(50));
            if (ReferenceEquals(oldItems, vm.FoundContainers) || ReferenceEquals(oldView, vm.FoundContainersView) ||
                oldItems.Count != 180 || oldView.Cast<object>().Count() != 180 || vm.FoundContainers.Count != 1 || grid.Items.Count != 1)
                throw new InvalidOperationException("A root switch mutated the old virtualized view or failed to publish the new view.");
            for (var iteration = 0; iteration < 20; iteration++)
            {
                vm.SelectedRoot = large;
                PumpDispatcher(TimeSpan.FromMilliseconds(15));
                FindVisualChildren<ScrollViewer>(grid).First().ScrollToBottom();
                vm.SelectedRoot = small;
                PumpDispatcher(TimeSpan.FromMilliseconds(15));
            }
            vm.SelectedRoot = large; vm.SelectedRoot = small;
            PumpDispatcher(TimeSpan.FromMilliseconds(50));
            if (errors.Count > 0 || vm.FoundContainers.Count != 1 || vm.FoundSignals.Count != 1)
                throw new InvalidOperationException("Rapid root switches produced stale row indexes or stale details.", errors.FirstOrDefault());
        }
        finally
        {
            window.Close();
            System.Windows.Application.Current.DispatcherUnhandledException -= errorHandler;
        }
    }

    private static void VerifyTreeVerticalReveal()
    {
        var tree = new TreeView { Height = 180, Width = 300 };
        ScrollViewer.SetHorizontalScrollBarVisibility(tree, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(tree, ScrollBarVisibility.Auto);
        ScrollViewer.SetCanContentScroll(tree, false);
        ContainerToFeeVisualTreeSelectionBehavior.SetIsEnabled(tree, true);
        var items = Enumerable.Range(0, 40).Select(index => new TreeViewItem
        {
            Header = new TextBlock { Text = index + new string('X', 200), Width = 1600, Height = 24 }
        }).ToArray();
        foreach (var item in items) tree.Items.Add(item);
        var window = new Window { Content = tree, Width = 340, Height = 230 };
        try
        {
            window.Show(); PumpDispatcher(TimeSpan.FromMilliseconds(50));
            var viewer = FindVisualChildren<ScrollViewer>(tree).First();
            var horizontalJump = false;
            viewer.ScrollChanged += (_, args) => horizontalJump |= args.HorizontalOffset > 0.1;
            ContainerToFeeVisualTreeSelectionBehavior.SetSelectedItem(tree, items[30]);
            PumpDispatcher(TimeSpan.FromMilliseconds(50));
            items[30].BringIntoView();
            PumpDispatcher(TimeSpan.FromMilliseconds(50));
            if (horizontalJump || viewer.HorizontalOffset > 0.1 || viewer.VerticalOffset <= 0)
                throw new InvalidOperationException("An oversized tree header caused a horizontal jump or was not vertically revealed.");
            var offset = viewer.VerticalOffset;
            ContainerToFeeVisualTreeSelectionBehavior.SetRevealEnabled(tree, false);
            ContainerToFeeVisualTreeSelectionBehavior.SetSelectedItem(tree, items[0]);
            PumpDispatcher(TimeSpan.FromMilliseconds(50));
            if (Math.Abs(viewer.VerticalOffset - offset) > 1)
                throw new InvalidOperationException("Disabled Sync still moved the tree viewport.");
        }
        finally { window.Close(); }
    }

    private static void VerifyDockedValidationAndWindowBounds()
    {
        var column = new ColumnDefinition { Width = new GridLength(320), MinWidth = 100 };
        CollapsibleColumnBehavior.SetIsVisible(column, false);
        if (column.Width.Value != 0 || column.MinWidth != 0) throw new InvalidOperationException("Hidden validation consumes width.");
        CollapsibleColumnBehavior.SetIsVisible(column, true);
        if (column.Width.Value != 320 || column.MinWidth != 100) throw new InvalidOperationException("Reopening validation lost the manually resized width.");
        var window = new Window { Width = 16000, Height = 12000, MinWidth = 10000, MinHeight = 9000 };
        WindowWorkAreaBehavior.Attach(window);
        try
        {
            window.Show(); PumpDispatcher(TimeSpan.FromMilliseconds(50));
            if (!double.IsFinite(window.MaxWidth) || !double.IsFinite(window.MaxHeight) || window.MinWidth > window.MaxWidth ||
                window.MinHeight > window.MaxHeight || window.ActualWidth > window.MaxWidth + 1 || window.ActualHeight > window.MaxHeight + 1)
                throw new InvalidOperationException("Window size was not limited to the current display.");
        }
        finally { window.Close(); }
    }

    private static void VerifyInvalidComparisonCanBeReviewed()
    {
        var oldFile = VIBN_Tools.ContainerGeneration.Models.ContainerFileXml.Document([]);
        var vm = new ContainerFileComparisonVM(oldFile, "Alt", () => Task.FromResult(oldFile),
            (_, _) => Task.FromResult("Applied"), () => false);
        XElement Container() => new("Container", new XElement("Component", "Same"), new XElement("Type", "Sensor"), new XElement("DataList"));
        SetPrivateField(vm, "_newFile", VIBN_Tools.ContainerGeneration.Models.ContainerFileXml.Document([Container(), Container()]));
        typeof(ContainerFileComparisonVM).GetMethod("Rebuild", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [null]);
        if (vm.Changes.Count != 2 || vm.Diagnostics.Count != 2 || vm.Changes.Any(item => !item.HasErrors))
            throw new InvalidOperationException("Invalid files cannot be inspected occurrence by occurrence.");
        vm.SelectChangesCommand.Execute(null);
        if (vm.Changes.Any(item => !item.IsSelected)) throw new InvalidOperationException("Invalid entries cannot be marked for review.");
        var edit = XElement.Parse(vm.EditedXml); edit.Element("Component")!.Value = "Corrected";
        vm.EditedXml = edit.ToString(); vm.CommitEditsCommand.Execute(null);
        if (vm.Diagnostics.Count != 0 || vm.Changes.Any(item => item.HasErrors) || vm.Changes.Count != 2)
            throw new InvalidOperationException("A duplicate name could not be corrected without losing an occurrence.");
        vm.EditedXml = "<Container>"; vm.CommitEditsCommand.Execute(null);
        if (!vm.Status.Contains("ungültig") || vm.Changes.Count != 2)
            throw new InvalidOperationException("An invalid XML edit changed the last valid comparison.");
    }
}
