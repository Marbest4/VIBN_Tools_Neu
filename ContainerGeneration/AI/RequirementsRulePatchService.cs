using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using VIBN_Tools.ContainerGeneration.Utils;

namespace VIBN_Tools.ContainerGeneration.AI;

public sealed record RequirementsRulePatchItem(
    string SuggestionId,
    string ComponentType,
    string SignalText,
    string PreviousSlot,
    string NewSlot);

public sealed record RequirementsRulePatchPlan(
    string SourcePath,
    string SourceHash,
    string UpdatedXml,
    string Preview,
    IReadOnlyList<RequirementsRulePatchItem> Items);

public sealed record RequirementsRulePatchApplyResult(
    string SourcePath,
    string BackupPath,
    int AppliedRules);

/// <summary>
/// Creates deterministic, exact-match overrides for reviewed slot corrections.
/// Plans are validated first and can only replace an unchanged source file after
/// an explicit confirmation. File.Replace creates the backup and replacement in
/// one filesystem operation.
/// </summary>
public sealed class RequirementsRulePatchService
{
    private const string Marker = "VIBN AI exact override";

    public RequirementsRulePatchPlan CreatePlan(
        string sourcePath,
        IEnumerable<RuleSuggestion> suggestions)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("Eine Requirements-XML muss ausgewählt werden.", nameof(sourcePath));

        var fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Die Requirements-XML wurde nicht gefunden.", fullPath);

