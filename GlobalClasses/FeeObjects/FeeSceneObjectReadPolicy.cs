namespace VIBN_Tools.GlobalClasses.FeeObjects;

/// <summary>
/// Defines scene-object types that are deliberately excluded from the shared
/// ModelValidation/Container2FEE/FEE2Container snapshots. Feature-specific
/// readers such as CAD Wizard remain independent from this policy.
/// </summary>
public static class FeeSceneObjectReadPolicy
{
    public const string DecorationTypeName = "Decoration";

    public static bool IsIgnoredType(string? sceneObjectType)
    {
        var normalized = sceneObjectType?.Trim();
        return string.Equals(normalized, DecorationTypeName, StringComparison.OrdinalIgnoreCase) ||
               (normalized?.EndsWith(
                   $".{DecorationTypeName}",
                   StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public static bool IsIgnoredObject(FeeAbstractObject? sceneObject) =>
        sceneObject is FeeDecoration ||
        IsIgnoredType(sceneObject?.FeeType);
}
