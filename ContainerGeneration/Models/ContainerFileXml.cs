using System.IO;
using System.Xml;
using System.Xml.Linq;
using VIBN_Tools.ContainerGeneration.BusinessLogic.ContainerData;

namespace VIBN_Tools.ContainerGeneration.Models;

/// <summary>Shared, backwards compatible ContainerFile extension for FEE identities.</summary>
public static class ContainerFileXml
{
    public static XDocument Load(string path)
    {
        using var reader = XmlReader.Create(path, ReaderSettings());
        return XDocument.Load(reader);
    }

    public static XElement ParseContainer(string xml)
    {
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, ReaderSettings());
        var element = XElement.Load(reader);
        if (element.Name != "Container" || string.IsNullOrWhiteSpace(element.Element("Component")?.Value) ||
            string.IsNullOrWhiteSpace(element.Element("Type")?.Value) || element.Element("DataList") is null)
            throw new InvalidDataException("Erwartet wird ein Container mit Component, Type und DataList.");
        return element;
    }

    private static XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
        MaxCharactersInDocument = 50 * 1024 * 1024, IgnoreComments = true
    };

    public static IEnumerable<XElement> Objects(XElement container) =>
        container.Element("SimObjects")?.Elements("SimObject") ?? [];

    public static bool CanRetainObjectAssociation(XElement item, string liveName, string containerName,
        string? liveFeeType = null) =>
        item.Attribute("assignment")?.Value == "Manual" ||
        (!string.IsNullOrWhiteSpace(liveName) && !string.IsNullOrWhiteSpace(containerName) &&
         string.Equals(liveName.Trim(), containerName.Trim(), StringComparison.OrdinalIgnoreCase)) ||
        IsStructuralObject(liveFeeType ?? item.Element("FeeType")?.Value, item.Element("Role")?.Value);

    // A stored role alone must never authorize an automatic physical-object link.
    public static bool IsStructuralObject(string? feeType, string? role)
    {
        var type = (feeType ?? "").Split('.').Last();
        return role == "Primary" && (type is "LogicObject" or "LogicBox" or "CabinetElement" or "Cabinet") ||
               role == "TechnicalHelper" && (type is "BoolNot" or "MoveBit" or "BoolAnd" or "BoolOr");
    }

    public static XElement Object(string guid, string name, string feeType, string role = "SimObject",
        string target = "", string clrType = "", IEnumerable<ContainerFeeSlot>? slots = null,
        string assignmentKind = "Automatic") =>
        new("SimObject", new XAttribute("assignment", assignmentKind), new XElement("Guid", guid), new XElement("Name", name),
            new XElement("FeeType", feeType), new XElement("Role", role),
            new XElement("Target", target), new XElement("ClrType", clrType),
            new XElement("Slots", (slots ?? []).Select(slot => new XElement("Slot",
                new XAttribute("name", slot.Name), new XAttribute("assignedGuid", slot.AssignedGuid)))));

    public static XDocument Document(IEnumerable<XElement> containers, XElement? inventory = null) =>
        new(new XDeclaration("1.0", "utf-8", null), new XElement("CAAMergeResult",
            new XAttribute("version", "1.0.0.0"), new XAttribute("createdAt", DateTime.UtcNow.ToString("O")),
            new XAttribute("autoCreateFile", ""), new XAttribute("zuli", ""),
            new XElement("ContainerList", containers.Select(item => new XElement(item))),
            inventory is null ? null : new XElement(inventory)));
}
