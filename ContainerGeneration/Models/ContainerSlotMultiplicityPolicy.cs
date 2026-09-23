namespace VIBN_Tools.ContainerGeneration.Models;

/// <summary>
/// Central business rule for repeated logical slots. PLC outputs are
/// exclusive. PLC inputs may fan in, but Container2FEE must route every
/// signal through its own Move object.
/// </summary>
public static class ContainerSlotMultiplicityPolicy
{
    public const string PlcInputPrefix = "PLC_IN_";
    public const string PlcOutputPrefix = "PLC_OUT_";

    public static bool IsPlcInput(string? slot) =>
        slot?.StartsWith(PlcInputPrefix, StringComparison.OrdinalIgnoreCase) == true;

    public static bool IsPlcOutput(string? slot) =>
        slot?.StartsWith(PlcOutputPrefix, StringComparison.OrdinalIgnoreCase) == true;

    public static string? GetDuplicateError(string? slot, int assignmentCount)
    {
        if (assignmentCount <= 1)
            return null;

        var displaySlot = string.IsNullOrWhiteSpace(slot) ? "<leer>" : slot.Trim();
        if (IsPlcInput(displaySlot))
            return null;

        if (IsPlcOutput(displaySlot))
        {
            return $"Ausgänge können nicht doppelt verschaltet werden. " +
                   $"Slot '{displaySlot}' ist {assignmentCount}-mal belegt.";
        }

        return $"Slot '{displaySlot}' ist {assignmentCount}-mal belegt. " +
               "Mehrfachbelegungen sind ausschließlich für PLC_IN_-Slots zulässig.";
    }
}
