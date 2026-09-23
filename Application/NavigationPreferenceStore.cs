using System.IO;
using System.Text.Json;

namespace VIBN_Tools.Application;

public interface INavigationPreferenceStore
{
    bool LoadExpanded();

    void SaveExpanded(bool expanded);
}

/// <summary>Persists the per-user navigation width preference atomically.</summary>
public sealed class JsonNavigationPreferenceStore : INavigationPreferenceStore
{
    private readonly string _filePath;

    public JsonNavigationPreferenceStore(string? filePath = null)
    {
        _filePath = Path.GetFullPath(filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GROB",
            "VIBN_Tools",
            "navigation-preferences.json"));
    }

    public bool LoadExpanded()
    {
        try
        {
            if (!File.Exists(_filePath))
                return true;
            var model = JsonSerializer.Deserialize<NavigationPreference>(File.ReadAllText(_filePath));
            return model?.IsExpanded ?? true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return true;
        }
    }

    public void SaveExpanded(bool expanded)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new NavigationPreference(expanded)));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    private sealed record NavigationPreference(bool IsExpanded);
}
