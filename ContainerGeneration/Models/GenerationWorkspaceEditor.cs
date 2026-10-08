using VIBN_Tools.ContainerGeneration.BusinessLogic.ContainerData;

namespace VIBN_Tools.ContainerGeneration.Models;

public sealed record UnassignEntryResult(
    bool WasAssigned,
    int RemovedDuplicateOccurrences,
    int RemovedEmptyContainers);

public sealed record MergeContainersResult(
    int MovedSignals,
    int RemovedContainers);

/// <summary>
/// Centralized, identity-based operations for the editable generation
/// workspace. An imported signal may exist in exactly one location.
/// </summary>
public static class GenerationWorkspaceEditor
{
    public static UnassignEntryResult MoveToUnassigned(
        ContainerEntry entry,
        string? restoredSignal,
        IList<ContainerData> containers,
        IList<ContainerEntry> unassigned,
        IList<ContainerEntry> filtered)
    {
        if (!string.IsNullOrWhiteSpace(restoredSignal))
            entry.Signal = restoredSignal;

        return MoveToOpenList(
            entry,
            containers,
            unassigned,
            filtered,
            unassigned);
    }

    public static UnassignEntryResult MoveToFiltered(
        ContainerEntry entry,
        IList<ContainerData> containers,
        IList<ContainerEntry> unassigned,
        IList<ContainerEntry> filtered) =>
        MoveToOpenList(
            entry,
            containers,
            unassigned,
            filtered,
            filtered);

    public static void MoveToContainer(
        ContainerEntry entry,
        ContainerData target,
        IList<ContainerData> containers,
        IList<ContainerEntry> unassigned,
        IList<ContainerEntry> filtered)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(target);

        foreach (var container in containers.ToList())
        {
            var matches = container.DataList
                .Where(candidate => SameSource(candidate, entry))
                .ToList();
            foreach (var match in matches)
                container.DataList.Remove(match);
        }

        RemoveAllMatches(unassigned, entry);
        RemoveAllMatches(filtered, entry);

        if (!containers.Contains(target))
            containers.Add(target);
        target.DataList.Add(entry);

