using VIBN_Tools.ContainerToFee;
using System.Collections.Concurrent;

namespace VIBN_Tools.ContainerToFeeVisual;

internal static class ContainerSignalSlotMap
{
    private static readonly ConcurrentDictionary<(string Type, string Slot), string> RuntimeSlots = new();
    internal static string RuntimeSlot(ContainerBaseClass container, string xmlType, string xmlSlot) => xmlType switch
    {
        "ReturnCircuit" or "SafeArea" => xmlSlot == "PLC_OUT_Signal" ? "Input 01" : "Output 01",
        "Button" => xmlSlot == "PLC_IN_NO" ? "Pressed" : "PressedInverted",
        "Stacklight" => xmlSlot.Replace("PLC_NO_", "", StringComparison.Ordinal),
        "CabinetSwitch" or "Switch" or "EStop" => xmlSlot.Replace("PLC_IN_", "", StringComparison.Ordinal),
        "CabinetFuse" or "Fuse" => xmlSlot.Replace("PLC_IN_", "", StringComparison.Ordinal),
        "CabinetLamp" => "On",
        _ => CanonicalLogicSlot(container, xmlSlot),
    };

    private static string CanonicalLogicSlot(ContainerBaseClass container, string xmlSlot)
    {
        if (!container.SlotAssignment.TryGetValue(xmlSlot, out var property) || property is null) return xmlSlot;
        return container.SlotAssignment.FirstOrDefault(item => item.Value?.Name == property.Name).Key ?? xmlSlot;
    }

    internal static bool Matches(string xmlType, string xmlSlot, string runtimeSlot)
    {
        if (!ContainerMetadataCatalog.TryGet(xmlType, out var descriptor)) return false;
        var expected = RuntimeSlots.GetOrAdd((xmlType, xmlSlot), key => RuntimeSlot(descriptor.Factory(), key.Type, key.Slot));
        return string.Equals(expected.Trim(), runtimeSlot.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
