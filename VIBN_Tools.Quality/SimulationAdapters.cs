namespace VIBN_Tools.Quality;

public enum SimulationCapability
{
    DiscoverModel,
    GenerateModel,
    LinkSignals,
    RunScenarios,
    ValidateModel,
}

public sealed record SimulationAdapterProbe(
    string PlatformId,
    string DisplayName,
    bool IsAvailable,
    bool IsLiveVerified,
    IReadOnlyList<SimulationCapability> Capabilities,
    string Message);

public interface ISimulationPlatformAdapter
{
    string PlatformId { get; }

    string DisplayName { get; }

    Task<SimulationAdapterProbe> ProbeAsync(ProjectProfile profile, CancellationToken cancellationToken = default);
}

/// <summary>
/// Conservative readiness adapter. It verifies configured files only and never
/// claims API capabilities which have not been provided and exercised.
/// </summary>
public sealed class ExternalSimulationReadinessAdapter(
    string platformId,
    string displayName,
    IReadOnlyList<SimulationCapability> documentedCapabilities) : ISimulationPlatformAdapter
{
    public string PlatformId { get; } = platformId;
    public string DisplayName { get; } = displayName;

    public Task<SimulationAdapterProbe> ProbeAsync(ProjectProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = profile.SimulationTools.FirstOrDefault(tool =>
            string.Equals(tool.PlatformId, PlatformId, StringComparison.OrdinalIgnoreCase));
        if (configuration is null || !configuration.Enabled)
        {
            return Task.FromResult(new SimulationAdapterProbe(
                PlatformId, DisplayName, false, false, [],
                "Im Projektprofil nicht aktiviert."));
        }
        var installationExists = File.Exists(configuration.InstallationPath) || Directory.Exists(configuration.InstallationPath);
        var projectExists = string.IsNullOrWhiteSpace(configuration.ProjectPath) ||
                            File.Exists(configuration.ProjectPath) || Directory.Exists(configuration.ProjectPath);
        var available = installationExists && projectExists;
        return Task.FromResult(new SimulationAdapterProbe(
            PlatformId,
            DisplayName,
            available,
            false,
            available ? documentedCapabilities : [],
            available
                ? "Installation und Projektpfad gefunden. Hersteller-API-Live-Abnahme ist noch offen."
                : "Konfigurierter Installations- oder Projektpfad wurde nicht gefunden."));
    }
}

public static class SimulationAdapterCatalog
{
    public static IReadOnlyList<ISimulationPlatformAdapter> CreateDefaults() =>
    [
        new ExternalSimulationReadinessAdapter(
            "Emulate3D",
            "FactoryTalk Emulate3D",
            [SimulationCapability.DiscoverModel, SimulationCapability.LinkSignals, SimulationCapability.RunScenarios, SimulationCapability.ValidateModel]),
        new ExternalSimulationReadinessAdapter(
            "EKS",
            "EKS RF::SUITE",
            [SimulationCapability.DiscoverModel, SimulationCapability.LinkSignals, SimulationCapability.ValidateModel]),
    ];
}
