using System.Xml.Linq;

namespace VIBN_Tools.ContainerToFeeVisual;

public sealed record FeeContainerVariableState(
    Guid VariableGuid,
    string Signal,
    string Address,
    string Path,
    string DataType,
    string Comment);

public sealed record FeeContainerVariableProjectionResult(
    FeeContainerProvenanceSnapshot Snapshot,
    int UpdatedEntries,
    IReadOnlyList<Guid> MissingVariableGuids,
    int UpdatedSlots,
    IReadOnlyList<Guid> UnresolvedSlotVariableGuids);

public static class FeeContainerVariableProjector
{
    public static FeeContainerVariableProjectionResult Apply(
        FeeContainerProvenanceSnapshot snapshot,
        IEnumerable<FeeContainerVariableState> variables,
        IReadOnlyDictionary<Guid, string>? slotsByVariable = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(variables);
        var byGuid = variables
            .GroupBy(variable => variable.VariableGuid)
            .ToDictionary(group => group.Key, group => group.First());
        var document = new XDocument(snapshot.ContainerDocument);
        var containers = document.Descendants()
            .Where(element => element.Name.LocalName == "Container").ToArray();
        var missing = new HashSet<Guid>();
        var unresolvedSlots = new HashSet<Guid>();
        var updated = 0;
        var updatedSlots = 0;

        foreach (var binding in snapshot.SignalBindings)
        {
            if (!byGuid.TryGetValue(binding.VariableGuid, out var variable))
            {
                missing.Add(binding.VariableGuid);
                continue;
            }

            var entries = containers[binding.ContainerIndex].Descendants()
                .Where(element => element.Name.LocalName == "Entry").ToArray();
            var entry = entries[binding.EntryIndex];
            SetChild(entry, "Signal", variable.Signal);
            SetChild(entry, "Address",
                string.IsNullOrWhiteSpace(variable.Path) ? variable.Address : variable.Path);
            SetChild(entry, "DataType", variable.DataType);
            SetChild(entry, "ID", variable.Comment);
            if (slotsByVariable is not null)
            {
                if (slotsByVariable.TryGetValue(binding.VariableGuid, out var slot) &&
                    !string.IsNullOrWhiteSpace(slot))
                {
                    SetChild(entry, "Slot", slot);
                    updatedSlots++;
                }
                else
                {
                    unresolvedSlots.Add(binding.VariableGuid);
                }
            }
            updated++;
        }

        return new FeeContainerVariableProjectionResult(
            snapshot with { ContainerDocument = document },
            updated,
            missing.OrderBy(guid => guid).ToArray(),
            updatedSlots,
            unresolvedSlots.OrderBy(guid => guid).ToArray());
    }

    private static void SetChild(XElement parent, string localName, string? value)
    {
        var element = parent.Elements()
            .FirstOrDefault(child => child.Name.LocalName == localName);
        if (element is null)
            parent.Add(new XElement(localName, value ?? string.Empty));
        else
            element.Value = value ?? string.Empty;
    }
}
