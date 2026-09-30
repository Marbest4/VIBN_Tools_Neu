using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;
using VIBN_Tools.Application;

namespace VIBN_Tools.ContainerToFee;

/// <summary>
/// Versioned per-object provenance stored as properties in the FEE
/// TagComponent. Marks are read only as a legacy fallback and are never used
/// for newly generated objects.
/// </summary>
internal static class ContainerObjectProvenance
{
    public const string SchemaKey = "vibn.container-object.schema";
    public const string GeneratorKey = "vibn.container-object.generator";
    public const string ContainerIdKey = "vibn.container-object.container-id";
    public const string ContainerTypeKey = "vibn.container-object.container-type";
    public const string CreatedUtcKey = "vibn.container-object.created-utc";
    public const string CurrentSchema = "1";
    public const string Generator = "Container2FEE Visual";

    // Backward-compatible read constants for projects generated before the
    // property-based format. New code must not write these marks.
    public const string GeneratedMarker = "VIBN.Container2FEE";
    public const string IdPrefix = "VIBN.ContainerId=";
    public const string TypePrefix = "VIBN.ContainerType=";

    public static async Task WriteNewObjectAsync(
        FeeAbstractObject? feeObject,
        ContainerBaseClass container)
    {
        if (feeObject is null || feeObject.Guid == Guid.Empty ||
            string.IsNullOrWhiteSpace(container.GenerationProvenanceId))
            return;

        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        properties[SchemaKey] = CurrentSchema;
        properties[GeneratorKey] = Generator;
        properties[ContainerIdKey] = container.GenerationProvenanceId;
        properties[ContainerTypeKey] = container.GenerationContainerType;
        properties[CreatedUtcKey] = DateTimeOffset.UtcNow.ToString("O");
        var result = await FeeTagPropertyStore.TryWriteAndVerifyAsync(feeObject.Guid, properties);
        if (!result.Confirmed)
        {
            ApplicationLogService.Instance.Warning(
                "Container2FEE Visual",
                $"Objekt-Provenienz für '{feeObject.Name}' wurde von FEE nicht bestätigt; " +
                "die fachliche Generierung wird fortgesetzt.",
                result.Warning);
        }
    }

    public static (string? ContainerId, string? ContainerType) Read(
        IReadOnlyDictionary<string, string>? properties,
        IEnumerable<string>? legacyMarks = null)
    {
        if (properties is not null &&
            properties.TryGetValue(SchemaKey, out var schema) &&
            string.Equals(schema, CurrentSchema, StringComparison.Ordinal))
        {
            properties.TryGetValue(ContainerIdKey, out var id);
            properties.TryGetValue(ContainerTypeKey, out var type);
            return (id, type);
        }

        var marks = legacyMarks?.Where(mark => !string.IsNullOrWhiteSpace(mark)).ToArray() ?? [];
        var legacyId = marks.FirstOrDefault(mark => mark.StartsWith(IdPrefix, StringComparison.Ordinal));
        var legacyType = marks.FirstOrDefault(mark => mark.StartsWith(TypePrefix, StringComparison.Ordinal));
        return (
            legacyId?[IdPrefix.Length..],
            legacyType?[TypePrefix.Length..]);
    }
}
