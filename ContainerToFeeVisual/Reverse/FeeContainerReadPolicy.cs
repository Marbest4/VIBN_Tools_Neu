namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>Reverse-container exclusions do not affect generation or SpecialDevices.</summary>
public static class FeeContainerReadPolicy
{
    public static bool IsIgnored(string? feeType, string? name)
    {
        var type = (feeType ?? "").Split('.').Last();
        if (new[] { "Decoration", "SceneObjectCompounder", "CADAssembly" }.Contains(type, StringComparer.OrdinalIgnoreCase)) return true;
        var joint = (name ?? "").Trim();
        return string.Equals(type, "MotionJoint", StringComparison.OrdinalIgnoreCase) && joint.Length == 2 &&
            char.ToLowerInvariant(joint[0]) == 'j' && joint[1] is >= '1' and <= '9';
    }
}
