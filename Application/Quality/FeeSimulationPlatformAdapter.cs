using VIBN_Tools.Quality;
using VIBN_Tools.Settings;

namespace VIBN_Tools.Application.Quality;

/// <summary>Exposes only the connection capability actually confirmed by the shared FEE client.</summary>
public sealed class FeeSimulationPlatformAdapter(FeeConnectionService connection) : ISimulationPlatformAdapter
{
    public string PlatformId => "FEE";

    public string DisplayName => "fe.screen-sim";

    public Task<SimulationAdapterProbe> ProbeAsync(
        ProjectProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configured = profile.SimulationTools.FirstOrDefault(tool =>
            string.Equals(tool.PlatformId, PlatformId, StringComparison.OrdinalIgnoreCase));
        var enabled = configured?.Enabled == true;
        var connected = enabled && connection.CanUseFeeFeatures;
        return Task.FromResult(new SimulationAdapterProbe(
            PlatformId,
            DisplayName,
            connected,
            connected,
            connected
                ? [SimulationCapability.DiscoverModel, SimulationCapability.GenerateModel,
                    SimulationCapability.LinkSignals, SimulationCapability.ValidateModel]
                : [],
            !enabled
                ? "Im Projektprofil nicht aktiviert."
                : connected
                    ? "Gemeinsamer FEE-Client meldet eine bestätigte Verbindung. Eine Modellsimulation wurde dadurch noch nicht ausgeführt."
                    : FeeConnectionService.MissingConnectionMessage));
    }
}
