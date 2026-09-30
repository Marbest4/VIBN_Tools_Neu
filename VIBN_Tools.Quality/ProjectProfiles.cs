using System.Text.Json;

namespace VIBN_Tools.Quality;

public sealed record SimulationToolConfiguration(
    string PlatformId,
    bool Enabled,
    string InstallationPath,
    string ProjectPath);

public sealed record ProjectProfile(
    string Id,
    string Name,
    string Customer,
    string ProjectRoot,
    string RequirementsPath,
    string ContainerPath,
    string TiaVersion,
    string VicoLibraryPath,
    string RockwellStandard,
    IReadOnlyList<string> AllowedContainerTypes,
    IReadOnlyDictionary<string, string> NamingRules,
    IReadOnlyList<string> SignalAddressRanges,
    IReadOnlyList<SimulationToolConfiguration> SimulationTools)
{
    public static ProjectProfile Create(string name) => new(
        Guid.NewGuid().ToString("N"),
        name,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        "GCCS",
        [],
        new Dictionary<string, string>(),
        [],
        [
            new SimulationToolConfiguration("FEE", true, string.Empty, string.Empty),
            new SimulationToolConfiguration("Emulate3D", false, string.Empty, string.Empty),
            new SimulationToolConfiguration("EKS", false, string.Empty, string.Empty),
        ]);
}

public sealed record ProjectProfileCollection(string? SelectedProfileId, IReadOnlyList<ProjectProfile> Profiles);

public static class ProjectProfileValidator
{
    public static IReadOnlyList<QualityFinding> Validate(ProjectProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var findings = new List<QualityFinding>();
        AddRequired(findings, "Name", profile.Name);
        AddOptionalPath(findings, "Projektwurzel", profile.ProjectRoot, directory: true);
        AddOptionalPath(findings, "Requirements", profile.RequirementsPath, directory: false);
        AddOptionalPath(findings, "ContainerFile", profile.ContainerPath, directory: false);
        AddOptionalPath(findings, "ViCo-Bibliothek", profile.VicoLibraryPath, directory: true);
        foreach (var duplicate in profile.SimulationTools.GroupBy(tool => tool.PlatformId, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            findings.Add(new QualityFinding("Projektprofil", "PROFILE_DUPLICATE_PLATFORM", QualityStatus.Failed, $"Simulationsplattform '{duplicate.Key}' ist mehrfach konfiguriert."));
        return findings;
    }

    private static void AddRequired(List<QualityFinding> findings, string field, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            findings.Add(new QualityFinding("Projektprofil", "PROFILE_REQUIRED", QualityStatus.Failed, $"Pflichtfeld '{field}' fehlt."));
    }

    private static void AddOptionalPath(List<QualityFinding> findings, string field, string value, bool directory)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            findings.Add(new QualityFinding("Projektprofil", "PROFILE_PATH_EMPTY", QualityStatus.Warning, $"Pfad '{field}' ist nicht konfiguriert."));
            return;
        }
        var exists = directory ? Directory.Exists(value) : File.Exists(value);
        if (!exists)
            findings.Add(new QualityFinding("Projektprofil", "PROFILE_PATH_MISSING", QualityStatus.Failed, $"Pfad '{field}' wurde nicht gefunden: {value}"));
    }
}

public sealed class JsonProjectProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public JsonProjectProfileStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VIBN_Tools",
            "quality",
            "project-profiles.json");
    }

    public ProjectProfileCollection Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<ProjectProfileCollection>(File.ReadAllText(_path), JsonOptions) ?? new(null, [])
                : new(null, []);
        }
        catch (JsonException)
        {
            return new(null, []);
        }
    }

    public void Save(ProjectProfileCollection profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var duplicate = profiles.Profiles.GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Projektprofil-ID '{duplicate.Key}' ist doppelt vorhanden.");
        AtomicJsonFile.Write(_path, profiles, JsonOptions);
    }
}
