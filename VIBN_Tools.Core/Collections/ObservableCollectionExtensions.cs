using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace VIBN_Tools.Core.Collections;

public static class ObservableCollectionExtensions
{
    /// <summary>
    /// Replaces all items while retaining the bound collection instance. Collections
    /// created as <see cref="RangeObservableCollection{T}"/> publish one reset event
    /// instead of one event per row; this keeps large WPF lists responsive.
    /// </summary>
    public static void ReplaceWith<T>(
        this ObservableCollection<T> target,
        IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(items);

        if (target is RangeObservableCollection<T> rangeCollection)
        {
            rangeCollection.ReplaceRange(items);
            return;
        }

        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }
}

/// <summary>
/// Observable collection optimized for replacing complete result sets. WPF sees
/// the completed state atomically and cannot navigate transient row indices.
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        // Snapshot first: callers may pass a filtered view over this same
        // collection, which would otherwise be emptied before enumeration.
        var replacement = items.ToArray();

        CheckReentrancy();
        Items.Clear();
        foreach (var item in replacement)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
