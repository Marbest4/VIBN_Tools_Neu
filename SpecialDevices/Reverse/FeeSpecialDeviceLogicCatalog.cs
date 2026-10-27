using VIBN_Tools.GlobalClasses;
using static VIBN_Tools.SpecialDevices.DeviceCatalog;

namespace VIBN_Tools.SpecialDevices;

/// <summary>Shares the real device factory definitions with container discovery.</summary>
public static class FeeSpecialDeviceLogicCatalog
{
    private static readonly Lazy<IReadOnlyList<FeeSpecialDeviceDefinition>> Definitions = new(() =>
        DeviceFactory.DeviceFactoryMap.Select(item => new FeeSpecialDeviceDefinition(item.Key.Item1.ToString(), item.Key.Item2.ToString(),
            item.Value("__discovery__", new SpecialDeviceAddresses(0, 0), RobotType.Kuka).DeviceLogicObject.LogicDefinitionName,
            DeviceMetadata.MetadataMap.TryGetValue(item.Key, out var metadata) && metadata.RequiresRobotType)).ToArray());

    public static bool Contains(string? definition) => !string.IsNullOrWhiteSpace(definition) &&
        Definitions.Value.Any(item => Normalize(item.LogicDefinitionName).Equals(Normalize(definition), StringComparison.OrdinalIgnoreCase));

    public static FeeSpecialDeviceDefinition? Find(string? definition) => string.IsNullOrWhiteSpace(definition) ? null :
        Definitions.Value.FirstOrDefault(item => Normalize(item.LogicDefinitionName).Equals(Normalize(definition), StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string definition) => definition.Trim().Replace('/', '\\').Split('\\').Last().Replace(".xml", "", StringComparison.OrdinalIgnoreCase);
}

public sealed record FeeSpecialDeviceDefinition(string Manufacturer, string DeviceType, string LogicDefinitionName, bool RequiresRobotType);
