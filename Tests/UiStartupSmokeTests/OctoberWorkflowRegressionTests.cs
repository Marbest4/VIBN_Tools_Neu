using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Xml.Linq;
using VIBN_Tools.Application.Behaviors;
using VIBN_Tools.Application.View;
using VIBN_Tools.Application.VM;
using VIBN_Tools.ContainerGeneration.Models;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.GlobalClasses.FeeObjects;
using VIBN_Tools.ModelValidation;

namespace VIBN_Tools.UiStartup.SmokeTests;

internal static partial class Program
{
    private static void VerifyOctoberWorkflowRegressions(string directory)
    {
        VerifyWorkstationColumnReset();
        VerifyContentElementDragParent();
        VerifyAssembliesParentScope();
        VerifyValidationColourFilters();
        VerifyPhysicalRoleCannotBypassNames();
        VerifyReverseGroupingAndEditIsolation();
        VerifyCombinedReverseExport();
        VerifyDeletedTreeIdentity(directory);
    }

    private static void VerifyWorkstationColumnReset()
    {
        var page = new ViCoSearchPage();
        var grid = (DataGrid)typeof(ViCoSearchPage).GetField("WorkstationGrid", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
        var original = grid.Columns.ToArray();
        grid.FrozenColumnCount = 0;
        foreach (var column in original.Reverse()) column.DisplayIndex = 0;
        grid.FrozenColumnCount = 2;
        typeof(ViCoSearchPage).GetMethod("RestoreDefaultColumnLayout", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);
        if (grid.FrozenColumnCount != 2 || original.Where((column, index) => column.DisplayIndex != index).Any())
            throw new InvalidOperationException("Workstation view reset did not restore valid order and frozen columns.");
        grid.Columns.Remove(original[^1]);
        typeof(ViCoSearchPage).GetMethod("RestoreDefaultColumnLayout", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);
        typeof(ViCoSearchPage).GetMethod("OnApplicationExit", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [null, null]);
    }

    private static void VerifyContentElementDragParent()
    {
        var parent = typeof(ContainerToFeeVisualDragDropBehavior).GetMethod("GetSafeParent", BindingFlags.Static | BindingFlags.NonPublic)!;
        var run = new Run("Name"); var span = new Span(run); var block = new TextBlock(span);
        if (!ReferenceEquals(parent.Invoke(null, [run]), span) || !ReferenceEquals(parent.Invoke(null, [span]), block) ||
            parent.Invoke(null, [new Run("detached")]) is not null)
            throw new InvalidOperationException("Content-element drag traversal failed or touched the visual API for a Run.");
        var value = new object(); run.DataContext = value;
        var owner = new ListBox();
        var resolve = typeof(ContainerToFeeVisualDragDropBehavior).GetMethod("ResolveItem", BindingFlags.Static | BindingFlags.NonPublic)!;
        if (!ReferenceEquals(resolve.Invoke(null, [owner, run]), value))
            throw new InvalidOperationException("A Run's item identity could not be resolved safely.");
    }

    private static void VerifyAssembliesParentScope()
    {
        var plant = new FeeBasicFrame { Name = "Plant" };
        var assemblies = new FeeBasicFrame { Name = "Assemblies", Parent = plant };
        var logical = new FeeBasicFrame { Name = "Module_A", Parent = assemblies };
        var direct = new FeeBasicFrame { Name = "CAD_Detail", Parent = logical };
        var obj = new FeeSurface { Name = "Surface", Parent = direct };
        var discovery = typeof(VisualFeeObject).Assembly.GetType("VIBN_Tools.ContainerToFeeVisual.FeeSimObjectDiscovery")!;
        var resolve = discovery.GetMethod("ResolveParentScope", BindingFlags.NonPublic | BindingFlags.Static)!;
        var scope = (ITuple)resolve.Invoke(null, [obj])!;
        if ((string)scope[0]! != logical.GuidString || (string)scope[1]! != "Module_A" || (string)scope[2]! != "Plant")
            throw new InvalidOperationException("Parent scope used a direct CAD parent instead of the first frame below Assemblies.");
        var sameParent = new FeeSurface { Name = "Surface", Parent = new FeeBasicFrame { Name = "Other_Detail", Parent = logical } };
        var otherParent = new FeeSurface { Name = "Surface", Parent = new FeeBasicFrame { Name = "Module_B", Parent = assemblies } };
        var identity = discovery.GetMethod("CreateIdentity", BindingFlags.NonPublic | BindingFlags.Static)!;
        if (!Equals(identity.Invoke(null, [obj]), identity.Invoke(null, [sameParent])) || Equals(identity.Invoke(null, [obj]), identity.Invoke(null, [otherParent])))
            throw new InvalidOperationException("Duplicate classification did not respect the logical Assemblies parent.");
        var model = (VisualFeeObject)typeof(VisualFeeObject).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke(["fee:test", obj.GuidString, obj.Name, typeof(FeeSurface).FullName!, "Surface", Array.Empty<string>(), logical.GuidString, logical.Name, true, plant.Name, false]);
        var row = new ContainerToFeeVisualFeeObjectVM(model, null, new VisualFeeObjectConnectionSummary(true, []));
        if (row.HasError || !row.HasWarning || !row.ParentDetails.Contains("Plant"))
            throw new InvalidOperationException("An equal-parent duplicate became an error or omitted Assemblies parent details.");
        direct.Parent = plant;
        scope = (ITuple)resolve.Invoke(null, [obj])!;
        if ((string)scope[1]! != "Plant") throw new InvalidOperationException("Missing Assemblies did not fall back to the topmost node.");
        plant.Parent = direct; // Invalid ancestry must terminate rather than hanging discovery.
        _ = resolve.Invoke(null, [obj]);
    }

    private static void VerifyValidationColourFilters()
    {
        var clean = new FeeAbstractObject { Name = "Clean" };
        var warning = new FeeAbstractObject { Name = "Warning", PlausibilityIssues = [new("Warning", Severity.Warning)] };
        var error = new FeeAbstractObject { Name = "Error", PlausibilityIssues = [new("Warning", Severity.Warning), new("Error", Severity.Error)] };
        var group = new ValidationGroupViewModel(new ObservableCollection<FeeAbstractObject>([clean, warning, error]));
        foreach (var (filter, expected) in new[] { (ValidationColorFilter.Clean, clean), (ValidationColorFilter.Warning, warning), (ValidationColorFilter.Error, error) })
        {
            group.ApplyFilter("", filter);
            if (!ReferenceEquals(group.ItemsView.Cast<FeeAbstractObject>().Single(), expected))
                throw new InvalidOperationException("Validation colour filters overlap or omit their intended status.");
        }
        group.ApplyFilter("Clean", ValidationColorFilter.Error);
        if (group.HasItems) throw new InvalidOperationException("Text and colour filters were not combined.");
        group.ApplyFilter("", ValidationColorFilter.All);
        if (group.ItemsView.Cast<object>().Count() != 3) throw new InvalidOperationException("All did not restore the full validation group.");
    }

    private static XElement EditingContainer(string name, Guid objectGuid, Guid signalGuid) => new("Container",
        new XAttribute("id", name), new XElement("Component", name), new XElement("Type", "Sensor"),
        new XElement("DataList", new XElement("Entry", new XAttribute("feeGuid", signalGuid.ToString("D")),
            new XElement("ID", name + ".S1"), new XElement("Address", "%I0.0"), new XElement("DataType", "Bool"),
            new XElement("Signal", name + ".Detected"), new XElement("Slot", "PLC_IN"), new XElement("Note", ""))),
        new XElement("SimObjects", ContainerFileXml.Object(objectGuid.ToString("D"), name, "SafetySensor",
            slots: [new() { Name = "Output", AssignedGuid = signalGuid.ToString("D") }])));

    private static void VerifyPhysicalRoleCannotBypassNames()
    {
        var container = EditingContainer("Container_A", Guid.NewGuid(), Guid.NewGuid());
        container.Element("SimObjects")!.ReplaceNodes(Enumerable.Range(0, 120).Select(index =>
            ContainerFileXml.Object(Guid.NewGuid().ToString("D"), "Other" + index, "Surface", index % 2 == 0 ? "Primary" : "TechnicalHelper")));
        var document = ContainerFileXml.Document([container]);
        var snapshot = new FeeContainerProvenanceSnapshot(new Dictionary<string, string>(), document, [], 1, 1, "test");
        var editor = new Fee2ContainerRootEditor(new Fee2ContainerRoot(Guid.NewGuid(), "Root", snapshot, 0, 0, 0, 0));
        if (editor.CreateObjectAssociations().Count != 0 || editor.NonContainerObjects.Count != 120)
            throw new InvalidOperationException("Stored Primary/TechnicalHelper roles automatically assigned differently named physical objects.");
    }

    private static void VerifyReverseGroupingAndEditIsolation()
    {
        var firstGuid = Guid.NewGuid(); var secondGuid = Guid.NewGuid(); var signalGuid = Guid.NewGuid();
        var document = ContainerFileXml.Document([EditingContainer("A", firstGuid, signalGuid), EditingContainer("B", secondGuid, Guid.NewGuid())]);
        var snapshot = new FeeContainerProvenanceSnapshot(new Dictionary<string, string>(), document, [], 2, 2, "test");
        var root = new Fee2ContainerRootSelectionVM(new Fee2ContainerRoot(Guid.NewGuid(), "Root", snapshot, 0, 0, 0, 0));
        // Preserve the variable identity in the detached edit fixture.
        root.ApplyEditor(Fee2ContainerRootEditor.FromDocument(root.Root, document));
        var edit = new Fee2ContainerEditVM(root);
        edit.Settings.GroupByComponent = false;
        edit.GroupCommand.Execute(null);
        if (edit.Editor.Containers.Count != 1 || edit.Editor.Signals.Count != 2 || root.Editor.Containers.Count != 2 ||
            edit.Editor.CreateObjectAssociations().Count != 2)
            throw new InvalidOperationException("Grouping lost objects/signals or mutated the root before Apply.");
        var grouped = edit.Editor.CreateSnapshot();
        if (grouped.SignalBindings.Count != 2 || !grouped.ContainerDocument.Descendants("Slot").Any(item => item.Attribute("assignedGuid")?.Value == signalGuid.ToString("D")) ||
            grouped.ContainerDocument.Descendants("SimObject").Where(item => item.Element("Name")?.Value != grouped.ContainerDocument.Descendants("Component").Single().Value)
                .Any(item => item.Attribute("assignment")?.Value != "Manual"))
            throw new InvalidOperationException("Grouping dropped GUIDs/slot routes or failed to preserve deliberate differently named associations.");
        edit.Editor.Signals[0].IsIncluded = false;
        var roundTrip = Fee2ContainerRootEditor.FromDocument(root.Root, edit.Editor.CreateSnapshot(includeExcluded: true).ContainerDocument);
        if (roundTrip.Signals.Count != 2 || roundTrip.Signals.Count(item => item.IsIncluded) != 1 || roundTrip.CreateSnapshot().SignalBindings.Count != 1)
            throw new InvalidOperationException("Editing or export ignored the signal export checkbox.");
        edit.Xml = "<broken>";
        if (edit.ApplyXmlIfNeeded()) throw new InvalidOperationException("Invalid XML silently applied an outdated edit session.");
        var split = FeeContainerFileGrouping.Group(document, new ContainerGenerationSettings { GroupById = true, RegexId = @"^([^.]+)" });
        if (split.Descendants("Entry").Where(entry => !string.IsNullOrWhiteSpace(entry.Element("Signal")?.Value)).Count() != 2 ||
            split.Descendants("SimObject").Select(item => item.Element("Guid")?.Value).Distinct().Count() != 2)
            throw new InvalidOperationException("ID grouping lost signal or physical object identities.");
        var multipleSignals = new XDocument(document);
        var originalContainer = multipleSignals.Descendants("Container").First();
        var additional = new XElement(originalContainer.Element("DataList")!.Element("Entry")!);
        additional.SetElementValue("ID", "Split.S2"); additional.SetElementValue("Signal", "SplitDetected");
        additional.SetAttributeValue("feeGuid", Guid.NewGuid().ToString("D")); originalContainer.Element("DataList")!.Add(additional);
        var splitObjects = FeeContainerFileGrouping.Group(multipleSignals, new ContainerGenerationSettings { GroupById = true, RegexId = @"^([^.]+)" });
        if (splitObjects.Descendants("SimObject").Count(item => item.Element("Guid")?.Value == firstGuid.ToString("D")) != 1 ||
            !splitObjects.Descendants("Container").Any(item => item.Attribute("export")?.Value == "false" && ContainerFileXml.Objects(item).Any()))
            throw new InvalidOperationException("Splitting one container duplicated or silently exported an ambiguous physical-object assignment.");

    }

    private static void VerifyCombinedReverseExport()
    {
        var sourceGuid = Guid.NewGuid();
        var firstDocument = ContainerFileXml.Document([EditingContainer("A", Guid.NewGuid(), Guid.NewGuid()), EditingContainer("B", Guid.NewGuid(), Guid.NewGuid())]);
        var secondDocument = ContainerFileXml.Document([EditingContainer("A", Guid.NewGuid(), sourceGuid)]);
        Fee2ContainerRootSelectionVM Root(XDocument document, string name)
        {
            var snapshot = new FeeContainerProvenanceSnapshot(new Dictionary<string, string>(), document, [], document.Descendants("Container").Count(), 0, name);
            var root = new Fee2ContainerRootSelectionVM(new Fee2ContainerRoot(Guid.NewGuid(), name, snapshot, 0, 0, 0, 0));
            root.ApplyEditor(Fee2ContainerRootEditor.FromDocument(root.Root, document));
            root.IsSelected = true;
            return root;
        }
        var first = Root(firstDocument, "First"); var second = Root(secondDocument, "Second"); var skipped = Root(secondDocument, "Skipped");
        skipped.IsSelected = false;
        first.Editor.Containers.Last().IsIncluded = false;
        first.Editor.Signals.First().IsIncluded = false;
        var selected = new[] { first, second, skipped }.Where(root => root.IsSelected).Select(root => root.CreateEditedRoot());
        var export = new Fee2ContainerService().CreateCombinedExportAsync(selected).GetAwaiter().GetResult();
        if (export.Snapshot.ContainerCount != 2 || export.Snapshot.SignalCount != 1 || export.Snapshot.SignalBindings.Count != 1 ||
            export.Snapshot.SignalBindings.Single().ContainerIndex != 1 || export.Snapshot.SignalBindings.Single().VariableGuid != sourceGuid ||
            export.Snapshot.ContainerDocument.Descendants("Container").Select(item => item.Attribute("id")?.Value).Distinct().Count() != 2 ||
            export.Snapshot.ContainerDocument.Descendants("Container").SelectMany(ContainerFileXml.Objects).Count() != 2)
            throw new InvalidOperationException("Combined export lost root attribution, GUIDs or per-root container/signal export selections.");
    }

    private static void VerifyDeletedTreeIdentity(string directory)
    {
        var path = Path.Combine(directory, "Deletion.xml");
        var document = ContainerFileXml.Document([EditingContainer("A", Guid.NewGuid(), Guid.NewGuid())]); document.Save(path);
        var service = new ContainerToFeeVisualPlanService();
        if (!service.LoadXmlAsync(path).GetAwaiter().GetResult().Success) throw new InvalidOperationException("Deletion fixture did not load.");
        var vm = new ContainerToFeeVisualPageVM(service);
        var root = vm.TreeRoots.Single();
        var container = root.SelfAndDescendants().First(item => item.Kind == VisualNodeKind.Container);
        var signal = root.SelfAndDescendants().First(item => item.Kind == VisualNodeKind.Signal);
        vm.SelectedTreeNode = signal;
        var resets = 0;
        vm.TreeRoots.CollectionChanged += (_, _) => resets++;
        vm.DeleteTreeNodeCommand.Execute(signal);
        if (resets != 0 || !ReferenceEquals(vm.TreeRoots.Single(), root) ||
            !root.SelfAndDescendants().Contains(container) || root.SelfAndDescendants().Any(item => item.Id == signal.Id))
            throw new InvalidOperationException("Tree deletion rebuilt the list instead of preserving surviving nodes and viewport anchors.");
    }
}
