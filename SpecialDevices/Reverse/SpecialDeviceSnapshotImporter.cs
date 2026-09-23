using static VIBN_Tools.SpecialDevices.DeviceCatalog;
using VIBN_Tools.GlobalClasses;

namespace VIBN_Tools.SpecialDevices;

public sealed record SpecialDeviceSnapshotImportResult(
    bool Success,
    SpecialDevice? Device,
    IReadOnlyList<string> Warnings,
    string Error);

/// <summary>
/// Converts a checked reverse snapshot into the established device factory
/// model. Signal differences are reported because the factory remains the
/// authoritative definition for a new FEE generation.
/// </summary>
public static class SpecialDeviceSnapshotImporter
{
    public static SpecialDeviceSnapshotImportResult Create(FeeSpecialDeviceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Enum.TryParse<DeviceManufacturer>(snapshot.Manufacturer, true, out var manufacturer))
            return Failure($"Unbekannter Hersteller '{snapshot.Manufacturer}'.");
        if (!DeviceTypeEnums.TryGetValue(manufacturer, out var deviceTypeEnum))
            return Failure($"Für Hersteller '{manufacturer}' ist kein Gerätetypkatalog vorhanden.");

        Enum deviceType;
        try
        {
            deviceType = (Enum)Enum.Parse(deviceTypeEnum, snapshot.DeviceType, ignoreCase: true);
        }
        catch (ArgumentException)
        {
            return Failure($"Gerätetyp '{snapshot.DeviceType}' ist für '{manufacturer}' nicht bekannt.");
        }

        RobotType? robotType = null;
        if (!string.IsNullOrWhiteSpace(snapshot.RobotType))
        {
            if (!Enum.TryParse<RobotType>(snapshot.RobotType, true, out var parsedRobot))
                return Failure($"Robotertyp '{snapshot.RobotType}' ist nicht bekannt.");
            robotType = parsedRobot;
        }

        if (DeviceMetadata.MetadataMap.TryGetValue((manufacturer, deviceType), out var metadata) &&
            metadata.RequiresRobotType && robotType is null)
        {
            return Failure($"{manufacturer} / {deviceType} benötigt einen Robotertyp.");
        }

        try
        {
            var device = DeviceFactory.Create(
                manufacturer,
                deviceType,
                snapshot.Prefix.Trim(),
                new SpecialDeviceAddresses(snapshot.InputByte, snapshot.OutputByte),
                robotType);
            var expected = (device.DeviceSignals ?? Array.Empty<VIBN_Tools.GlobalClasses.FeeObjects.FeeInterfaceSignal>())
                .Select(signal => $"{signal.Tag}|{signal.Address}|{signal.IOType}|{signal.Usage}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var actual = snapshot.Signals
                .Select(signal => $"{signal.Tag}|{signal.Address}|{signal.DataType}|{signal.Usage}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var warnings = expected.SetEquals(actual)
                ? Array.Empty<string>()
                : new[]
                {
                    "Die aktuellen FEE-Signale weichen von der katalogisierten Gerätedefinition ab. " +
                    "Die Warteschlange verwendet bei erneuter Erzeugung die katalogisierte Definition; " +
                    "den JSON-Snapshot für den Soll-Ist-Vergleich aufbewahren."
                };
            return new SpecialDeviceSnapshotImportResult(true, device, warnings, string.Empty);
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException)
        {
            return Failure(exception.Message);
        }
    }

    private static SpecialDeviceSnapshotImportResult Failure(string error) =>
        new(false, null, Array.Empty<string>(), error);
}
