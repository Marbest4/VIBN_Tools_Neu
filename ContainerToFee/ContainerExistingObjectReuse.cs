using System.Reflection;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFee;

/// <summary>Shared, explicit injection point for reusing an existing container logic.</summary>
internal static class ContainerExistingObjectReuse
{
    public static bool TryAssignLogic(ContainerBaseClass container, FeeLogic logic)
    {
        var property = FindLogicProperty(container);
        if (property is null)
            return false;
        property.SetValue(container, logic);
        return true;
    }

    public static FeeLogic? GetAssignedLogic(ContainerBaseClass container) =>
        FindLogicProperty(container)?.GetValue(container) as FeeLogic;

    public static bool TryAssignCabinetElement(ContainerBaseClass container, FeeCabinetElement element)
    {
        var property = FindCabinetElementProperty(container);
        if (property is null)
            return false;
        property.SetValue(container, element);
        return true;
    }

    public static FeeCabinetElement? GetAssignedCabinetElement(ContainerBaseClass container) =>
        FindCabinetElementProperty(container)?.GetValue(container) as FeeCabinetElement;

    public static string? GetExpectedCabinetElementType(ContainerBaseClass container) => container switch
    {
        General.CabinetSwitch_Container => FeeCabinetElement.CabinetElementType.PositionSwitch2,
        General.CabinetFuse_Container => FeeCabinetElement.CabinetElementType.Fuse,
        General.CabinetEStop_Container => FeeCabinetElement.CabinetElementType.EStop,
        General.CabinetLamp_Container => FeeCabinetElement.CabinetElementType.LampYellow,
        _ => null,
    };

    private static PropertyInfo? FindLogicProperty(ContainerBaseClass container) =>
        container.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(candidate => candidate.CanWrite && candidate.PropertyType == typeof(FeeLogic));

    private static PropertyInfo? FindCabinetElementProperty(ContainerBaseClass container) =>
        container.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(candidate => candidate.CanWrite && candidate.PropertyType == typeof(FeeCabinetElement));
}