        var accepted = suggestions
            .Where(suggestion => suggestion.Status == RuleSuggestionStatus.Accepted)
            .Where(suggestion => string.Equals(
                suggestion.PropertyName, "Slot", StringComparison.OrdinalIgnoreCase))
            .Where(suggestion => !string.Equals(
                suggestion.PreviousValue, suggestion.NewValue, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (accepted.Length == 0)
            throw new InvalidOperationException("Es gibt keine angenommenen Slot-Regelvorschläge.");

        var conflicting = accepted
            .GroupBy(
                suggestion => $"{Normalize(suggestion.ComponentType)}\u001f{Normalize(suggestion.SignalText)}",
                StringComparer.Ordinal)
            .FirstOrDefault(group => group
                .Select(suggestion => Normalize(suggestion.NewValue))
                .Distinct(StringComparer.Ordinal)
                .Count() > 1);
        if (conflicting is not null)
        {
            var sample = conflicting.First();
            throw new InvalidOperationException(
                $"Für Typ '{sample.ComponentType}' und Signal '{sample.SignalText}' sind mehrere Zielslots angenommen.");
        }

        var sourceBytes = File.ReadAllBytes(fullPath);
        using var input = new MemoryStream(sourceBytes, writable: false);
        var document = XDocument.Load(input, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        Validate(document, "Die ausgewählte Requirements-XML ist nicht schema-konform.");

        var items = accepted
            .GroupBy(suggestion => suggestion.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(suggestion => suggestion.ComponentType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(suggestion => suggestion.SignalText, StringComparer.OrdinalIgnoreCase)
            .Select(suggestion => ApplySuggestion(document, suggestion))
            .ToArray();

        Validate(document, "Die erzeugte Requirements-Vorschau ist nicht schema-konform.");
        var updatedXml = Serialize(document);
        var preview = string.Join(
            Environment.NewLine,
            items.Select(item =>
                $"Typ '{item.ComponentType}', Signal '{item.SignalText}': " +
                $"Slot '{Display(item.PreviousSlot)}' -> '{item.NewSlot}' (exakter Volltexttreffer)"));

        return new RequirementsRulePatchPlan(
            fullPath,
            Convert.ToHexString(SHA256.HashData(sourceBytes)),
            updatedXml,
            preview,
            items);
    }

    public RequirementsRulePatchApplyResult Apply(
        RequirementsRulePatchPlan plan,
        bool explicitlyConfirmed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!explicitlyConfirmed)
            throw new InvalidOperationException("Die XML-Übernahme wurde nicht ausdrücklich bestätigt.");

        var currentBytes = File.ReadAllBytes(plan.SourcePath);
        var currentHash = Convert.ToHexString(SHA256.HashData(currentBytes));
        if (!string.Equals(currentHash, plan.SourceHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Die Requirements-XML wurde seit der Vorschau verändert. Bitte die Vorschau neu erzeugen.");
        }

        var directory = Path.GetDirectoryName(plan.SourcePath)
            ?? throw new InvalidOperationException("Der Requirements-Pfad besitzt kein Verzeichnis.");
        var fileName = Path.GetFileName(plan.SourcePath);
        var temporaryPath = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");
        var backupPath = Path.Combine(
            directory,
            $"{fileName}.{DateTime.Now:yyyyMMdd-HHmmssfff}.{Guid.NewGuid():N}.vibn-backup");

        try
        {
            File.WriteAllText(temporaryPath, plan.UpdatedXml, new UTF8Encoding(false));
            var written = XDocument.Load(temporaryPath, LoadOptions.SetLineInfo);
            Validate(written, "Die temporäre Requirements-XML ist nicht schema-konform.");
            File.Replace(temporaryPath, plan.SourcePath, backupPath, ignoreMetadataErrors: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        return new RequirementsRulePatchApplyResult(
            plan.SourcePath,
            backupPath,
            plan.Items.Count);
    }

    private static RequirementsRulePatchItem ApplySuggestion(
        XDocument document,
        RuleSuggestion suggestion)
    {
        var componentsRoot = document.Root?.Element("Components")
            ?? throw new InvalidOperationException("Das Element AutoCreate/Components fehlt.");
        var typedComponents = componentsRoot.Elements("Component")
            .Where(component => string.Equals(
                component.Attribute("type")?.Value,
                suggestion.ComponentType,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (typedComponents.Length == 0)
        {
            throw new InvalidOperationException(
                $"Der Komponententyp '{suggestion.ComponentType}' existiert nicht in der Requirements-XML.");
        }

        var overrideName = $"{Marker} {suggestion.Id[..Math.Min(12, suggestion.Id.Length)]}";
        typedComponents
            .Where(component => IsGeneratedOverride(component) &&
                (string.Equals(component.Attribute("name")?.Value, overrideName, StringComparison.Ordinal) ||
                 component.Descendants("Key").Any(key =>
                     string.Equals(key.Attribute("match")?.Value, "exact", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(key.Value.Trim(), suggestion.SignalText.Trim(), StringComparison.OrdinalIgnoreCase))))
            .Remove();

        var baseComponents = componentsRoot.Elements("Component")
            .Where(component => string.Equals(
                component.Attribute("type")?.Value,
                suggestion.ComponentType,
                StringComparison.OrdinalIgnoreCase))
            .Where(component => !IsGeneratedOverride(component))
            .ToArray();
        if (baseComponents.Length == 0)
        {
            throw new InvalidOperationException(
                $"Für den Komponententyp '{suggestion.ComponentType}' existiert keine reguläre Basisdefinition.");
        }

        foreach (var component in baseComponents)
            AddExactExclusion(component, suggestion.SignalText);

        var generated = new XElement(
            "Component",
            new XAttribute("name", overrideName),
            new XAttribute("type", suggestion.ComponentType.Trim()),
            new XElement(
                "Keygroup",
                new XAttribute("type", "required"),
                new XAttribute("operator", "OR"),
                new XElement(
                    "KeySet",
                    new XAttribute("name", Marker),
                    new XElement(
                        "Key",
                        new XAttribute("keep", true),
                        new XAttribute("match", "exact"),
                        suggestion.SignalText.Trim()))),
            new XElement(
                "Slots",
                new XElement("Slot", new XAttribute("name", suggestion.NewValue.Trim()))));
        componentsRoot.Add(generated);

        return new RequirementsRulePatchItem(
            suggestion.Id,
            suggestion.ComponentType.Trim(),
            suggestion.SignalText.Trim(),
            suggestion.PreviousValue.Trim(),
            suggestion.NewValue.Trim());
    }

    private static void AddExactExclusion(XElement component, string signalText)
    {
        var keySet = component
            .Elements("Keygroup")
            .Where(group => string.Equals(
                group.Attribute("type")?.Value, "exclude", StringComparison.OrdinalIgnoreCase))
            .SelectMany(group => group.Elements("KeySet"))
            .FirstOrDefault(set => string.Equals(
                set.Attribute("name")?.Value, Marker, StringComparison.Ordinal));
        if (keySet is null)
        {
            var group = new XElement(
                "Keygroup",
                new XAttribute("type", "exclude"),
                new XAttribute("operator", "OR"),
                new XElement("KeySet", new XAttribute("name", Marker)));
            component.Element("Slots")?.AddBeforeSelf(group);
            keySet = group.Element("KeySet")!;
        }

        if (keySet.Elements("Key").Any(key =>
                string.Equals(key.Value.Trim(), signalText.Trim(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(key.Attribute("match")?.Value, "exact", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        keySet.Add(new XElement(
            "Key",
            new XAttribute("keep", true),
            new XAttribute("match", "exact"),
            signalText.Trim()));
    }

    private static bool IsGeneratedOverride(XElement component) =>
        component.Elements("Keygroup")
            .SelectMany(group => group.Elements("KeySet"))
            .Any(set => string.Equals(set.Attribute("name")?.Value, Marker, StringComparison.Ordinal)) &&
        (component.Attribute("name")?.Value?.StartsWith(Marker, StringComparison.Ordinal) ?? false);

    private static void Validate(XDocument document, string message)
    {
        using var schema = ResourceHandler.GetEmbeddedResourceStream(ResourceHandler.AUTOCREATE_SCHEMA);
        if (!XmlHandler.Validate(document, schema))
            throw new InvalidDataException(message);
    }

    private static string Serialize(XDocument document)
    {
        var builder = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = document.Declaration is null,
            Encoding = new UTF8Encoding(false),
            NewLineChars = Environment.NewLine
        };
        using (var writer = XmlWriter.Create(builder, settings))
            document.Save(writer);
        return builder.ToString();
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
    private static string Display(string value) => string.IsNullOrWhiteSpace(value) ? "leer" : value;
}
