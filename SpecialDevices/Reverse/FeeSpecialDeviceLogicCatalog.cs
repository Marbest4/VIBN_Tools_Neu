using static VIBN_Tools.SpecialDevices.DeviceCatalog;

namespace VIBN_Tools.SpecialDevices;

/// <summary>Shares the real device factory definitions with container discovery.</summary>
public static class FeeSpecialDeviceLogicCatalog
{
    private static readonly Lazy<IReadOnlySet<string>> Definitions = new(() =>
        DeviceFactory.DeviceFactoryMap.Values.Select(factory =>
            factory("__discovery__", new SpecialDeviceAddresses(0, 0), RobotType.Kuka).DeviceLogicObject.LogicDefinitionName)
            .Where(name => !string.IsNullOrWhiteSpace(name)).Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase));

    public static bool Contains(string? definition) => !string.IsNullOrWhiteSpace(definition) &&
        Definitions.Value.Contains(Normalize(definition));

    private static string Normalize(string definition) => definition.Trim().Replace('/', '\\').Split('\\').Last().Replace(".xml", "", StringComparison.OrdinalIgnoreCase);
}
