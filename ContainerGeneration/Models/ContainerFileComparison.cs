using System.IO;
using System.Xml.Linq;

namespace VIBN_Tools.ContainerGeneration.Models;

public enum ContainerFileChangeKind { Added, Removed, Changed, Unchanged }

public sealed record ContainerFileDifference(string Path, string OldValue, string NewValue)
{
    public bool IsDifferent => !string.Equals(OldValue, NewValue, StringComparison.Ordinal);
    public string Status => IsDifferent ? "Geändert" : "Gleich";
}

/// <summary>A reviewable change. Edits to NewContainer take effect before application.</summary>
public sealed class ContainerFileChange
{
    public ContainerFileChange(XElement? oldContainer, XElement? newContainer)
    {
        OldContainer = oldContainer is null ? null : new XElement(oldContainer);
        NewContainer = newContainer is null ? null : new XElement(newContainer);
        IsSelected = Kind != ContainerFileChangeKind.Unchanged;
    }
    public XElement? OldContainer { get; }
    public XElement? NewContainer { get; set; }
    public bool IsSelected { get; set; }
    public string Name => (NewContainer ?? OldContainer)?.Element("Component")?.Value ?? "";
    public string Type => (NewContainer ?? OldContainer)?.Element("Type")?.Value ?? "";
    public ContainerFileChangeKind Kind => OldContainer is null ? ContainerFileChangeKind.Added :
        NewContainer is null ? ContainerFileChangeKind.Removed :
        Rows.Any(row => row.IsDifferent) ? ContainerFileChangeKind.Changed : ContainerFileChangeKind.Unchanged;
    public string KindText => Kind switch
    {
        ContainerFileChangeKind.Added => "Neuer Container", ContainerFileChangeKind.Removed => "Container entfällt",
        ContainerFileChangeKind.Changed => "Geänderter Container", _ => "Unverändert"
    };
    public IReadOnlyList<ContainerFileDifference> Rows => ContainerFileComparison.Align(OldContainer, NewContainer);
}

public static class ContainerFileComparison
{
    public static IReadOnlyList<ContainerFileChange> Compare(XDocument oldFile, XDocument newFile)
    {
        var old = Index(oldFile); var next = Index(newFile);
        return old.Keys.Union(next.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .Select(key => new ContainerFileChange(old.GetValueOrDefault(key), next.GetValueOrDefault(key))).ToArray();
    }

    private static Dictionary<string, XElement> Index(XDocument document)
    {
        var result = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in document.Descendants("Container"))
        {
            var name = container.Element("Component")?.Value?.Trim();
            var type = container.Element("Type")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type))
                throw new InvalidDataException("Ein Container besitzt keinen Namen oder Typ.");
            if (!result.TryAdd(name + "\u001f" + type, container))
                throw new InvalidDataException($"Container '{name}' ({type}) ist mehrfach vorhanden. Bitte eindeutige Container herstellen.");
        }
        return result;
    }

    public static IReadOnlyList<ContainerFileDifference> Align(XElement? oldContainer, XElement? newContainer)
    {
        var old = Flatten(oldContainer); var next = Flatten(newContainer);
        return old.Keys.Union(next.Keys, StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .Select(key => new ContainerFileDifference(key, old.GetValueOrDefault(key, ""), next.GetValueOrDefault(key, "")))
            .ToArray();
    }

    private static Dictionary<string, string> Flatten(XElement? container)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (container is null) return result;
        // Container IDs are export-local. Name and type identify the container;
        // signal/FEE GUIDs remain significant to distinguish actual assignments.
        foreach (var element in container.Elements().Where(item => item.Name != "DataList" && item.Name != "SimObjects"))
            FlattenElement(element, element.Name.LocalName, result);
        foreach (var section in new[] { "DataList", "SimObjects" })
        {
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in container.Element(section)?.Elements() ?? [])
            {
                var identity = section == "SimObjects" ? item.Element("Guid")?.Value?.ToLowerInvariant() :
                    item.Element("Slot")?.Value + "/" + item.Element("Signal")?.Value;
                identity ??= "";
                occurrences.TryGetValue(identity, out var count); occurrences[identity] = ++count;
                FlattenElement(item, $"{section}/{identity}[{count}]", result);
            }
        }
        return result;
    }

    private static void FlattenElement(XElement element, string path, IDictionary<string, string> result)
    {
        result[path + "/#vorhanden"] = "Ja";
        foreach (var attr in element.Attributes()) result[path + "/@" + attr.Name.LocalName] = attr.Value;
        if (!element.HasElements) { result[path] = element.Value; return; }
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var child in element.Elements())
        {
            var key = child.Name.LocalName;
            if (child.Name == "Slot") key += "[" + child.Attribute("name")?.Value + "]";
            else if (child.Element("Guid") is { } guid) key += "[" + guid.Value.ToLowerInvariant() + "]";
            counts.TryGetValue(key, out var count); counts[key] = ++count;
            FlattenElement(child, path + "/" + key + (count > 1 ? $"[{count}]" : ""), result);
        }
    }
}
