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
try { ContainerFileXml.ParseContainer("<!DOCTYPE Container [<!ENTITY data SYSTEM 'file:///etc/passwd'>]><Container><Component>&data;</Component><Type>Button</Type><DataList/></Container>"); throw new Exception("DTD was accepted."); }
catch (XmlException) { }

var schema = new XmlSchemaSet(); schema.Add(null, Path.Combine(AppContext.BaseDirectory, "CAAResult.xsd"));
var document = ContainerFileXml.Document([duplicateObjects], new XElement("FeeInventory",
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
