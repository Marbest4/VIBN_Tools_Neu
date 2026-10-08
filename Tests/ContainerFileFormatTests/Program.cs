using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using VIBN_Tools.ContainerGeneration.Models;

static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static XElement Container(string name, string type, params XElement[] objects) => new("Container",
    new XAttribute("id", name), new XElement("Component", name), new XElement("Type", type),
    new XElement("DataList", new XElement("Entry", new XAttribute("feeGuid", "11111111-1111-1111-1111-111111111111"),
        new XElement("ID", "S1"), new XElement("Address", "%I0.0"), new XElement("DataType", "Bool"),
        new XElement("Signal", "Signal_A"), new XElement("Slot", "PLC_IN_NO"), new XElement("Note", ""))),
    new XElement("SimObjects", objects));

var firstGuid = "22222222-2222-2222-2222-222222222222";
var secondGuid = "33333333-3333-3333-3333-333333333333";
var before = ContainerFileXml.Document([
    Container("Keep", "Button", ContainerFileXml.Object(firstGuid, "Keep", "Button")),
    Container("Removed", "Button"), Container("Changed", "Button"), Container("Retyped", "Button")]);
var after = new XDocument(before);
after.Descendants("Container").Single(item => item.Element("Component")?.Value == "Removed").Remove();
var changed = after.Descendants("Container").Single(item => item.Element("Component")?.Value == "Changed");
changed.Descendants("Signal").Single().Value = "Signal_B";
changed.Element("SimObjects")!.Add(ContainerFileXml.Object(secondGuid, "Different name", "Surface"));
after.Descendants("Container").Single(item => item.Element("Component")?.Value == "Retyped").Element("Type")!.Value = "Sensor";
after.Root!.Element("ContainerList")!.Add(Container("Added", "Button"));
var changes = ContainerFileComparison.Compare(before, after);
Assert(changes.Count(item => item.Kind == ContainerFileChangeKind.Added) == 2, "New containers/type replacements were lost.");
Assert(changes.Count(item => item.Kind == ContainerFileChangeKind.Removed) == 2, "Removed containers/type replacements were lost.");
Assert(changes.Single(item => item.Name == "Changed").Kind == ContainerFileChangeKind.Changed, "Signal/object change was not detected.");
Assert(changes.Single(item => item.Name == "Keep").Kind == ContainerFileChangeKind.Unchanged, "Unchanged container was changed.");
Assert(changes.Single(item => item.Name == "Changed").Rows.Any(row => row.IsDifferent && row.NewValue == "Surface"), "Object type is absent from aligned details.");

var shuffled = new XDocument(before);
shuffled.Descendants("Container").First().SetAttributeValue("id", "different export-local id");
Assert(ContainerFileComparison.Compare(before, shuffled).All(item => item.Kind == ContainerFileChangeKind.Unchanged), "Export-local ID caused a false change.");
var duplicateObjects = Container("DuplicateObjects", "Cylinder", ContainerFileXml.Object(firstGuid, "Axis", "MotionJoint"),
    ContainerFileXml.Object(secondGuid, "Axis", "MotionJoint"));
var duplicateChanged = new XElement(duplicateObjects);
duplicateChanged.Element("SimObjects")!.Elements().Last().Element("FeeType")!.Value = "Surface";
Assert(ContainerFileComparison.Align(duplicateObjects, duplicateChanged).Count(row => row.IsDifferent) == 1,
    "Equal names merged distinct object GUIDs.");
var empty = ContainerFileXml.Document([]);
Assert(ContainerFileComparison.Compare(before, empty).All(item => item.Kind == ContainerFileChangeKind.Removed), "Empty target file cannot remove all containers.");
var signalGuidChange = new XDocument(before);
signalGuidChange.Descendants("Entry").First().SetAttributeValue("feeGuid", secondGuid);
Assert(ContainerFileComparison.Compare(before, signalGuidChange).Count(item => item.Kind == ContainerFileChangeKind.Changed) == 1,
    "Signal GUID changes were ignored.");
