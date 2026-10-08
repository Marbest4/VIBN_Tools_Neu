using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace VIBN_Tools.Application.Behaviors;

/// <summary>
/// Brings a related row into the virtualized viewport without changing the
/// user's active selection. This keeps cross-list navigation deterministic
/// even when the target row has not been materialized yet.
/// </summary>
public static class DataGridRevealBehavior
{
    private static readonly DependencyProperty RevealRevisionProperty = DependencyProperty.RegisterAttached(
        "RevealRevision",
        typeof(long),
        typeof(DataGridRevealBehavior),
        new PropertyMetadata(0L));

    public static readonly DependencyProperty RevealItemProperty = DependencyProperty.RegisterAttached(
        "RevealItem",
        typeof(object),
        typeof(DataGridRevealBehavior),
        new PropertyMetadata(null, OnRevealItemChanged));

    public static object? GetRevealItem(DependencyObject element) =>
        element.GetValue(RevealItemProperty);

    public static void SetRevealItem(DependencyObject element, object? value) =>
        element.SetValue(RevealItemProperty, value);

    private static void OnRevealItemChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not DataGrid grid)
            return;

        var revision = (long)grid.GetValue(RevealRevisionProperty) + 1;
        grid.SetValue(RevealRevisionProperty, revision);
        if (args.NewValue is null)
            return;
        var requestedItem = args.NewValue;

        QueueReveal(grid, requestedItem, revision, attempt: 0);
    }

    private static void QueueReveal(DataGrid grid, object item, long revision, int attempt)
    {
        if (grid.Dispatcher.HasShutdownStarted || grid.Dispatcher.HasShutdownFinished) return;
        _ = grid.Dispatcher.BeginInvoke(attempt == 0 ? DispatcherPriority.Loaded : DispatcherPriority.ContextIdle,
            new Action(() =>
        {
            if ((long)grid.GetValue(RevealRevisionProperty) != revision || !grid.IsLoaded) return;
            try
            {
                var index = grid.Items.IndexOf(item);
                if (index < 0 || index >= grid.Items.Count) return;
                var viewer = grid.Template?.FindName("DG_ScrollViewer", grid) as ScrollViewer ?? FindVisualChild<ScrollViewer>(grid);
                if (viewer is null || !double.IsFinite(viewer.ViewportHeight) || viewer.ViewportHeight <= 0) return;
                var logical = viewer.CanContentScroll && VirtualizingPanel.GetScrollUnit(grid) == ScrollUnit.Item;
                var row = grid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
                var rowHeight = row is { ActualHeight: > 0 } ? row.ActualHeight
                    : double.IsFinite(grid.RowHeight) && grid.RowHeight > 0 ? grid.RowHeight : 24d;
                double offset;
                if (row is not null && attempt > 0)
                {
                    var presenter = viewer.Template?.FindName("PART_ScrollContentPresenter", viewer) as FrameworkElement;
                    var viewport = presenter ?? (FrameworkElement)viewer;
                    var top = row.TransformToAncestor(viewport).Transform(new Point(0, 0)).Y;
                    var pixelDelta = top - Math.Max(0d, (viewport.ActualHeight - rowHeight) / 2d);
                    offset = viewer.VerticalOffset + pixelDelta / (logical ? rowHeight : 1d);
                }
                else
                    offset = logical ? index - Math.Max(0d, (viewer.ViewportHeight - 1d) / 2d)
                        : index * rowHeight - Math.Max(0d, (viewer.ViewportHeight - rowHeight) / 2d);
                if (!double.IsFinite(offset)) return;
                viewer.ScrollToVerticalOffset(Math.Clamp(offset, 0d, Math.Max(0d, viewer.ScrollableHeight)));
                // Let layout and the generator finish before correcting the
                // realized row's position. UpdateLayout inside the selection
                // notification can revive stale row indexes during root changes.
                if (attempt < 2) QueueReveal(grid, item, revision, attempt + 1);
            }
            catch (ArgumentOutOfRangeException) { }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }
        }));
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;
            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
                return descendant;
        }
        return null;
    }
}
