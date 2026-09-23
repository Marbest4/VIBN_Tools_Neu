using System.IO;

namespace VIBN_Tools.Application;

/// <summary>Creates a safe base name for user-selected export files.</summary>
public static class ExportFileNamePolicy
{
    public static string Create(string? value, string fallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);

        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }
}
