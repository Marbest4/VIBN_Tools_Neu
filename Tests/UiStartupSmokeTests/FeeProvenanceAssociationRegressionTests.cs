using System.Reflection;
using System.Xml.Linq;
using VIBN_Tools.Application.VM;
using VIBN_Tools.ContainerGeneration.Models;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.UiStartup.SmokeTests;

internal static partial class Program
{
    private static string ProvenanceIdentity(XElement container, int occurrence = 1) =>
        (string)typeof(ContainerToFeeVisualPlanService).Assembly
            .GetType("VIBN_Tools.ContainerToFeeVisual.ContainerXmlVisualPlanParser")!
            .GetMethod("CreateContainerId", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [container.Attribute("id")?.Value ?? "", container.Element("Component")!.Value,
                container.Element("Type")!.Value, occurrence])!;

    private static IReadOnlyList<FeeContainerObjectAssociation> ProjectObjectAssociations(
        FeeContainerProvenanceSnapshot snapshot, FeeContainerReconstructionResult classification,
        IReadOnlyList<FeeAbstractObject> objects) =>
        (IReadOnlyList<FeeContainerObjectAssociation>)typeof(ContainerToFeeVisualPlanService).Assembly
            .GetType("VIBN_Tools.ContainerToFeeVisual.FeeContainerAssociationProjection")!
            .GetMethod("Apply", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [snapshot, classification, objects])!;

