using VIBN_Tools.GlobalClasses.FeeObjects;
using static VIBN_Tools.GlobalClasses.Interfaces;

namespace VIBN_Tools.ContainerToFee.General;

/// <summary>Cabinet group identity; child records keep their own concrete CabinetElement types and signal slots.</summary>
public sealed class Cabinet_Container : ContainerBaseClass, ISimObjectOwner, ICreatableContainer
{
    private static readonly CabinetContainerManager Cabinets = new();
    public Cabinet_Container() => SlotAssignment = new();
    public bool IsCreationRequested { get; set; }
    public async Task CreateSimObjectsAsync(FeeAbstractObject parentObject) =>
        _ = await Cabinets.GetOrCreateCabinetAsync(ComponentName, parentObject, preferredGuid: Guid.TryParse(SourceCabinetGuid, out var guid) ? guid : null);
    public Task AssignSignalsAsync(FeeInterface targetInterface) => Task.CompletedTask;
}