var duplicateContainer = ContainerFileXml.Document([Container("Same", "Button"), Container("Same", "Button")]);
try { ContainerFileComparison.Compare(before, duplicateContainer); throw new Exception("Ambiguous container identities were accepted."); }
catch (System.IO.InvalidDataException) { }
var duplicateReview = ContainerFileComparison.CompareForReview(empty, duplicateContainer);
Assert(duplicateReview.Count == 2 && duplicateReview.All(item => item.HasErrors && !item.IsSelected),
    "Review discarded duplicate containers or selected ambiguous writes by default.");
Assert(duplicateReview.Select(item => item.ReviewKey).Distinct().Count() == 2,
    "Duplicate occurrences cannot be marked separately.");
var broken = ContainerFileXml.Document([new XElement("Container", new XElement("DataList"),
    new XElement("SimObjects", ContainerFileXml.Object("bad-guid", "Surface", "")))]);
var brokenReview = ContainerFileComparison.CompareForReview(before, broken);
Assert(brokenReview.Any(item => item.NewContainer is not null && item.HasErrors), "Invalid identities were rejected instead of marked for review.");
Assert(ContainerFileComparison.Inspect(broken, "Neu").Count >= 4, "Missing name/type and invalid object GUID/type were not reported.");
var incompleteFee = new XDocument(before);
incompleteFee.Root!.Add(new XElement("ComparisonDiagnostics", new XElement("Issue", new XAttribute("root", "Root A"), "Read failed")));
Assert(ContainerFileComparison.Inspect(incompleteFee, "FEE").Any(item => item.Text.Contains("Root A") && item.Message == "Read failed"),
    "Partial FEE read diagnostics were lost.");
Assert(ContainerFileComparison.Inspect(before, "Alt").Count == 0, "Valid input was marked invalid.");
var noNotes = new XDocument(before); foreach (var note in noNotes.Descendants("Note").ToArray()) note.Remove();
Assert(ContainerFileComparison.Inspect(noNotes, "Alt").Count == 0, "Optional signal notes were treated as errors.");
Assert(ContainerFileComparison.CompareForReview(empty, before, _ => false).All(item => item.HasErrors),
    "Unsupported generation types were discarded instead of marked.");
var automatic = ContainerFileXml.Object(firstGuid, "Other", "Surface");
Assert(!ContainerFileXml.CanRetainObjectAssociation(automatic, "Other", "Container_A"), "A different name was automatically assigned.");
Assert(ContainerFileXml.CanRetainObjectAssociation(automatic, "container_a", "Container_A"), "Same-name automatic association was rejected.");
var manual = ContainerFileXml.Object(firstGuid, "Other", "Surface", assignmentKind: "Manual");
Assert(ContainerFileXml.CanRetainObjectAssociation(manual, "Other", "Container_A"), "Explicit mixed-name assignment was rejected.");
foreach (var role in new[] { "Primary", "TechnicalHelper", "SimObject" })
{
    var physical = ContainerFileXml.Object(firstGuid, "Other", "Surface", role);
    Assert(!ContainerFileXml.CanRetainObjectAssociation(physical, "Other", "Container_A"), "A stored role bypassed the physical-object name rule.");
}
foreach (var (type, role) in new[] { ("LogicObject", "Primary"), ("CabinetElement", "Primary"), ("BoolNot", "TechnicalHelper"), ("MoveBit", "TechnicalHelper") })
    Assert(!ContainerFileXml.CanRetainObjectAssociation(ContainerFileXml.Object(firstGuid, "Other", type, role), "Other", "Container_A"),
        "A structural type bypassed the automatic name rule.");