    private static void VerifyProvenanceObjectIsolation()
    {
        var containers = Enumerable.Range(0, 130).Select(index =>
            EditingContainer("Container_" + index, Guid.NewGuid(), Guid.NewGuid())).ToArray();
        foreach (var container in containers) container.SetAttributeValue("id", "");
        containers[1].Attribute("id")!.Remove(); containers[2].SetAttributeValue("id", " ");
        containers[3].SetAttributeValue("id", "duplicate"); containers[4].SetAttributeValue("id", "duplicate");
        containers[5].SetAttributeValue("id", "fee-container:1");
        var identities = containers.Select(item => ProvenanceIdentity(item)).ToArray();
        var primaries = containers.Select(container => new FeeAbstractObject
        {
            Guid = Guid.Parse(ContainerFileXml.Objects(container).Single().Element("Guid")!.Value),
            Name = container.Element("Component")!.Value, FeeType = "LogicObject"
        }).ToArray();
        var expected = primaries.ToDictionary(item => item.Guid, item => item.Name);
        var signalSources = containers.Select((container, index) => (id: identities[index], source: new FeeContainerSignalSource(
            container.Element("DataList")!.Element("Entry")!.Element("ID")!.Value,
            container.Element("DataList")!.Element("Entry")!.Element("Signal")!.Value, "%I0.0", "Bool",
            Guid.Parse(container.Element("DataList")!.Element("Entry")!.Attribute("feeGuid")!.Value)))).ToArray();
        var live = primaries.ToList();
        // Stored names and roles do not override the actual name in FEE.
        foreach (var (type, role) in new[] { ("Surface", "SimObject"), ("LogicObject", "Primary"), ("MoveBit", "TechnicalHelper") })
        {
            var other = new FeeAbstractObject { Guid = Guid.NewGuid(), Name = "Other_" + type, FeeType = type };
            live.Add(other);
            containers[0].Element("SimObjects")!.Add(ContainerFileXml.Object(other.GuidString, primaries[0].Name, type, role));
        }
        var manual = new FeeAbstractObject { Guid = Guid.NewGuid(), Name = "ManuallyChosen", FeeType = "Surface" };
        live.Add(manual);
        containers[0].Element("SimObjects")!.Add(ContainerFileXml.Object(manual.GuidString, manual.Name, manual.FeeType, assignmentKind: "Manual"));
        expected.Add(manual.Guid, primaries[0].Name);
        // A previous generation may have no explicit object entries at all.
        foreach (var container in containers.Where((_, index) => index % 2 == 1)) container.Element("SimObjects")!.Remove();
        var document = ContainerFileXml.Document(containers);
        var originalXml = document.ToString();
        var created = FeeContainerProvenanceCodec.Create(document, identities.ToHashSet(), "provenance-regression",
            signalSources.ToDictionary(item => item.id, item => (IReadOnlyList<FeeContainerSignalSource>)new[] { item.source }));
        if (!FeeContainerProvenanceCodec.TryRead(created.Tags, out var saved, out var error))
            throw new InvalidOperationException("Provenance fixture could not round-trip: " + error);
        var variableStates = signalSources.Select(item => new FeeContainerVariableState(item.source.VariableGuid,
            item.source.Signal, item.source.Address, "", item.source.DataType, item.source.Id)).ToArray();
        var projected = FeeContainerVariableProjector.Apply(saved!, variableStates).Snapshot;
        var classification = FeeContainerLiveReconstructor.Reconstruct(Guid.NewGuid(), "Root",
            primaries.Select((item, index) => new FeeContainerLiveObject(item.Guid, item.Name, item.FeeType,
                "Grob_Sensor", ProvenanceContainerId: identities[index], ProvenanceContainerType: "Sensor")), [], []);
        var associations = ProjectObjectAssociations(projected, classification, live);
        var root = new Fee2ContainerRoot(Guid.NewGuid(), "Provenance root", projected, 0, 0, 0, 0,
            ObjectAssociations: associations);
        var editor = new Fee2ContainerRootEditor(root);
        VerifyOwnership(editor, expected);
        if (editor.Signals.Count != 130 || editor.CreateSnapshot().SignalBindings.Count != 130 ||
            editor.Containers.Any(container => editor.Signals.Count(signal => signal.ContainerId == container.Id) != 1) ||
            editor.CreateObjectAssociations().Single(item => item.ObjectGuid == manual.Guid).IsManual != true ||
            document.ToString() != originalXml)
            throw new InvalidOperationException("Provenance normalization lost signal identity, manual intent or modified the source.");
        var reopened = Fee2ContainerRootEditor.FromDocument(root, editor.CreateSnapshot().ContainerDocument);
        VerifyOwnership(reopened, expected);

        // Directly opening old XML needs the same isolation as the live projection.
        var legacySnapshot = saved! with { ContainerDocument = new XDocument(saved!.ContainerDocument) };
        var legacyXml = legacySnapshot.ContainerDocument.ToString();
        var legacyRoot = root with { Provenance = legacySnapshot, ObjectAssociations = null };
        var legacyEditor = new Fee2ContainerRootEditor(legacyRoot);
        if (legacyEditor.Containers.Select(item => item.Id).Distinct().Count() != 130 ||
            legacyEditor.Containers.Any(container => container.AssociatedObjectItems.Any(item =>
                !item.IsManual && !ContainerFileXml.HasMatchingObjectName(item.ObjectName, container.Component))) ||
            legacySnapshot.ContainerDocument.ToString() != legacyXml)
            throw new InvalidOperationException("Opening legacy provenance merged rows or changed the stored root.");

        VerifyRepeatedNameProvenance();
        VerifyTechnicalSignalRoutes();
    }

    private static void VerifyOwnership(Fee2ContainerRootEditor editor, IReadOnlyDictionary<Guid, string> expected)
    {
        if (editor.Containers.Count != 130 || editor.Containers.Select(item => item.Id).Distinct().Count() != 130 ||
            editor.CreateObjectAssociations().Count != expected.Count ||
            editor.Containers.Sum(item => item.AssociatedObjectCount) != expected.Count)
            throw new InvalidOperationException("Provenance objects were lost or repeated across container rows.");
        foreach (var container in editor.Containers)
            if (container.AssociatedObjectItems.Any(item => expected[item.ObjectGuid] != container.Component))
                throw new InvalidOperationException("An object was assigned to a different container after provenance projection.");
        foreach (var container in editor.CreateSnapshot().ContainerDocument.Descendants("Container"))
            if (ContainerFileXml.Objects(container).Any(item =>
                expected[Guid.Parse(item.Element("Guid")!.Value)] != container.Element("Component")!.Value))
                throw new InvalidOperationException("The export repeated another container's FEE objects.");
    }

