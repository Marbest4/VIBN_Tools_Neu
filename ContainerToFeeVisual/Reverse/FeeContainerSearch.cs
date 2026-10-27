namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>Comma-separated inclusions and !exclusions, matching the workstation search.</summary>
public static class FeeContainerSearch
{
    public static bool Matches(string? query, IEnumerable<string?> values)
    {
        var fields = values.Select(value => Normalize(value ?? "")).ToArray();
        foreach (var rawTerm in (query ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var excluded = rawTerm.StartsWith('!');
            var term = Normalize(excluded ? rawTerm[1..] : rawTerm);
            if (term.Length == 0) continue;
            var found = fields.Any(field => field.Contains(term, StringComparison.Ordinal));
            if (excluded ? found : !found) return false;
        }
        return true;
    }

    private static string Normalize(string value) => new(value.Where(character => char.IsLetterOrDigit(character) || character == '.')
        .Select(char.ToLowerInvariant).ToArray());
}
