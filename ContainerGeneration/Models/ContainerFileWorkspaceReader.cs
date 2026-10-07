using System.Collections.ObjectModel;
using System.IO;
using System.Xml.Linq;
using VIBN_Tools.ContainerGeneration.BusinessLogic.ContainerData;

namespace VIBN_Tools.ContainerGeneration.Models;

public sealed record ContainerFileWorkspace(
    IReadOnlyList<ContainerData> Containers,
    IReadOnlyList<ContainerEntry> UnassignedSignals,
    System.Xml.XmlElement? FeeInventory = null);

/// <summary>
/// Reads exported ContainerFiles into the existing generation workspace
/// model. It intentionally does not create a second comparison domain.
/// </summary>
public static class ContainerFileWorkspaceReader
{
    public static ContainerFileWorkspace Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("ContainerFile-Pfad fehlt.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("ContainerFile wurde nicht gefunden.", path);

        var document = ContainerFileXml.Load(path);
        var parsed = document.Descendants("Container")
            .Select(ParseContainer)
            .ToList();
        if (parsed.Count == 0)
            throw new InvalidDataException("Die Datei enthält keine Container-Elemente.");

        var unknown = parsed.Where(IsUnknown).ToArray();
        var unassigned = unknown.SelectMany(container => container.DataList).ToArray();
        var containers = parsed
            .Where(container => !IsUnknown(container))
            .Select(container => new ContainerData(container))
            .ToArray();
        var xml = new System.Xml.XmlDocument { XmlResolver = null };
        var inventory = document.Root?.Element("FeeInventory");
        if (inventory is not null) xml.LoadXml(inventory.ToString());
        return new ContainerFileWorkspace(containers, unassigned, xml.DocumentElement);
    }

    private static ComponentContainer ParseContainer(XElement element)
    {
        var entries = element.Descendants("Entry")
            .Select(entry => new ContainerEntry
            {
                ID = Child(entry, "ID"),
                Address = Child(entry, "Address"),
                DataType = Child(entry, "DataType"),
                Signal = Child(entry, "Signal"),
                Slot = Child(entry, "Slot"),
                Note = Child(entry, "Note"),
                FeeGuid = entry.Attribute("feeGuid")?.Value ?? string.Empty
            })
            .ToArray();
        return new ComponentContainer
        {
            Id = element.Attribute("id")?.Value?.Trim() ?? string.Empty,
            Component = Child(element, "Component"),
            Type = Child(element, "Type"),
            DataList = new ObservableCollection<ContainerEntry>(entries),
            SimObjects = new ObservableCollection<ContainerFeeObject>(
                element.Element("SimObjects")?.Elements("SimObject").Select(item => new ContainerFeeObject
                {
                    Guid = Child(item, "Guid"), Name = Child(item, "Name"), FeeType = Child(item, "FeeType"),
                    Role = Child(item, "Role"), Target = Child(item, "Target"), ClrType = Child(item, "ClrType"),
                    Slots = item.Element("Slots")?.Elements("Slot").Select(slot => new ContainerFeeSlot
                    {
                        Name = slot.Attribute("name")?.Value ?? string.Empty,
                        AssignedGuid = slot.Attribute("assignedGuid")?.Value ?? string.Empty
                    }).ToList() ?? []
                }) ?? [])
        };
    }

    private static bool IsUnknown(ComponentContainer container) =>
        string.Equals(container.Type, "unknown", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(container.Component, "unknown", StringComparison.OrdinalIgnoreCase);

    private static string Child(XElement parent, string name) =>
        parent.Elements(name).FirstOrDefault()?.Value?.Trim() ?? string.Empty;
}
