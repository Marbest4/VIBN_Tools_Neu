using System.IO;
using System.Xml.Linq;

namespace VIBN_Tools.ContainerGeneration.Models;

public enum ContainerFileChangeKind { Added, Removed, Changed, Unchanged }

public sealed record ContainerFileDiagnostic(string Source, string Container, string Message, XElement? Element = null)
{
    public string Text => $"{Source}: {Container} – {Message}";
}

public sealed record ContainerFileDifference(string Path, string OldValue, string NewValue)
{
    public bool IsDifferent => !string.Equals(OldValue, NewValue, StringComparison.Ordinal);
    public string Status => IsDifferent ? "Geändert" : "Gleich";
}

/// <summary>A reviewable change. Edits to NewContainer take effect before application.</summary>
public sealed class ContainerFileChange
{
    public ContainerFileChange(XElement? oldContainer, XElement? newContainer, string? reviewKey = null,
        IReadOnlyList<ContainerFileDiagnostic>? diagnostics = null)
    {
        OldContainer = oldContainer is null ? null : new XElement(oldContainer);
        NewContainer = newContainer is null ? null : new XElement(newContainer);
        ReviewKey = reviewKey ?? Name + "\u001f" + Type;
        Diagnostics = diagnostics ?? [];
        IsSelected = !HasErrors && Kind != ContainerFileChangeKind.Unchanged;
    }
    public XElement? OldContainer { get; }
    public XElement? NewContainer { get; set; }
    public bool IsSelected { get; set; }
    public string ReviewKey { get; }
    public IReadOnlyList<ContainerFileDiagnostic> Diagnostics { get; }
    public bool HasErrors => Diagnostics.Count > 0;
    public string ValidationText => string.Join(Environment.NewLine, Diagnostics.Select(item => item.Text));
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
    /// <summary>Retains malformed identities and duplicate occurrences for inspection and repair.</summary>
    public static IReadOnlyList<ContainerFileChange> CompareForReview(XDocument oldFile, XDocument newFile,
        Func<string, bool>? isKnownType = null)
    {
        var diagnostics = Inspect(oldFile, "Alt / FEE", isKnownType).Concat(Inspect(newFile, "Neu", isKnownType)).ToArray();
        var old = ReviewIndex(oldFile); var next = ReviewIndex(newFile);
        return old.Keys.Union(next.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .Select(key =>
            {
                var previous = old.GetValueOrDefault(key); var current = next.GetValueOrDefault(key);
                return new ContainerFileChange(previous, current, key,
                    diagnostics.Where(issue => issue.Element is not null && (ReferenceEquals(issue.Element, previous) || ReferenceEquals(issue.Element, current))).ToArray());
            }).ToArray();
    }

    public static IReadOnlyList<ContainerFileDiagnostic> Inspect(XDocument document, string source,
        Func<string, bool>? isKnownType = null)
    {
        var result = new List<ContainerFileDiagnostic>();
        var containers = document.Descendants("Container").ToArray();
        if (document.Root is null || document.Root.Name.LocalName is not "CAAMergeResult" and not "ContainerFile")
            result.Add(new(source, "Datei", "Kein gültiges ContainerFile-Wurzelelement."));
        if (document.Root?.Name == "CAAMergeResult" && document.Root.Element("ContainerList") is null)
            result.Add(new(source, "Datei", "ContainerList fehlt."));
        var counts = containers.GroupBy(Identity, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var container in containers)
        {
            var name = container.Element("Component")?.Value?.Trim(); var type = container.Element("Type")?.Value?.Trim();
            void Issue(string message) => result.Add(new(source, $"{(string.IsNullOrWhiteSpace(name) ? "(Name fehlt)" : name)} ({type ?? "Typ fehlt"})", message, container));
            if (string.IsNullOrWhiteSpace(name)) Issue("Containername fehlt.");
            if (string.IsNullOrWhiteSpace(type)) Issue("Containertyp fehlt.");
            else if (isKnownType?.Invoke(type) == false) Issue("Containertyp wird vom FEE-Generator nicht unterstützt: " + type);
            if (counts[Identity(container)] > 1) Issue("Name und Typ sind mehrfach vorhanden; Vorkommen werden einzeln angezeigt.");
            if (container.Element("DataList") is null) Issue("DataList fehlt.");
            foreach (var entry in container.Element("DataList")?.Elements("Entry") ?? [])
            {
                var missing = new[] { "ID", "Address", "DataType", "Signal", "Slot" }.Where(field => entry.Element(field) is null).ToArray();
                if (missing.Length > 0) Issue("Signaleintrag: Felder fehlen: " + string.Join(", ", missing));
                else if (string.IsNullOrWhiteSpace(entry.Element("Signal")?.Value) || string.IsNullOrWhiteSpace(entry.Element("Slot")?.Value))
                    Issue("Signaleintrag: Signalname oder Slot ist leer.");
                if (entry.Attribute("feeGuid") is { } signalGuid && (!Guid.TryParse(signalGuid.Value, out var guid) || guid == Guid.Empty))
                    Issue("Ungültige FEE-Signal-GUID: " + signalGuid.Value);
            }
            foreach (var item in ContainerFileXml.Objects(container))
            {
                if (!Guid.TryParse(item.Element("Guid")?.Value, out var guid) || guid == Guid.Empty)
                    Issue("Ungültige SimObject-GUID: " + item.Element("Guid")?.Value);
                if (string.IsNullOrWhiteSpace(item.Element("FeeType")?.Value)) Issue("FEE-Typ eines SimObjects fehlt.");
            }
        }
        foreach (var issue in document.Root?.Element("ComparisonDiagnostics")?.Elements("Issue") ?? [])
            result.Add(new(source, issue.Attribute("root")?.Value ?? "FEE", issue.Value));
        return result;
    }

    private static string Identity(XElement container) =>
        (container.Element("Component")?.Value?.Trim() ?? "") + "\u001f" + (container.Element("Type")?.Value?.Trim() ?? "");

    private static Dictionary<string, XElement> ReviewIndex(XDocument document)
    {
        var result = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in document.Descendants("Container"))
        {
            var identity = Identity(container); occurrences.TryGetValue(identity, out var count);
            occurrences[identity] = ++count;
            result[identity + "\u001f" + count] = container;
        }
        return result;
    }

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
