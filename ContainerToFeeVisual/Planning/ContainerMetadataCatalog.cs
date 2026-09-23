using VIBN_Tools.ContainerToFee;
using VIBN_Tools.ContainerToFee.General;
using VIBN_Tools.ContainerToFee.GrobStandard;
using VIBN_Tools.GlobalClasses.FeeObjects;
using static VIBN_Tools.GlobalClasses.Interfaces;

namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>
/// Mirrors the container type switch of the legacy reader without invoking
/// its UI error paths. The factories still create the original container
/// classes; no generation behavior is reimplemented here.
/// </summary>
internal static class ContainerMetadataCatalog
{
    private static readonly IReadOnlyDictionary<string, ContainerDescriptor> Descriptors =
        new Dictionary<string, ContainerDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            ["BeltControl"] = Describe<GrobBeltControl_Container>("Grob_BeltControl"),
            ["Button"] = Describe<Button_Container>(simObjectName: "Button"),
            ["Clamping"] = Describe<GrobClamping_Container>("Grob_Clamping"),
            ["Conveyor"] = Describe<GrobConveyor_Container>("Grob_Conveyor"),
            ["Cylinder"] = Describe<GrobCylinder_Container>("Grob_Cylinder", ["Move-Bit-Hilfslogik"]),
            ["FeedSafetyDoor"] = Describe<GrobCylinder_Container>("Grob_Cylinder", ["Move-Bit-Hilfslogik"]),
            ["GripperBasic"] = Describe<GrobGripperBasic_Container>("Grob_GripperBasic", ["Move-Bit-Hilfslogik"]),
            ["GripperVacuum"] = Describe<GrobGripperVacuum_Container>("Grob_GripperVacuum", ["Move-Bit-Hilfslogik"]),
            ["LiftUnit"] = Describe<GrobLiftUnit_Container>("Grob_LiftUnit"),
            ["PneumaticSupply"] = Describe<GrobPneumaticSupply_Container>("Grob_PneumaticSupply"),
            ["ReturnCircuit"] = Describe<SimpleNot_Container>(technicalHelpers: ["Bool-NOT-Hilfslogik"]),
            ["SafeArea"] = Describe<SimpleNot_Container>(technicalHelpers: ["Bool-NOT-Hilfslogik"]),
            ["SafetyDoor"] = Describe<GrobSafetyDoor_Container>("Grob_SafetyDoor"),
            ["Sensor"] = Describe<GrobSensor_Container>("Grob_Sensor"),
            ["SensorX"] = Describe<PartOrientation_Container>(),
            ["Stacklight"] = Describe<Stacklight_Container>(simObjectName: "Segmented Lamp"),
            ["Stop"] = Describe<GrobStop_Container>("Grob_Stop"),
            ["CabinetLamp"] = Describe<CabinetLamp_Container>(technicalHelpers: ["Cabinet Lamps", "CabinetElement"]),
            ["EStop"] = Describe<CabinetEStop_Container>(technicalHelpers: ["Cabinet EStops", "CabinetElement"]),
            ["Fuse"] = Describe<CabinetFuse_Container>(technicalHelpers: ["Cabinet Fuses", "CabinetElement"]),
            ["Switch"] = Describe<CabinetSwitch_Container>(technicalHelpers: ["Cabinet Switches", "CabinetElement"]),
        };

    public static bool TryGet(string xmlType, out ContainerDescriptor descriptor) =>
        Descriptors.TryGetValue(xmlType, out descriptor!);

    public static IReadOnlyList<string> FindXmlTypesByLogicName(string? logicName) =>
        string.IsNullOrWhiteSpace(logicName)
            ? []
            : Descriptors
                .Where(item => string.Equals(
                    item.Value.ExpectedLogicName,
                    logicName,
                    StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Key)
                .ToArray();

    private static ContainerDescriptor Describe<TContainer>(
        string? expectedLogicName = null,
        IReadOnlyList<string>? technicalHelpers = null,
        string? simObjectName = null)
        where TContainer : ContainerBaseClass, new()
    {
        var probe = new TContainer();
        var targets = probe is ISimObjectFindOrSelect selectable
            ? selectable.GetSimObjectTargets()
                .Select((target, index) => new TargetDescriptor(
                    index,
                    target.DisplayName ?? simObjectName ?? target.AllowedType.Name,
                    target.AllowedType,
                    target.AllowMultiSelect))
                .ToArray()
            : [];

        return new ContainerDescriptor(
            typeof(TContainer),
            static () => new TContainer(),
            new HashSet<string>(
                probe.SlotAssignment is null ? Array.Empty<string>() : probe.SlotAssignment.Keys,
                StringComparer.Ordinal),
            expectedLogicName,
            technicalHelpers ?? [],
            targets,
            probe is ICreatableContainer);
    }
}

internal sealed record ContainerDescriptor(
    Type RuntimeType,
    Func<ContainerBaseClass> Factory,
    IReadOnlySet<string> Slots,
    string? ExpectedLogicName,
    IReadOnlyList<string> TechnicalHelpers,
    IReadOnlyList<TargetDescriptor> Targets,
    bool SupportsCreation);

internal sealed record TargetDescriptor(
    int Index,
    string DisplayName,
    Type AllowedType,
    bool AllowMultiSelect);
