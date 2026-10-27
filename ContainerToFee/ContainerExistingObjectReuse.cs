using System.Reflection;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFee;

/// <summary>Shared, explicit injection point for reusing an existing container logic.</summary>
internal static class ContainerExistingObjectReuse
{
    public static FeeCabinetElement[] FindCabinetElements(ContainerBaseClass container, IEnumerable<FeeCabinetElement> source)
    {
        var elements = source.ToArray();
        var expected = GetExpectedCabinetElementType(container);
        var hasParent = Guid.TryParse(container.ParentCabinetGuid, out var parentGuid) && elements.Any(item => item.Parent?.Guid == parentGuid);
        var compatible = elements.Where(item => SameDefinition(item.ElementType, expected) &&
            (hasParent ? item.Parent?.Guid == parentGuid : string.IsNullOrWhiteSpace(container.ParentCabinetName) ||
                string.Equals(item.Parent?.Name, container.ParentCabinetName, StringComparison.OrdinalIgnoreCase))).ToArray();
        var exact = Guid.TryParse(container.SourcePrimaryObjectGuid, out var guid) ? compatible.Where(item => item.Guid == guid).ToArray() : [];
        return exact.Length > 0 ? exact : compatible.Where(item => string.Equals(item.Name, container.ComponentName, StringComparison.OrdinalIgnoreCase)).ToArray();

        static bool SameDefinition(string? actual, string? wanted) => !string.IsNullOrWhiteSpace(actual) && !string.IsNullOrWhiteSpace(wanted) &&
            string.Equals(actual.Replace('/', '\\').Split('\\').Last().Replace(".xml", "", StringComparison.OrdinalIgnoreCase),
                wanted.Replace('/', '\\').Split('\\').Last().Replace(".xml", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
    }

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
