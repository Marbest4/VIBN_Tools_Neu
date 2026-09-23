using System.Reflection;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFee.General;

/// <summary>
/// Known AutoCreate signal group for workpiece orientation. The master rules
/// define two interface signals but no FEE logic or SimObject. Treating it as
/// a typed signal-only container keeps validation/provenance intact without
/// inventing a runtime model that is absent from Requirements.xml.
/// </summary>
public sealed class PartOrientation_Container : ContainerBaseClass
{
    public PartOrientation_Container()
    {
        SlotAssignment = new Dictionary<string, PropertyInfo>
        {
            ["PLC_orientation_OK"] = typeof(PartOrientation_Container).GetProperty(nameof(SignalOrientationOk))!,
            ["PLC_orientation_nOK"] = typeof(PartOrientation_Container).GetProperty(nameof(SignalOrientationNotOk))!,
        };
    }

    public FeeInterfaceSignal? SignalOrientationOk { get; set; }

    public FeeInterfaceSignal? SignalOrientationNotOk { get; set; }
}
