using System.Xml.Linq;
using VIBN_Tools.Application.VM;
using VIBN_Tools.ContainerGeneration.Models;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.UiStartup.SmokeTests;

internal static partial class Program
{
    private static void VerifyReverseEditingAndStructuralMembers()
    {
        if (FeeContainerSearch.Matches("!Motor", ["Motor 01", "Sensor"]) ||
            !FeeContainerSearch.Matches("!Motor", ["Scanner 01", "Sensor"]) ||
            !FeeContainerSearch.Matches("scanner,!Motor", ["SCANNER 01", "N/A"]) ||
            FeeContainerSearch.Matches("scanner,!Motor", ["Scanner Motor", "N/A"]) ||
            FeeContainerSearch.Matches("scanner,safety", ["Scanner 01"]) ||
            !FeeContainerSearch.Matches(" ! , ", ["Any"]))
            throw new InvalidOperationException("Reverse search inclusions/exclusions differ from workstation search.");
        VerifyManualReverseContainerRemoval();
        VerifyLiveStructuralMembers();
    }

    private static void VerifyManualReverseContainerRemoval()
    {
        var objectGuid = Guid.NewGuid(); var signalGuid = Guid.NewGuid();
        var document = ContainerFileXml.Document([]);
        var snapshot = new FeeContainerProvenanceSnapshot(new Dictionary<string, string>(), document, [], 0, 0, "");
        var root = new Fee2ContainerRoot(Guid.NewGuid(), "Manual", snapshot, 0, 0, 0, 0, false,
            NonContainerObjects: [new(objectGuid, "Scanner", "LogicObject", "Manual candidate")],
            ObjectDetails: [new(objectGuid, "Scanner", "LogicObject", "Root", "Sick_Scanner_MicroScan3", "Input01 ↔ Signal")],
            LiveVariables: [new(signalGuid, "ScannerReady", "%I10.0", "", "Bool", "Ready")],
            LiveAssignments: [new(signalGuid, objectGuid, "Input01")]);
        var selection = new Fee2ContainerRootSelectionVM(root);
        var page = new Fee2ContainerPageVM { SelectedRoot = selection };
        var candidate = page.NonContainerObjects.Single();
        if (candidate.TargetContainerType != "N/A" || !page.SupportedContainerTypes.Contains("N/A"))
            throw new InvalidOperationException("Undefined reverse container types cannot be selected as N/A.");
        page.AddObjectAsContainerCommand.Execute(candidate);
        var added = page.FoundContainers.Single();
        var exported = selection.Editor.CreateSnapshot().ContainerDocument.Descendants("Container").Single();
        if (added.OriginalSignalCount != 1 || page.FoundSignals.Single().VariableGuid != signalGuid || exported.Element("Type")!.Value != "N/A" ||
            ContainerFileXml.Objects(exported).Single().Element("LogicDefinitionName")?.Value != "Sick_Scanner_MicroScan3")
            throw new InvalidOperationException("Manual N/A container lost its current logic or signal routes.");
        page.RemoveContainerCommand.Execute(added);
        PumpDispatcher(TimeSpan.FromMilliseconds(30));
        if (page.FoundContainers.Count != 0 || page.FoundSignals.Count != 0 || selection.Editor.Containers.Count != 0 ||
            selection.Editor.CreateSnapshot(includeExcluded: true).ContainerCount != 0 ||
            page.NonContainerObjects.Single().Guid != objectGuid || page.LinkedUnassignedSignals.Single().Guid != signalGuid)
            throw new InvalidOperationException("Remove only disabled export or lost the retained live FEE data.");
        page.AddObjectAsContainerCommand.Execute(page.NonContainerObjects.Single());
        var restored = page.FoundContainers.Single(); restored.Component = "Renamed"; restored.Type = "Sensor";
        if (page.FoundSignals.Single().Container != "Renamed" || page.FoundSignals.Single().ContainerType != "Sensor" ||
            selection.Editor.CreateSnapshot().SignalBindings.Count != 1 || document.Descendants("Container").Any())
            throw new InvalidOperationException("Re-adding created duplicates, lost edit observers or mutated the source snapshot.");
    }

