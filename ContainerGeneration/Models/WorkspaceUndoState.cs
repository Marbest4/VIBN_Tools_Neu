using VIBN_Tools.ContainerGeneration.BusinessLogic.ContainerData;

namespace VIBN_Tools.ContainerGeneration.Models;

/// <summary>
/// A complete, independent copy of the editable container workspace.
/// Keeping full states makes undo deterministic across moves, deletes,
/// slot edits and automatic removal of empty containers.
/// </summary>
public sealed class WorkspaceUndoState
{
    public string Description { get; }
    public IReadOnlyList<ContainerData> Containers { get; }
    public IReadOnlyList<ContainerEntry> Unassigned { get; }
    public IReadOnlyList<ContainerEntry> Filtered { get; }
    public System.Xml.XmlElement? FeeInventory { get; }

    private WorkspaceUndoState(
        string description,
        IReadOnlyList<ContainerData> containers,
        IReadOnlyList<ContainerEntry> unassigned,
        IReadOnlyList<ContainerEntry> filtered,
        System.Xml.XmlElement? feeInventory)
    {
        Description = description;
        Containers = containers;
        Unassigned = unassigned;
        Filtered = filtered;
        FeeInventory = feeInventory is null ? null : (System.Xml.XmlElement)feeInventory.CloneNode(true);
    }

    public static WorkspaceUndoState Capture(
        string description,
        IEnumerable<ContainerData> containers,
        IEnumerable<ContainerEntry> unassigned,
        IEnumerable<ContainerEntry> filtered,
        System.Xml.XmlElement? feeInventory = null) =>
        new(
            description,
            containers.Select(CloneContainer).ToList(),
            unassigned.Select(entry => entry.Clone()).ToList(),
            filtered.Select(entry => entry.Clone()).ToList(), feeInventory);

    private static ContainerData CloneContainer(ContainerData source)
    {
        var clone = new ContainerData();
        using var updates = clone.DeferUpdates();
        clone.Id = source.Id;
        clone.Component = source.Component;
        clone.Type = source.Type;
        clone.MinSignals = source.MinSignals;
        clone.MaxSignals = source.MaxSignals;
        clone.ManuallyChecked = source.ManuallyChecked;

        clone.Slots.Clear();
        foreach (var slot in source.Slots)
            clone.Slots.Add(slot);

        foreach (var entry in source.DataList)
            clone.DataList.Add(entry.Clone());

        foreach (var item in source.SimObjects)
            clone.SimObjects.Add(item.Clone());

        clone.Validate();
        clone.RefreshReimportStatus();
        return clone;
    }
}
