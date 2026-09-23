using System.Collections.ObjectModel;
using System.IO;
using System.Xml.Linq;
using VIBN_Tools.ContainerGeneration.BusinessLogic.ContainerData;

namespace VIBN_Tools.ContainerGeneration.Models;

public sealed record ContainerFileWorkspace(
    IReadOnlyList<ContainerData> Containers,
    IReadOnlyList<ContainerEntry> UnassignedSignals);

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

        var document = XDocument.Load(path, LoadOptions.SetLineInfo);
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
        return new ContainerFileWorkspace(containers, unassigned);
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
                Note = Child(entry, "Note")
            })
            .ToArray();
        return new ComponentContainer
        {
            Id = element.Attribute("id")?.Value?.Trim() ?? string.Empty,
            Component = Child(element, "Component"),
            Type = Child(element, "Type"),
            DataList = new ObservableCollection<ContainerEntry>(entries)
        };
    }

    private static bool IsUnknown(ComponentContainer container) =>
        string.Equals(container.Type, "unknown", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(container.Component, "unknown", StringComparison.OrdinalIgnoreCase);

    private static string Child(XElement parent, string name) =>
        parent.Elements(name).FirstOrDefault()?.Value?.Trim() ?? string.Empty;
}
