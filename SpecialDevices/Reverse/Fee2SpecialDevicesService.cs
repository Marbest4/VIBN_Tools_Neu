using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.Settings;

namespace VIBN_Tools.SpecialDevices;

public sealed record Fee2SpecialDeviceRoot(
    Guid Guid, string Name, FeeSpecialDeviceSnapshot Snapshot, int UpdatedSignalCount, int MissingSignalCount,
    bool HasProvenance = false, Fee2SpecialDeviceSignalCoverage? SignalCoverage = null,
    string LogicDefinitionName = "", string ParentName = "", int? CurrentInputByte = null, int? CurrentOutputByte = null)
{
    public string SourceKind => "Aktuelle FEE-Struktur";
    public int InputSignalCount => SignalCoverage?.InputSignalCount ?? 0;
    public int ConnectedInputSignalCount => SignalCoverage?.ConnectedInputSignalCount ?? 0;
    public int OutputSignalCount => SignalCoverage?.OutputSignalCount ?? 0;
    public int ConnectedOutputSignalCount => SignalCoverage?.ConnectedOutputSignalCount ?? 0;
    public int CurrentSignalCount => Snapshot.Signals.Select(signal => signal.VariableGuid).Distinct().Count();
    public int ExpectedSignalCount => CurrentSignalCount + MissingSignalCount;
    public string MissingPlcSlots => string.Join(", ", SignalCoverage?.MissingSlots ?? []);
    public string InputByteText => CurrentInputByte?.ToString() ?? "—";
    public string OutputByteText => CurrentOutputByte?.ToString() ?? "—";
    public IReadOnlyList<FeeSpecialDeviceSignalSnapshot> LinkedSignals => Snapshot.Signals;
}

public sealed record Fee2SpecialDeviceSignalCoverage(
    int InputSignalCount,
    int ConnectedInputSignalCount,
    int OutputSignalCount,
    int ConnectedOutputSignalCount,
    IReadOnlyList<string> MissingSlots);

public sealed record Fee2SpecialDevicesProgress(int Current, int Total, string Message);

public sealed record Fee2SpecialDeviceDiscoveryIssue(Guid? Guid, string RootName, string Message);

public sealed record Fee2SpecialDeviceDiscoveryResult(
    IReadOnlyList<Fee2SpecialDeviceRoot> Roots,
    int IgnoredWithoutProvenance,
    IReadOnlyList<Fee2SpecialDeviceDiscoveryIssue> Issues);

/// <summary>Uses the same fresh scene/variable/route snapshot as FEE2Container.</summary>
public sealed class Fee2SpecialDevicesService
{
    public async Task<Fee2SpecialDeviceDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default,
        IProgress<Fee2SpecialDevicesProgress>? progress = null)
    {
        if (Services.Connection?.CanUseFeeFeatures != true || Services.ApiInstance is null)
            throw new InvalidOperationException(FeeConnectionService.MissingConnectionMessage);
        var forwarding = new InlineProgress<Fee2ContainerProgress>(item =>
            progress?.Report(new Fee2SpecialDevicesProgress(item.Percent, 100, item.Message)));
        var scene = await new Fee2ContainerService().DiscoverAsync(cancellationToken, reconstructLegacyRoots: true, progress: forwarding);
        var devices = scene.Roots.SelectMany(root => root.SpecialDevices ?? []).DistinctBy(item => item.Guid)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var issues = scene.Issues.Select(item => new Fee2SpecialDeviceDiscoveryIssue(item.Guid, item.RootName, item.Message)).ToList();
        foreach (var device in devices)
        {
            if (device.CurrentInputByte is null || device.CurrentOutputByte is null)
                issues.Add(new(device.Guid, device.Name, "Eine Basisadresse ist aus den aktuellen verknüpften Signaladressen nicht ableitbar; in der Ansicht wird — angezeigt."));
            if (FeeSpecialDeviceLogicCatalog.Find(device.LogicDefinitionName)?.RequiresRobotType == true && device.Snapshot.RobotType is null)
                issues.Add(new(device.Guid, device.Name, "Robotertyp ist aus den aktuellen Adressen nicht eindeutig ableitbar. Die Live-Signale werden trotzdem angezeigt."));
        }
        return new Fee2SpecialDeviceDiscoveryResult(devices, 0, issues);
    }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    { public void Report(T value) => report(value); }
}