        RemoveEmptyContainers(containers, target);
    }

    /// <summary>
    /// Moves all signals from the source containers into one target container.
    /// Signal identity is preserved and empty source containers are removed.
    /// </summary>
    public static MergeContainersResult MergeContainers(
        IEnumerable<ContainerData> sources,
        ContainerData target,
        IList<ContainerData> containers,
        IList<ContainerEntry> unassigned,
        IList<ContainerEntry> filtered)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(target);

        var sourceList = sources
            .Where(source => source is not null && !ReferenceEquals(source, target))
            .Distinct()
            .ToList();
        var beforeCount = containers.Count;
        var moved = 0;

        var entries = sourceList.SelectMany(source => source.DataList).Distinct().ToArray();
        MoveToContainerBatch(entries, target, containers, unassigned, filtered);
        moved = entries.Length;

        target.ManuallyChecked = false;
        target.Validate();
        target.RefreshReimportStatus();
        return new MergeContainersResult(moved, Math.Max(0, beforeCount - containers.Count));
    }

    public static void MoveToContainerBatch(IEnumerable<ContainerEntry> entries, ContainerData target,
        IList<ContainerData> containers, IList<ContainerEntry> unassigned, IList<ContainerEntry> filtered)
    {
        ArgumentNullException.ThrowIfNull(target);
        MoveBatch(entries, target, null, containers, unassigned, filtered);
    }

    public static void MoveToUnassignedBatch(IEnumerable<ContainerEntry> entries,
        IList<ContainerData> containers, IList<ContainerEntry> unassigned, IList<ContainerEntry> filtered) =>
        MoveBatch(entries, null, unassigned, containers, unassigned, filtered);

    public static void MoveToFilteredBatch(IEnumerable<ContainerEntry> entries,
        IList<ContainerData> containers, IList<ContainerEntry> unassigned, IList<ContainerEntry> filtered) =>
        MoveBatch(entries, null, filtered, containers, unassigned, filtered);

    private static void MoveBatch(IEnumerable<ContainerEntry> entries, ContainerData? target,
        IList<ContainerEntry>? openTarget, IList<ContainerData> containers,
        IList<ContainerEntry> unassigned, IList<ContainerEntry> filtered)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(containers);
        ArgumentNullException.ThrowIfNull(unassigned);
        ArgumentNullException.ThrowIfNull(filtered);
        var selection = entries.ToArray();
        if (selection.Any(entry => entry is null)) throw new ArgumentException("A signal selection contains null.", nameof(entries));
        selection = selection.DistinctBy(entry => string.IsNullOrWhiteSpace(entry.SignalId)
            ? "source:" + GenerationWorkspaceReconciler.CreatePrimaryKey(entry) : "id:" + entry.SignalId).ToArray();
        if (selection.Length == 0) return;

        // Preserve SameSource's fallback for older entries without SignalId,
        // while keeping different stable IDs distinct even with equal names.
        var ids = selection.Where(entry => !string.IsNullOrWhiteSpace(entry.SignalId)).Select(entry => entry.SignalId).ToHashSet(StringComparer.Ordinal);
        var keys = selection.Select(GenerationWorkspaceReconciler.CreatePrimaryKey).ToHashSet(StringComparer.Ordinal);
        var legacyKeys = selection.Where(entry => string.IsNullOrWhiteSpace(entry.SignalId))
            .Select(GenerationWorkspaceReconciler.CreatePrimaryKey).ToHashSet(StringComparer.Ordinal);
        bool Matches(ContainerEntry entry) => string.IsNullOrWhiteSpace(entry.SignalId)
            ? keys.Contains(GenerationWorkspaceReconciler.CreatePrimaryKey(entry))
            : ids.Contains(entry.SignalId) || legacyKeys.Contains(GenerationWorkspaceReconciler.CreatePrimaryKey(entry));
        using (new ContainerUpdateBatch(containers.Concat(target is null ? Array.Empty<ContainerData>() : new[] { target })))
        {
            foreach (var container in containers)
                RemoveMatches(container.DataList, Matches);
            RemoveMatches(unassigned, Matches);
            RemoveMatches(filtered, Matches);
            if (target is not null && !containers.Contains(target)) containers.Add(target);
            foreach (var entry in selection)
            {
                if (target is not null) target.DataList.Add(entry);
                else { entry.Slot = string.Empty; openTarget!.Add(entry); }
            }
            RemoveEmptyContainers(containers, target);
        }
    }

    private static void RemoveMatches(IList<ContainerEntry> entries, Func<ContainerEntry, bool> matches)
    {
        for (var index = entries.Count - 1; index >= 0; index--)
            if (matches(entries[index])) entries.RemoveAt(index);
    }

    private static UnassignEntryResult MoveToOpenList(
        ContainerEntry entry,
        IList<ContainerData> containers,
        IList<ContainerEntry> unassigned,
        IList<ContainerEntry> filtered,
        IList<ContainerEntry> target)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(containers);
        ArgumentNullException.ThrowIfNull(unassigned);
        ArgumentNullException.ThrowIfNull(filtered);

        var wasAssigned = false;
        var removedOccurrences = 0;

        foreach (var container in containers.ToList())
        {
            var matches = container.DataList
                .Where(candidate => SameSource(candidate, entry))
                .ToList();

            if (matches.Count == 0)
                continue;

            wasAssigned = true;
            foreach (var match in matches)
            {
                container.DataList.Remove(match);
                removedOccurrences++;
            }

            container.Validate();
            container.RefreshReimportStatus();
        }

        var openListOccurrences =
            unassigned.Count(candidate => SameSource(candidate, entry)) +
            filtered.Count(candidate => SameSource(candidate, entry));
        RemoveAllMatches(unassigned, entry);
        RemoveAllMatches(filtered, entry);
        target.Add(entry);

        entry.Slot = string.Empty;

        var removedEmptyContainers = RemoveEmptyContainers(containers);

        return new UnassignEntryResult(
            wasAssigned,
            Math.Max(0, removedOccurrences - 1) + openListOccurrences,
            removedEmptyContainers);
    }

    private static int RemoveEmptyContainers(
        IList<ContainerData> containers,
        ContainerData? except = null)
    {
        var removed = 0;
        for (var index = containers.Count - 1; index >= 0; index--)
        {
            if (ReferenceEquals(containers[index], except) ||
                containers[index].DataList.Count != 0)
            {
                continue;
            }

            containers.RemoveAt(index);
            removed++;
        }

        return removed;
    }

    public static bool SameSource(ContainerEntry left, ContainerEntry right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (ReferenceEquals(left, right))
            return true;

        if (!string.IsNullOrWhiteSpace(left.SignalId) &&
            !string.IsNullOrWhiteSpace(right.SignalId))
        {
            return string.Equals(
                left.SignalId,
                right.SignalId,
                StringComparison.Ordinal);
        }

        return string.Equals(
            GenerationWorkspaceReconciler.CreatePrimaryKey(left),
            GenerationWorkspaceReconciler.CreatePrimaryKey(right),
            StringComparison.Ordinal);
    }

    public static void RemoveAllMatches(
        IList<ContainerEntry> entries,
        ContainerEntry reference)
    {
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            if (SameSource(entries[index], reference))
                entries.RemoveAt(index);
        }
    }
}
