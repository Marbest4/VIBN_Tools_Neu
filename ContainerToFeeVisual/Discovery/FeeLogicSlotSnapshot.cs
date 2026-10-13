using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFeeVisual;

internal static class FeeLogicSlotSnapshot
{
    // XML can omit unassigned slots. Resolve names once with the explicit scene
    // refresh, never on root/interface selection. Existing assignment GUIDs stay intact.
    internal static async Task PopulateAsync(IEnumerable<FeeAbstractObject> objects, CancellationToken token)
    {
        using var throttle = new SemaphoreSlim(4);
        var reads = objects.OfType<FeeLogic>().Where(item => item.LogicDefinitionGuid != Guid.Empty &&
                (item.Slots?.Count is not > 0 || string.Equals(item.LogicDefinitionName, "UNDEFINED", StringComparison.OrdinalIgnoreCase)))
            .Select(async logic =>
            {
                await throttle.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    var slots = await Services.ApiInstance.Object.GetSlotNamesAsync(logic.Guid);
                    token.ThrowIfCancellationRequested();
                    logic.Slots ??= new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
                    foreach (var slot in slots ?? [])
                        if (!string.IsNullOrWhiteSpace(slot)) logic.Slots.TryAdd(slot, Guid.Empty);
                }
                catch (Exception exception) when (!token.IsCancellationRequested)
                {
                    new VisualPlanLogger().Warning($"Slots der Logik '{logic.Name}' konnten nicht gelesen werden: {exception.GetBaseException().Message}");
                }
                finally { throttle.Release(); }
            });
        await Task.WhenAll(reads);
        token.ThrowIfCancellationRequested();
    }
}
