using VIBN_Tools.ContainerGeneration.BusinessLogic.ContainerData;

namespace VIBN_Tools.ContainerGeneration.Models;

/// <summary>
/// A complete, independent copy of the editable container workspace.
/// Capture plain values immediately; build and validate observable container
/// models only when undo actually restores them, not during every drop.
/// </summary>
public sealed class WorkspaceUndoState
{
    public string Description { get; }
    private readonly IReadOnlyList<ContainerSnapshot> _containers;
    private IReadOnlyList<ContainerData>? _restoredContainers;
    public IReadOnlyList<ContainerData> Containers =>
        _restoredContainers ??= _containers.Select(RestoreContainer).ToArray();
    public IReadOnlyList<ContainerEntry> Unassigned { get; }
    public IReadOnlyList<ContainerEntry> Filtered { get; }
    public System.Xml.XmlElement? FeeInventory { get; }

    private WorkspaceUndoState(
        string description,
        IReadOnlyList<ContainerSnapshot> containers,
        IReadOnlyList<ContainerEntry> unassigned,
        IReadOnlyList<ContainerEntry> filtered,
        System.Xml.XmlElement? feeInventory)
    {
        Description = description;
        _containers = containers;
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
            containers.Select(container => new ContainerSnapshot(
                container.Id, container.Component, container.Type, container.MinSignals, container.MaxSignals,
                container.ManuallyChecked, container.Slots.ToArray(),
                container.DataList.Select(entry => entry.Clone()).ToArray(),
                container.SimObjects.Select(item => item.Clone()).ToArray())).ToArray(),
            unassigned.Select(entry => entry.Clone()).ToList(),
            filtered.Select(entry => entry.Clone()).ToList(), feeInventory);

    private sealed record ContainerSnapshot(string Id, string Component, string Type,
        int? MinSignals, int? MaxSignals, bool ManuallyChecked, string[] Slots,
        ContainerEntry[] Entries, ContainerFeeObject[] SimObjects);

    private static ContainerData RestoreContainer(ContainerSnapshot source)
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

        foreach (var entry in source.Entries)
            clone.DataList.Add(entry);

        foreach (var item in source.SimObjects)
            clone.SimObjects.Add(item);

        clone.Validate();
        clone.RefreshReimportStatus();
        return clone;
    }
}
