using System.Collections.ObjectModel;

namespace VIBN_Tools.Core.Collections;

public static class ObservableCollectionExtensions
{
    /// <summary>Replaces all items while retaining the bound collection instance.</summary>
    public static void ReplaceWith<T>(
        this ObservableCollection<T> target,
        IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(items);

        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }
}
