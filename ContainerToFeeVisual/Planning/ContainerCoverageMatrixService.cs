using System.IO;
using System.Xml.Linq;

namespace VIBN_Tools.ContainerToFeeVisual;

public sealed record ContainerCoverageRow(
    string ContainerType,
    string RuntimeClass,
    bool RequirementsPresent,
    int RuntimeSlotCount,
    int RequirementsSlotCount,
    string MissingInRequirements,
    string UnknownInRuntime,
    bool ForwardGeneration,
    bool ReverseRecognition,
    string Status);

public sealed record ContainerCoverageMatrix(
    IReadOnlyList<ContainerCoverageRow> Rows,
    int CompleteRows,
    int WarningRows,
    string SourcePath);

/// <summary>
/// Produces one auditable matrix from the shared Container2FEE/FEE2Container
/// catalog and a selected Requirements XML. Both directions intentionally use
/// the same catalog, so a type can no longer disappear unnoticed in one tab.
/// </summary>
public sealed class ContainerCoverageMatrixService
{
    public ContainerCoverageMatrix Build(string requirementsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requirementsPath);
        if (!File.Exists(requirementsPath))
            throw new FileNotFoundException("Requirements XML was not found.", requirementsPath);

        var document = XDocument.Load(requirementsPath, LoadOptions.SetLineInfo);
        var requirementTypes = document.Descendants("Component")
            .Where(element => !string.IsNullOrWhiteSpace((string?)element.Attribute("type")))
            .GroupBy(element => ((string)element.Attribute("type")!).Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(component => component.Descendants("Slot"))
                    .Select(slot => ((string?)slot.Attribute("name") ?? string.Empty).Trim())
                    .Where(slot => slot.Length > 0)
                    .ToHashSet(StringComparer.Ordinal),
                StringComparer.OrdinalIgnoreCase);

        var reverseTypes = FeeContainerLiveReconstructor.SupportedContainerTypes
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var supportedTypes = ContainerMetadataCatalog.SupportedXmlTypes
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var runtimeRows = supportedTypes.Select(type =>
        {
            ContainerMetadataCatalog.TryGet(type, out var descriptor);
            var requirementSlots = requirementTypes.GetValueOrDefault(type) ?? [];
            var missing = descriptor.Slots.Except(requirementSlots, StringComparer.Ordinal).Order().ToArray();
            var unknown = requirementSlots.Except(descriptor.Slots, StringComparer.Ordinal).Order().ToArray();
            var present = requirementTypes.ContainsKey(type);
            var reverse = reverseTypes.Contains(type);
            var status = !present
                ? "Requirements-Typ fehlt"
                : missing.Length > 0 || unknown.Length > 0
                    ? "Slot-Abweichung prüfen"
                    : reverse
                        ? "Vollständig abgedeckt"
                        : "FEE2Container fehlt";
            return new ContainerCoverageRow(
                type,
                descriptor.RuntimeType.Name,
                present,
                descriptor.Slots.Count,
                requirementSlots.Count,
                string.Join(", ", missing),
                string.Join(", ", unknown),
                ForwardGeneration: true,
                ReverseRecognition: reverse,
                status);
        });
        var requirementsOnlyRows = requirementTypes
            .Where(item => !supportedTypes.Contains(item.Key))
            .Select(item => new ContainerCoverageRow(
                item.Key,
                "—",
                RequirementsPresent: true,
                RuntimeSlotCount: 0,
                RequirementsSlotCount: item.Value.Count,
                MissingInRequirements: string.Empty,
                UnknownInRuntime: string.Join(", ", item.Value.Order(StringComparer.Ordinal)),
                ForwardGeneration: false,
                ReverseRecognition: reverseTypes.Contains(item.Key),
                Status: reverseTypes.Contains(item.Key)
                    ? "Vorwärtsgenerator fehlt"
                    : "Generatoren fehlen"));
        var rows = runtimeRows
            .Concat(requirementsOnlyRows)
            .OrderBy(row => row.ContainerType, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var complete = rows.Count(row => row.Status == "Vollständig abgedeckt");
        return new ContainerCoverageMatrix(rows, complete, rows.Length - complete, requirementsPath);
    }
}
