using FS.SDK.Mathematics;

namespace VIBN_Tools.ContainerToFee;

/// <summary>
/// Dimensions used by the original Container2FEE generator. The visual
/// workflow delegates to the same generator and must not introduce a second
/// set of SimObject defaults.
/// </summary>
public static class ContainerGeneratedObjectDefaults
{
    public static Vector3 ButtonScale => new(0.5f, 0.5f, 0.5f);
    public static Vector3 ConveyorSurfaceScale => new(2f, 0.5f, 0.05f);
    public static Vector3 MotionJointScale => new(0.5f, 0.5f, 0.5f);
    public static Vector3 PickAndPlaceScale => new(0.1f, 0.1f, 0.1f);
    public static Vector3 SensorScale => new(0.01f, 0.03f, 0.01f);
    public static Vector3 StopFloorScale => new(0.01f, 0.2f, 0.05f);
    public static Vector3 GripperPartTypeSensorScale => new(0.1f, 0.1f, 0.05f);

    // ModelValidation rejects zero operation times and identical end
    // positions. These fallbacks are used only when the ContainerFile does not
    // provide a value; they remain editable in Model Validation afterwards.
    public const float ConveyorVelocity = 1f;
    public const float MotionHomePosition = 0f;
    public const float MotionWorkPosition = 1f;
    public const float MotionOperationTime = 1f;
    public const float GripperUnclampedPosition = 0f;
    public const float GripperClampedPosition = 0.1f;
}
