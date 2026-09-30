using VIBN_Tools.ContainerToFee.GrobStandard;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFee;

public enum ContainerPreflightSeverity
{
    Warning,
    Error,
}

public sealed record ContainerPreflightIssue(
    ContainerPreflightSeverity Severity,
    string Code,
    string Message);

/// <summary>
/// Checks the deterministic prerequisites that the current ModelValidation
/// applies to generated logic and its simulation-object links. Spatial
/// placement and project-specific Pick/Drop marks cannot be inferred from a
/// ContainerFile and are therefore reported separately as manual follow-up.
/// </summary>
public static class ContainerModelValidationPreflight
{
    public static IReadOnlyList<ContainerPreflightIssue> Validate(ContainerBaseClass container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var issues = new List<ContainerPreflightIssue>();

        switch (container)
        {
            case GrobBeltControl_Container belt:
                Require(Has(belt, belt.Signal_BeltControlState, nameof(belt.Signal_BeltControlState)), "BELT_STATE_MISSING",
                    "PLC_IN_BeltControlState fehlt.", issues);
                issues.Add(Warning(
                    "BELT_AXIS_LINK_MANUAL",
                    "PLC_OUT_AxisValue muss nach der Erzeugung mit der zugehörigen Achslogik verbunden werden; " +
                    "diese Beziehung ist im ContainerFile nicht enthalten."));
                break;

            case GrobClamping_Container clamping:
                Require(clamping.Signal_ReleaseClamping is not null, "CLAMPING_CONTROL_MISSING",
                    "PLC_OUT_ReleaseClamping fehlt.", issues);
                Require(Has(clamping, clamping.Signal_ClampingReleased, nameof(clamping.Signal_ClampingReleased)), "CLAMPING_STATUS_MISSING",
                    "PLC_IN_ClampingReleased fehlt.", issues);
                break;

            case GrobConveyor_Container conveyor:
                var discreteControl = Has(conveyor, conveyor.Signal_Clockwise, nameof(conveyor.Signal_Clockwise)) &&
                                      Has(conveyor, conveyor.Signal_CounterClockwise, nameof(conveyor.Signal_CounterClockwise));
                var wordControl = Has(conveyor, conveyor.Signal_ControlWord, nameof(conveyor.Signal_ControlWord)) &&
                                  Has(conveyor, conveyor.Signal_StatusWord, nameof(conveyor.Signal_StatusWord));
                Require(discreteControl || wordControl, "CONVEYOR_CONTROL_INCOMPLETE",
                    "Es wird entweder Clockwise und CounterClockwise oder ControlWord und StatusWord benötigt.", issues);
                Require(conveyor.Surfaces_Conveyor.Count > 0 || conveyor.IsCreationRequested,
                    "CONVEYOR_SURFACE_MISSING",
                    "Für SIM_Velocity ist eine vorhandene oder neu zu erzeugende Surface erforderlich.", issues);
                break;

            case GrobCylinder_Container cylinder:
                Require(cylinder.Signal_ToHomePos is not null || cylinder.Signal_ToWorkPos is not null,
                    "CYLINDER_CONTROL_MISSING",
                    "Mindestens ToHomePos oder ToWorkPos fehlt.", issues);
                Require(HasAny(cylinder.Signals_InHomePos) || HasAny(cylinder.Signals_InWorkPos),
                    "CYLINDER_STATUS_MISSING",
                    "Mindestens InHomePos oder InWorkPos fehlt.", issues);
                Require(cylinder.Joints_Cylinder.Count > 0 || cylinder.IsCreationRequested,
                    "CYLINDER_JOINT_MISSING",
                    "Für die Positions-, Ziel- und Geschwindigkeitsverknüpfung ist ein MotionJoint erforderlich.", issues);
                break;

            case GrobGripperBasic_Container gripper:
                Require(gripper.Signal_Clamp is not null || gripper.Signal_Unclamp is not null,
                    "GRIPPER_CONTROL_MISSING",
                    "Mindestens Clamp oder Unclamp fehlt.", issues);
                Require(HasAny(gripper.Signals_Clamped) || HasAny(gripper.Signals_Unclamped),
                    "GRIPPER_STATUS_MISSING",
                    "Mindestens Clamped oder Unclamped fehlt.", issues);
                Require(gripper.Joints_Gripper.Count > 0 || gripper.IsCreationRequested,
                    "GRIPPER_JOINT_MISSING",
                    "Für die Positions-, Ziel- und Geschwindigkeitsverknüpfung ist ein MotionJoint erforderlich.", issues);
                if (gripper.IsCreationRequested && gripper.PickPlacers_Gripper.Count == 0)
                    issues.Add(PickAndPlaceMarksWarning());
                break;

            case GrobGripperVacuum_Container vacuum:
                Require(vacuum.PickPlacers_Gripper.Count > 0 || vacuum.IsCreationRequested,
                    "VACUUM_PICK_PLACE_MISSING",
                    "Für SIM_Pick und SIM_Drop ist ein PickAndPlace-Objekt erforderlich.", issues);
                if (vacuum.IsCreationRequested && vacuum.PickPlacers_Gripper.Count == 0)
                    issues.Add(PickAndPlaceMarksWarning());
                break;

            case GrobLiftUnit_Container lift:
                Require(lift.Signal_ToHomePos is not null && lift.Signal_ToWorkPos is not null,
                    "LIFT_CONTROL_INCOMPLETE",
                    "ToHomePos und ToWorkPos werden beide benötigt.", issues);
                Require(
                    Has(lift, lift.Signal_InHomePos, nameof(lift.Signal_InHomePos)) &&
                    Has(lift, lift.Signal_InWorkPos, nameof(lift.Signal_InWorkPos)),
                    "LIFT_STATUS_INCOMPLETE",
                    "InHomePos und InWorkPos werden beide benötigt.", issues);
                Require(lift.Joints_LiftUnit.Count > 0 || lift.IsCreationRequested,
                    "LIFT_JOINT_MISSING",
                    "Für die Positions-, Ziel- und Geschwindigkeitsverknüpfung ist ein MotionJoint erforderlich.", issues);
                break;

            case GrobSafetyDoor_Container door:
                Require(door.Signal_Unlock is not null, "SAFETY_DOOR_UNLOCK_MISSING",
                    "PLC_OUT_Unlock fehlt.", issues);
                Require(Has(door, door.Signal_Unlocked, nameof(door.Signal_Unlocked)), "SAFETY_DOOR_UNLOCKED_MISSING",
                    "PLC_IN_Unlocked fehlt.", issues);
                Require(
                    Has(door, door.Signal_Closed_Ch1, nameof(door.Signal_Closed_Ch1)) ||
                    Has(door, door.Signal_Closed_Ch2, nameof(door.Signal_Closed_Ch2)),
                    "SAFETY_DOOR_CLOSED_MISSING",
                    "Mindestens ein Closed-Kanal fehlt.", issues);
                Require(
                    Has(door, door.Signal_ClosedAndLocked, nameof(door.Signal_ClosedAndLocked)) ||
                    Has(door, door.Signal_ClosedAndLocked_Ch1, nameof(door.Signal_ClosedAndLocked_Ch1)) ||
                    Has(door, door.Signal_ClosedAndLocked_Ch2, nameof(door.Signal_ClosedAndLocked_Ch2)),
                    "SAFETY_DOOR_LOCKED_MISSING",
                    "Mindestens eine ClosedAndLocked-Rückmeldung fehlt.", issues);
                break;

            case GrobSensor_Container sensor:
                Require(sensor.Sensor is not null || sensor.IsCreationRequested,
                    "SENSOR_OBJECT_MISSING",
                    "Für SIM_SensorValue ist ein vorhandener oder neu zu erzeugender Sensor erforderlich.", issues);
                break;

            case GrobStop_Container stop:
                Require(stop.Signal_Open is not null || stop.Signal_Close is not null,
                    "STOP_CONTROL_MISSING",
                    "Mindestens Open oder Close fehlt.", issues);
                Require(
                    Has(stop, stop.Signal_Opened, nameof(stop.Signal_Opened)) ||
                    Has(stop, stop.Signal_Closed, nameof(stop.Signal_Closed)),
                    "STOP_STATUS_MISSING",
                    "Mindestens Opened oder Closed fehlt.", issues);
                Require(stop.Floors_Stop.Count > 0 || stop.IsCreationRequested,
                    "STOP_FLOOR_MISSING",
                    "Für SIM_Collision ist ein vorhandener oder neu zu erzeugender Floor erforderlich.", issues);
                break;
        }

        return issues;
    }

    private static bool HasAny<T>(IEnumerable<T>? values) => values?.Any() == true;

    private static bool Has(
        ContainerBaseClass container,
        FeeInterfaceSignal? signal,
        string propertyName) =>
        signal is not null || container.HasAssignedSignalsForProperty(propertyName);

    private static void Require(
        bool condition,
        string code,
        string message,
        ICollection<ContainerPreflightIssue> issues)
    {
        if (!condition)
            issues.Add(new ContainerPreflightIssue(ContainerPreflightSeverity.Error, code, message));
    }

    private static ContainerPreflightIssue Warning(string code, string message) =>
        new(ContainerPreflightSeverity.Warning, code, message);

    private static ContainerPreflightIssue PickAndPlaceMarksWarning() =>
        Warning(
            "PICK_PLACE_MARKS_MANUAL",
            "Pick/Drop-Marks und die reale Position sind projektspezifisch und müssen vor der abschließenden " +
            "ModelValidation gesetzt werden.");
}
