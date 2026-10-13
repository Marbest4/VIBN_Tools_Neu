using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Xml.Linq;
using VIBN_Tools.Application.VM;
using VIBN_Tools.ContainerGeneration.BusinessLogic.ContainerData;
using VIBN_Tools.ContainerGeneration.BusinessLogic;
using VIBN_Tools.ContainerGeneration.BusinessLogic.RequirementsXml;
using VIBN_Tools.ContainerGeneration.Models;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.Core.Containers;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.UiStartup.SmokeTests;

internal static partial class Program
{
    private static void VerifyOptionalRequirementsRegressions()
    {
        VerifyDeleteWhileRenaming();
        VerifyOptionalRequirementsReconstruction();
        VerifySharedRequirementsAreOptional();
        VerifyUnknownAddressDoesNotAbortValidation();
    }

    private static void VerifyDeleteWhileRenaming()
    {
        var vm = new ContainerGenerationPageVM(); vm.Settings.AutoSaveEnabled = false;
        var container = new ContainerData { Component = "Rename" };
        container.DataList.Add(new ContainerEntry { Signal = "Keep", Slot = "PLC_IN" });
        vm.ContainerList.Add(container); vm.SelectedContainers = [container];
        var editor = new TextBox { Text = "Rename" };
        var grid = new DataGrid { ItemsSource = vm.ContainerList, CanUserDeleteRows = false };
        var host = new StackPanel(); host.Children.Add(editor); host.Children.Add(grid);
        var window = new Window { Content = host, Width = 500, Height = 250 };
        try
        {
            window.Show(); PumpDispatcher(TimeSpan.FromMilliseconds(60));
            editor.Focus(); Keyboard.Focus(editor);
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor), 0, Key.Delete)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = editor };
            vm.ContainerGrid_OnPreviewKeyDown(args);
            if (args.Handled || container.DataList.Count != 1 || vm.UnassignedEntries.Count != 0)
                throw new InvalidOperationException("Delete in the rename editor cleared the container or consumed text editing.");
            var editing = typeof(ContainerGenerationPageVM).GetMethod("IsEditingText", BindingFlags.Static | BindingFlags.NonPublic)!;
            var rich = new RichTextBox(); var run = new Run("inline"); var paragraph = new Paragraph(run); rich.Document.Blocks.Add(paragraph);
            if (!(bool)editing.Invoke(null, [run])! || !(bool)editing.Invoke(null, [new ComboBox()])!)
                throw new InvalidOperationException("Text editing through content elements or a ComboBox was not protected.");
            grid.Focus(); Keyboard.Focus(grid);
            args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(grid), 0, Key.Delete)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = grid };
            vm.ContainerGrid_OnPreviewKeyDown(args);
            if (!args.Handled || vm.UnassignedEntries.Count != 1)
                throw new InvalidOperationException("Delete outside an editor no longer performs the existing container action.");
        }
        finally { window.Close(); vm.OnViewUnloaded(); }
    }

    private static void VerifyOptionalRequirementsReconstruction()
    {
        var primary = Guid.NewGuid(); var joint = Guid.NewGuid(); var helper = Guid.NewGuid(); var variable = Guid.NewGuid();
        var req = new ContainerRequirementsCatalog(XDocument.Parse("""
            <AutoCreate><Components><Component name="Door" type="FeedSafetyDoor" minSignals="1" maxSignals="4">
              <Slots><Slot name="PLC_IN_InHomePos"/></Slots>
            </Component></Components></AutoCreate>
            """));
        var objects = new[] {
            new FeeContainerLiveObject(primary, "Door", "LogicObject", "Grob_Cylinder"),
            new FeeContainerLiveObject(joint, "Door", "MotionJoint", LinkedObjectGuids: [primary]),
            new FeeContainerLiveObject(helper, "Door", "BoolNot", LinkedObjectGuids: [primary]) };
        var variables = new[] { new FeeContainerLiveVariable(variable, "Home", "%I1.0", "", "Bool", "") };
        var assignments = new[] {
            new FeeContainerLiveAssignment(variable, primary, "PLC_IN_InHomePos"),
            new FeeContainerLiveAssignment(variable, helper, "Input 01") };
        var result = FeeContainerLiveReconstructor.Reconstruct(Guid.NewGuid(), "Root A", objects, variables, assignments, req);
        var container = result.Snapshot.ContainerDocument.Descendants("Container").Single();
        if (container.Element("Type")?.Value != "FeedSafetyDoor" || result.Snapshot.SignalCount != 1 ||
            result.ObjectAssociations.Count != 3 || result.ObjectAssociations.Single(item => item.ObjectGuid == helper).Role != "TechnicalHelper")
            throw new InvalidOperationException("Req type selection, provenance-free objects or internal helper-route deduplication failed.");
        var noReq = FeeContainerLiveReconstructor.Reconstruct(Guid.NewGuid(), "Root A", objects, variables, assignments);
        if (noReq.Snapshot.ContainerCount != 1 || noReq.Snapshot.ContainerDocument.Descendants("Type").Single().Value != "Cylinder")
            throw new InvalidOperationException("The standard catalog could not reconstruct the same scene without Req-XML.");

        var variants = XDocument.Parse("""
            <AutoCreate><Components>
              <Component name="Normal" type="Cylinder"><Slots><Slot name="PLC_IN_InHomePos">
                <Keygroup type="required"><KeySet><Key keep="true">Normal</Key></KeySet></Keygroup>
              </Slot></Slots></Component>
              <Component name="Door" type="FeedSafetyDoor"><Slots><Slot name="PLC_IN_InHomePos">
                <Keygroup type="required"><KeySet><Key keep="true">Home</Key></KeySet></Keygroup>
              </Slot></Slots></Component>
            </Components></AutoCreate>
            """);
        var matcher = new ContainerGenerator();
        var variantReq = new ContainerRequirementsCatalog(variants, (signal, types) => matcher.MatchRequirementTypes(variants, signal, types));
        var classified = FeeContainerLiveReconstructor.Reconstruct(Guid.NewGuid(), "Root", objects, variables, assignments, variantReq);
        if (classified.Snapshot.ContainerDocument.Descendants("Type").Single().Value != "FeedSafetyDoor")
            throw new InvalidOperationException("Shared required/exclude Req rules did not disambiguate the current live logic.");

        var otherPrimary = Guid.NewGuid(); var otherVariable = Guid.NewGuid();
        var second = FeeContainerLiveReconstructor.Reconstruct(Guid.NewGuid(), "Root B",
            [new FeeContainerLiveObject(otherPrimary, "Door", "LogicObject", "Grob_Cylinder")],
            [new FeeContainerLiveVariable(otherVariable, "Other", "%I2.0", "", "Bool", "")],
            [new FeeContainerLiveAssignment(otherVariable, otherPrimary, "PLC_IN_InHomePos")], req);
        if (second.Snapshot.SignalBindings.Single().VariableGuid != otherVariable ||
            second.ObjectAssociations.Any(item => item.ObjectGuid == primary) ||
            second.Snapshot.ContainerDocument.Descendants("Container").Single().Attribute("id")?.Value == container.Attribute("id")?.Value)
            throw new InvalidOperationException("Equal names in separate roots merged object or variable identities.");

        var unknown = FeeContainerLiveReconstructor.Reconstruct(Guid.NewGuid(), "Root", [objects[0]], variables,
            [new FeeContainerLiveAssignment(variable, primary, "NewLiveSlot")], req);
        if (unknown.Snapshot.SignalCount != 1 || unknown.Snapshot.ContainerDocument.Descendants("Slot").Single().Value != "NewLiveSlot" || unknown.Issues.Count == 0)
            throw new InvalidOperationException("A changed live slot was discarded instead of preserved with a warning.");
        var genericReq = new ContainerRequirementsCatalog(XDocument.Parse("""
            <AutoCreate><Components><Component name="Custom" type="CustomerLogic"><Slots><Slot name="NewLiveSlot"/></Slots></Component></Components></AutoCreate>
            """));
        var custom = FeeContainerLiveReconstructor.Reconstruct(Guid.NewGuid(), "Root",
            [new FeeContainerLiveObject(primary, "Custom", "LogicObject", "CustomerDefinition")], variables,
            [new FeeContainerLiveAssignment(variable, primary, "NewLiveSlot")], genericReq);
        if (custom.Snapshot.ContainerDocument.Descendants("Type").Single().Value != "CustomerLogic" || custom.Snapshot.SignalCount != 1)
            throw new InvalidOperationException("A uniquely matching custom Req type could not retain its live FEE definition.");
    }

    private static void VerifyUnknownAddressDoesNotAbortValidation()
    {
        var parent = new FeeInterface();
        var signal = new FeeInterfaceSignal { Tag = "Actual", Address = "A701", Path = "", IOTypeString = "Bool", ParentInterface = parent };
        parent.Signals = [signal];
        signal.CheckObjectIssuesAsync([]).GetAwaiter().GetResult();
        if (!signal.PlausibilityIssues.Any(issue => issue.Message.Contains("A701", StringComparison.Ordinal)))
            throw new InvalidOperationException("An ambiguous PLC address was neither reported nor safely skipped.");
    }

    private static void VerifySharedRequirementsAreOptional()
    {
        var previous = RequirementsProvider.RequirementsFile; var previousPath = RequirementsProvider.SourcePath;
        var directory = Path.Combine(Path.GetTempPath(), "vibn-optional-req-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ContainerGenerationPageVM? generation = null;
        try
        {
            var path = Path.Combine(directory, "Req.xml");
            File.WriteAllText(path, """
                <AutoCreate><Components><Component name="Customer" type="CustomerLogic">
                  <Slots><Slot name="CustomInput"/></Slots>
                </Component></Components><FilterList/></AutoCreate>
                """);
            var source = new RequirementsXml();
            if (!source.ReadFromFile(path).IsSuccess) throw new InvalidOperationException("The shared Req fixture could not be read.");
            RequirementsProvider.RequirementsFile = source; RequirementsProvider.SourcePath = path;
            var reverse = new Fee2ContainerPageVM();
            if (!reverse.SupportedContainerTypes.Contains("CustomerLogic") || !reverse.RequirementsDescription.Contains(path))
                throw new InvalidOperationException("FEE2Container did not reuse the loaded Req file or show its source.");
            reverse.UseRequirements = false;
            if (reverse.SupportedContainerTypes.Contains("CustomerLogic") || !reverse.SupportedContainerTypes.Contains("Sensor"))
                throw new InvalidOperationException("Disabling Req removed standard type detection or retained inactive definitions.");
            generation = new ContainerGenerationPageVM(); generation.Settings.AutoSaveEnabled = false;
            if (!ReferenceEquals(generation.RequirementsFile, source) || !generation.ComponentTypes.Contains("CustomerLogic"))
                throw new InvalidOperationException("Opening ContainerGeneration discarded the already loaded shared Req file.");
        }
        finally
        {
            generation?.OnViewUnloaded();
            RequirementsProvider.RequirementsFile = previous; RequirementsProvider.SourcePath = previousPath;
            Directory.Delete(directory, true);
        }
    }
}
