using System.Xml;
using System.Xml.Linq;

namespace VIBN_Tools.Quality;

/// <summary>Reads signal identities without depending on a particular ContainerXML namespace.</summary>
public sealed class ContainerSignalObservationReader
{
    public IReadOnlyList<SignalObservation> Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 50L * 1024 * 1024,
        };
        using var reader = XmlReader.Create(path, settings);
        var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        var sourceKey = Path.GetFullPath(path);
        return document.Descendants()
            .Where(IsSignalEntry)
            .Select(entry => ToObservation(entry, sourceKey))
            .Where(observation => !string.IsNullOrWhiteSpace(observation.SymbolicName) ||
                                  !string.IsNullOrWhiteSpace(observation.Address))
            .DistinctBy(observation => string.Join("\u001f", observation.SignalId, observation.SymbolicName,
                observation.Address, observation.Container, observation.Slot), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsSignalEntry(XElement element)
    {
        if (!element.Name.LocalName.Equals("Entry", StringComparison.OrdinalIgnoreCase) &&
            !element.Name.LocalName.Contains("Signal", StringComparison.OrdinalIgnoreCase))
            return false;
        return !string.IsNullOrWhiteSpace(Read(element, "Signal", "SignalName", "SymbolicName", "Name", "Address", "Adress"));
    }

    private static SignalObservation ToObservation(XElement entry, string sourceKey)
    {
        var container = entry.Ancestors().FirstOrDefault(ancestor =>
            ancestor.Name.LocalName.Equals("Container", StringComparison.OrdinalIgnoreCase));
        return new SignalObservation(
            Read(entry, "SignalId", "SignalID", "ID", "Id", "Guid"),
            Read(entry, "Signal", "SignalName", "SymbolicName", "Name"),
            Read(entry, "Address", "Adress", "ByteAddress"),
            Read(entry, "DataType", "Datatype", "Type"),
            Read(entry, "InterfaceGuid", "InterfaceGUID", "ProviderGuid"),
            container is null ? string.Empty : Read(container, "Component", "Name", "ID", "Id"),
            Read(entry, "Slot", "SlotName", "ExpectedSlot"),
            Read(entry, "FeeVariableGuid", "FeeGuid", "VariableGuid"),
            sourceKey);
    }

    private static string Read(XElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var attribute = element.Attributes().FirstOrDefault(candidate =>
                candidate.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (attribute is not null && !string.IsNullOrWhiteSpace(attribute.Value))
                return attribute.Value.Trim();
            var child = element.Elements().FirstOrDefault(candidate =>
                candidate.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (child is not null && !string.IsNullOrWhiteSpace(child.Value))
                return child.Value.Trim();
        }
        return string.Empty;
    }
}
