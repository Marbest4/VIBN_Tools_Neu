namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>Identifies generated helper instances without accepting a helper owned by another container.</summary>
public static class VisualFeeTechnicalHelperResolver
{
    public static bool IsExpectedType(string helperName, string objectType)
    {
        static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant).ToArray());
        var expected = Normalize(helperName);
        var actual = Normalize(objectType);
        return expected.Contains("BOOLNOT", StringComparison.Ordinal) && actual.EndsWith("BOOLNOT", StringComparison.Ordinal) ||
            expected.Contains("MOVEBIT", StringComparison.Ordinal) && actual.EndsWith("MOVEBIT", StringComparison.Ordinal);
    }

    public static bool MatchesIdentity(VisualFeeContainerObject helper, VisualNode container) =>
        !string.IsNullOrWhiteSpace(helper.ProvenanceContainerId)
            ? string.Equals(helper.ProvenanceContainerId, container.Id, StringComparison.Ordinal)
            : string.Equals(helper.Name, container.Name, StringComparison.OrdinalIgnoreCase);
}