    private static void VerifyLiveStructuralMembers()
    {
        var cabinet = Guid.NewGuid(); var scanner = Guid.NewGuid(); var selector = Guid.NewGuid(); var fuse = Guid.NewGuid();
        var scannerSignal = Guid.NewGuid(); var helperSignal = Guid.NewGuid(); var selectorSignal = Guid.NewGuid(); var fuseSignal = Guid.NewGuid();
        string[] helperTypes = ["BoolAnd", "BoolAlwaysTrue", "BoolAnd10Inputs", "ConvertBoolToByte", "ConvertByteToBool",
            "BoolCopy5Outputs", "BoolOr", "BoolPositiveEdge"];
        var helpers = helperTypes.Select(_ => Guid.NewGuid()).ToArray();
        var wrongSurface = Guid.NewGuid(); var orphan = Guid.NewGuid();
        var objects = new List<FeeContainerLiveObject>
        {
            new(cabinet, "Cabinet", "Cabinet"),
            new(scanner, "Scanner", "LogicObject", "Sick_Scanner_MicroScan3"),
            new(selector, "RawName;%I0.0", "CabinetElement", CabinetDefinition: "Grob_3PositionSwitch", Label: "Selector", ParentGuid: cabinet),
            new(fuse, "Fuse", "CabinetElement", CabinetDefinition: "Fuse", ParentGuid: cabinet),
            new(wrongSurface, "WrongName", "Surface", LinkedObjectGuids: [scanner]),
            new(orphan, "Orphan", "BoolAlwaysTrue")
        };
        for (var index = 0; index < helpers.Length; index++)
            objects.Add(new(helpers[index], "Helper " + index, helperTypes[index],
                LinkedObjectGuids: index == helpers.Length - 1 ? [scanner, helpers[0]] : [helpers[index + 1]]));
        var variables = new FeeContainerLiveVariable[] { new(scannerSignal, "Ready", "%I0.0", "", "Bool", ""),
            new(helperSignal, "Enable", "%Q0.0", "", "Bool", ""), new(selectorSignal, "Position", "%I1.0", "", "Bool", ""),
            new(fuseSignal, "FuseOk", "%I2.0", "", "Bool", "") };
        var routes = new FeeContainerLiveAssignment[] { new(scannerSignal, scanner, "Ready"), new(helperSignal, helpers[0], "Input 01"),
            new(selectorSignal, selector, "NO1"), new(fuseSignal, fuse, "NO") };
        var result = FeeContainerLiveReconstructor.Reconstruct(Guid.NewGuid(), "Root", objects, variables, routes);
        var scannerXml = result.Snapshot.ContainerDocument.Descendants("Container").Single(item => item.Element("Type")!.Value == "N/A");
        if (result.Snapshot.ContainerCount != 3 || scannerXml.Descendants("Entry").Count() != 2 ||
            helpers.Any(guid => !result.ObjectAssociations.Any(item => item.ObjectGuid == guid && item.ContainerObjectGuid == scanner && item.Role == "TechnicalHelper")) ||
            result.ObjectAssociations.Count(item => item.ObjectGuid == cabinet && item.Role == "TechnicalHelper") != 2 ||
            result.ObjectAssociations.Any(item => item.ObjectGuid == wrongSurface) ||
            !result.UnmappedObjects.Any(item => item.Guid == orphan))
            throw new InvalidOperationException("Current helper chains, CabinetElement definitions, shared Cabinet or strict SimObject naming were not respected.");
        var live = objects.Select(item => item.Guid == scanner
            ? (FeeAbstractObject)new FeeLogic { Guid = scanner, Name = item.Name, LogicDefinitionName = item.LogicDefinitionName! }
            : new FeeAbstractObject { Guid = item.Guid, Name = item.Name, FeeType = item.FeeType }).ToArray();
        var projected = ProjectObjectAssociations(result.Snapshot, result, live);
        var root = new Fee2ContainerRoot(Guid.NewGuid(), "Root", result.Snapshot, 4, 0, 4, 0, false,
            NonContainerObjects: result.UnmappedObjects, ObjectAssociations: projected, LiveVariables: variables, LiveAssignments: routes,
            ObjectDetails: objects.Select(item => new FeeContainerObjectDetails(item.Guid, item.Name, item.FeeType,
                item.ParentGuid == cabinet ? "Cabinet" : "", item.LogicDefinitionName ?? "", "", ParentGuid: item.ParentGuid?.ToString("D") ?? "",
                CabinetDefinition: item.CabinetDefinition ?? "")).ToArray());
        var editor = new Fee2ContainerRootEditor(root);
        var roundTrip = Fee2ContainerRootEditor.FromDocument(root, editor.CreateSnapshot(includeExcluded: true).ContainerDocument);
        if (roundTrip.CreateObjectAssociations().Count(item => item.ObjectGuid == cabinet) != 1 ||
            helpers.Any(guid => roundTrip.CreateObjectAssociations().Count(item => item.ObjectGuid == guid) != 1) ||
            roundTrip.NonContainerObjects.Any(item => helpers.Contains(item.Guid) || item.Guid == cabinet))
            throw new InvalidOperationException("Round-trip discarded linked differently named helpers or shared Cabinets.");
        var switchContainer = roundTrip.Containers.Single(item => item.Type == "CabinetSwitch");
        roundTrip.RemoveContainer(switchContainer);
        if (roundTrip.NonContainerObjects.Any(item => item.Guid == cabinet) || roundTrip.CreateObjectAssociations().Count(item => item.ObjectGuid == cabinet) != 1)
            throw new InvalidOperationException("Removing one container removed a Cabinet still used by another container.");
    }
}
