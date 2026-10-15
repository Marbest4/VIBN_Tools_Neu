using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFeeVisual;

internal static class FeeLogicSlotSnapshot
{
    // XML can omit unassigned slots. Resolve names once with the explicit scene
    // refresh, never on root/interface selection. Existing assignment GUIDs stay intact.
    internal static async Task PopulateAsync(IEnumerable<FeeAbstractObject> objects, CancellationToken token)
    {
        var candidates = objects.OfType<FeeLogic>().DistinctBy(item => item.Guid).ToArray();
        var completed = 0;
        FeeReadProgressScope.Report("Logik-Slots lesen", 0, candidates.Length, "Logiken");
        foreach (var logic in candidates)
            {
                token.ThrowIfCancellationRequested();
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
                finally { FeeReadProgressScope.Report("Logik-Slots lesen", Interlocked.Increment(ref completed), candidates.Length, "Logiken"); }
            }
        token.ThrowIfCancellationRequested();
    }
}
