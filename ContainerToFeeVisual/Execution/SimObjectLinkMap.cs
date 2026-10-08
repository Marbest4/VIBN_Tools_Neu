using VIBN_Tools.GlobalClasses.FeeObjects;
using static VIBN_Tools.GlobalClasses.FeeObjects.FeeLogic;

namespace VIBN_Tools.ContainerToFeeVisual;

internal sealed record RequiredSimObjectLink(Guid ObjectGuid, string ObjectSlot, string LogicSlot);

/// <summary>Uses the same endpoints as the forward containers, without their labels, parameter writes or object creation.</summary>
internal static class SimObjectLinkMap
{
    internal static IReadOnlyList<RequiredSimObjectLink> Create(string containerType, FeeAbstractObject item, int index)
    {
        var slots = new List<(string ObjectSlot, string LogicSlot)>();
        if (item is FeeJoint)
        {
            var motion = containerType switch
            {
                "Cylinder" or "FeedSafetyDoor" => (LogicsStandard.Grob_Cylinder.Slots.TargetPosition,
                    LogicsStandard.Grob_Cylinder.Slots.Velocity, LogicsStandard.Grob_Cylinder.Slots.ActualPosition),
                "LiftUnit" => (LogicsStandard.Grob_LiftUnit.Slots.TargetPosition,
                    LogicsStandard.Grob_LiftUnit.Slots.Velocity, LogicsStandard.Grob_LiftUnit.Slots.ActualPosition),
                "GripperBasic" => (LogicsStandard.Grob_GripperBasic.Slots.TargetPosition,
                    LogicsStandard.Grob_GripperBasic.Slots.Velocity, LogicsStandard.Grob_GripperBasic.Slots.ActualPosition),
                _ => ("", "", ""),
            };
            if (motion.Item1.Length > 0)
            {
                slots.Add(("InTarget", motion.Item1)); slots.Add(("InVelocity", motion.Item2));
                if (index == 0) slots.Add(("OutValue", motion.Item3));
            }
        }
        else if (item is FeeSurface && containerType == "Conveyor")
            slots.Add(("InVelocityX", LogicsStandard.Grob_Conveyor.Slots.VelocityOut));
        else if (item is FeeSensor && containerType == "Sensor")
            slots.Add(("Channel1", LogicsStandard.Grob_Sensor.Slots.SensorValue));
        else if (item is FeeFloor && containerType == "Stop")
            slots.Add(("Collision", LogicsStandard.Grob_Stop.Slots.Collision));
        else if (item is FeePickAndPlace && containerType == "GripperBasic")
        {
            slots.Add(("Pick", LogicsStandard.Grob_GripperBasic.Slots.Pick));
            slots.Add(("Drop", LogicsStandard.Grob_GripperBasic.Slots.Drop));
            if (index == 0) slots.Add(("Feedback", LogicsStandard.Grob_GripperBasic.Slots.PartPicked));
        }
        else if (item is FeePickAndPlace && containerType == "GripperVacuum")
        {
            slots.Add(("Pick", LogicsStandard.Grob_GripperVacuum.Slots.Pick));
            slots.Add(("Drop", LogicsStandard.Grob_GripperVacuum.Slots.Drop));
        }
        return slots.Select(slot => new RequiredSimObjectLink(item.Guid, slot.ObjectSlot, slot.LogicSlot)).ToArray();
    }

    internal static bool IsLinked(RequiredSimObjectLink expected, string logicGuid, IReadOnlyList<VisualFeeObjectLink> links) =>
        links.Any(link =>
            string.Equals(link.ObjectGuidString, expected.ObjectGuid.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(link.SlotName, expected.ObjectSlot, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(link.LinkedObjectGuidString, logicGuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(link.LinkedSlotName, expected.LogicSlot, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(link.LinkedObjectGuidString, expected.ObjectGuid.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(link.LinkedSlotName, expected.ObjectSlot, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(link.ObjectGuidString, logicGuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(link.SlotName, expected.LogicSlot, StringComparison.OrdinalIgnoreCase));
}
