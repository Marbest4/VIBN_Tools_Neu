using System.Globalization;
using System.Xml.Linq;

namespace VIBN_Tools.Rockwell;

public static partial class AllenBradleyInterfaceExcelExporter
{
    private static void ReadSafetyModuleRows(
        XElement controller,
        IReadOnlySet<string> requestedMembers,
        IReadOnlyDictionary<string, string> comments,
        ICollection<AllenBradleyInterfaceRow> result,
        ICollection<string> warnings)
    {
        var modules = DirectChild(controller, "Modules")?.Elements()
            .Where(element => element.Name.LocalName == "Module") ?? Enumerable.Empty<XElement>();
        foreach (var module in modules)
        {
            var connections = DirectChild(DirectChild(module, "Communications") ?? module, "Connections")?.Elements()
                .Where(element => element.Name.LocalName == "Connection") ?? Enumerable.Empty<XElement>();
            foreach (var connection in connections)
            {
                if (Attribute(module, "SafetyNetwork").Length == 0 &&
                    !string.Equals(Attribute(module, "Class"), "Safety", StringComparison.OrdinalIgnoreCase) &&
                    !Attribute(connection, "Type").StartsWith("Safety", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var (elementName, suffix, usage) in new[]
                         { ("InputTag", "I", "Write"), ("OutputTag", "O", "Read") })
                {
                    var ioTag = DirectChild(connection, elementName);
                    if (ioTag is null)
                        continue;
                    var prefix = ModuleTagPrefix(module, ioTag, suffix);
                    if (prefix is null)
                    {
                        warnings.Add($"Safety-Modul '{Attribute(module, "Name")}': Adresse für {elementName} konnte nicht eindeutig bestimmt werden.");
                        continue;
                    }
                    var data = ioTag.Elements().Where(element => element.Name.LocalName == "Data" &&
                        string.Equals(Attribute(element, "Format"), "Decorated", StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (data.Length == 0)
                        warnings.Add($"Safety-Modul-Signal '{prefix}' wurde übersprungen: keine Decorated-Daten in der L5X vorhanden.");
                    foreach (var values in data)
                        Traverse(values, [prefix], requestedMembers, comments, result, warnings,
                            includeSafetySignals: true, isSafety: true, usage: usage);
                }
            }
        }
    }

    private static string? ModuleTagPrefix(XElement module, XElement ioTag, string suffix)
    {
        var explicitName = Attribute(ioTag, "Name");
        if (!string.IsNullOrWhiteSpace(explicitName))
            return explicitName;
        var moduleName = Attribute(module, "Name");
        if (string.IsNullOrWhiteSpace(moduleName))
            return null;

        var port = DirectChild(module, "Ports")?.Elements().FirstOrDefault(element =>
            element.Name.LocalName == "Port" &&
            string.Equals(Attribute(element, "Upstream"), "true", StringComparison.OrdinalIgnoreCase));
        var parentModule = Attribute(module, "ParentModule");
        if (port is not null && !string.Equals(Attribute(port, "Type"), "Ethernet", StringComparison.OrdinalIgnoreCase))
        {
            // A PointIO/backplane module uses the adapter/chassis name and
            // slot, e.g. sz_VS100G1:1:I, rather than sz_VS100G1_1:I.
            if (parentModule.Length == 0 || !int.TryParse(Attribute(port, "Address"),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var slot) || slot < 0)
                return null;
            return $"{parentModule}:{slot}:{suffix}";
        }
        return $"{moduleName}:{suffix}";
    }
}