Assert(!ContainerFileXml.CanRetainObjectAssociation(automatic, "", ""), "Empty names authorized an automatic link.");
Assert(ContainerFileXml.HasMatchingObjectName(" Container_A ", "container_a"), "Name normalization is inconsistent.");
var legacyIds = ContainerFileXml.Document(Enumerable.Range(0, 130).Select(index => Container("C" + index, "Sensor")));
foreach (var container in legacyIds.Descendants("Container")) container.SetAttributeValue("id", "");
var legacyContainers = legacyIds.Descendants("Container").ToArray();
legacyContainers[1].Attribute("id")!.Remove(); legacyContainers[2].SetAttributeValue("id", " ");
legacyContainers[3].SetAttributeValue("id", "duplicate"); legacyContainers[4].SetAttributeValue("id", "duplicate");
legacyContainers[5].SetAttributeValue("id", "fee-container:1");
ContainerFileXml.EnsureUniqueContainerIds(legacyIds);
var normalizedIds = legacyContainers.Select(item => item.Attribute("id")!.Value).ToArray();
Assert(normalizedIds.All(id => !string.IsNullOrWhiteSpace(id)) && normalizedIds.Distinct().Count() == 130,
    "Empty, missing or duplicate IDs still share a reverse-editor identity.");
Assert(normalizedIds[5] == "fee-container:1", "A valid unique ID was overwritten by a generated ID.");
ContainerFileXml.EnsureUniqueContainerIds(legacyIds);
Assert(normalizedIds.SequenceEqual(legacyContainers.Select(item => item.Attribute("id")!.Value)), "ID normalization changed an already normalized document.");
var runtimeErrors = ContainerFileXml.Document([Container("Container_A", "Button", automatic)]);
runtimeErrors.Root!.Add(new XElement("ComparisonDiagnostics", new XElement("Issue", new XAttribute("objectGuid", firstGuid), "Live object validation failed")));
Assert(ContainerFileComparison.CompareForReview(runtimeErrors, runtimeErrors).Single().HasErrors, "A live FEE error did not mark its container comparison row.");
var manualModel = new VIBN_Tools.ContainerGeneration.BusinessLogic.ContainerData.ContainerFeeObject { AssignmentKind = "Manual" };
Assert(manualModel.Clone().AssignmentKind == "Manual", "Cloning lost manual association intent.");
try { ContainerFileXml.ParseContainer("<!DOCTYPE Container [<!ENTITY data SYSTEM 'file:///etc/passwd'>]><Container><Component>&data;</Component><Type>Button</Type><DataList/></Container>"); throw new Exception("DTD was accepted."); }
catch (XmlException) { }

var schema = new XmlSchemaSet(); schema.Add(null, Path.Combine(AppContext.BaseDirectory, "CAAResult.xsd"));
var document = ContainerFileXml.Document([duplicateObjects, Container("Manual", "Button", manual)], new XElement("FeeInventory",
    new XElement("SimObjects", ContainerFileXml.Object(firstGuid, "Axis", "Surface", "Available")),
    new XElement("Signals", new XElement("Signal", new XElement("Guid", secondGuid), new XElement("InterfaceGuid", firstGuid),
        new XElement("InterfaceName", "Existing"), new XElement("Tag", "Signal_A"), new XElement("Address", "%I0.0"),
        new XElement("Path", ""), new XElement("DataType", "Bool")))));
document.Validate(schema, (_, args) => throw new InvalidOperationException(args.Message));
empty.Validate(schema, (_, args) => throw new InvalidOperationException(args.Message));
var active = 0; var peak = 0;
await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => VIBN_Tools.GlobalClasses.FeeObjects.FeeMutationScope.RunAsync(async () =>
{
    var count = Interlocked.Increment(ref active); peak = Math.Max(peak, count);
    await VIBN_Tools.GlobalClasses.FeeObjects.FeeMutationScope.RunAsync(async () => { await Task.Yield(); return true; });
    await Task.Delay(10); Interlocked.Decrement(ref active); return true;
})));
Assert(peak == 1 && active == 0, "Complete FEE writes overlapped or nested generation deadlocked.");
Console.WriteLine("ContainerFile schema, identity, aligned comparison and secure parsing checks passed.");