    private static void VerifyRepeatedNameProvenance()
    {
        var first = EditingContainer("Repeated", Guid.NewGuid(), Guid.NewGuid());
        var second = EditingContainer("Repeated", Guid.NewGuid(), Guid.NewGuid());
        first.SetAttributeValue("id", ""); second.SetAttributeValue("id", "");
        var firstObject = new FeeAbstractObject { Guid = Guid.NewGuid(), Name = "Repeated", FeeType = "LogicObject" };
        var secondObject = new FeeAbstractObject { Guid = Guid.NewGuid(), Name = "Repeated", FeeType = "LogicObject" };
        var reconstructed = FeeContainerLiveReconstructor.Reconstruct(Guid.NewGuid(), "Root",
            [new(firstObject.Guid, "Repeated", "LogicObject", "Grob_Sensor", ProvenanceContainerId: ProvenanceIdentity(first)),
             new(secondObject.Guid, "Repeated", "LogicObject", "Grob_Sensor", ProvenanceContainerId: ProvenanceIdentity(second, 2))], [], []);
        first.Element("SimObjects")!.Remove(); second.Element("SimObjects")!.Remove();
        var snapshot = new FeeContainerProvenanceSnapshot(new Dictionary<string, string>(), ContainerFileXml.Document([first, second]), [], 2, 0, "test");
        var associations = ProjectObjectAssociations(snapshot, reconstructed, [firstObject, secondObject]);
        if (associations.Count != 2 || associations.Select(item => item.ContainerId).Distinct().Count() != 2 ||
            snapshot.ContainerDocument.Descendants("Container").Any(container => ContainerFileXml.Objects(container).Count() != 1))
            throw new InvalidOperationException("Original provenance did not disambiguate repeated names after ID normalization.");
        // Contradictory explicit assignments must stay available for review.
        var conflict = ContainerFileXml.Object(firstObject.GuidString, "Different", "Surface", assignmentKind: "Manual");
        foreach (var container in snapshot.ContainerDocument.Descendants("Container")) container.Element("SimObjects")!.ReplaceNodes(new XElement(conflict));
        var root = new Fee2ContainerRoot(Guid.NewGuid(), "Conflict", snapshot, 0, 0, 0, 0);
        var editor = new Fee2ContainerRootEditor(root);
        if (editor.CreateObjectAssociations().Count != 0 || editor.NonContainerObjects.Single().Guid != firstObject.Guid)
            throw new InvalidOperationException("Conflicting stored GUIDs were silently assigned to the first container.");
    }

    private static void VerifyTechnicalSignalRoutes()
    {
        var primary = Guid.NewGuid(); var helper = Guid.NewGuid(); var signal = Guid.NewGuid();
        var result = FeeContainerLiveReconstructor.Reconstruct(Guid.NewGuid(), "Root",
            [new(primary, "Container_A", "LogicObject", ProvenanceContainerId: "same", ProvenanceContainerType: "ReturnCircuit"),
             new(helper, "OtherHelper", "MoveBit", ProvenanceContainerId: "same", ProvenanceContainerType: "ReturnCircuit")],
            [new(signal, "Ready", "%I0.0", "", "Bool", "S1")], [new(signal, helper, "Output 01")]);
        if (result.Snapshot.SignalCount != 1 || result.ObjectAssociations.Any(item => item.ObjectGuid == helper) ||
            result.UnmappedObjects.Single().Guid != helper)
            throw new InvalidOperationException("Strict object names lost a real technical signal route or assigned a differently named helper.");
        var cabinet = Guid.NewGuid();
        var labelled = FeeContainerLiveReconstructor.Reconstruct(Guid.NewGuid(), "Root",
            [new(cabinet, "RawName;%I0.0", "CabinetElement", CabinetDefinition: "Grob_2PositionSwitch", Label: "Selector")],
            [new(signal, "Selected", "%I0.0", "", "Bool", "S1")], [new(signal, cabinet, "NO1")]);
        if (labelled.Snapshot.SignalCount != 1 || labelled.ObjectAssociations.Count != 0 || labelled.UnmappedObjects.Single().Guid != cabinet)
            throw new InvalidOperationException("Cabinet label handling authorized a different object name or dropped its signal.");
    }
}
