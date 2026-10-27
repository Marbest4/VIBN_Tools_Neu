using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace VIBN_Tools.Application.Behaviors;

/// <summary>Keeps visible rows at their screen position while a drop changes row heights or removes rows above them.</summary>
internal static class DataGridViewportPreservation
{
    public static IDisposable Capture(DataGrid source)
    {
        DependencyObject scope = source;
        while (VisualTreeHelper.GetParent(scope) is { } parent)
        {
            scope = parent;
            if (scope is UserControl) break;
        }
        return new RestoreScope(Children<DataGrid>(scope).Prepend(source).Distinct()
            .Where(grid => grid.IsVisible).Select(Snapshot.Create).Where(item => item is not null).Cast<Snapshot>().ToArray());
    }

    private sealed class RestoreScope(Snapshot[] snapshots) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in snapshots) item.Restore(0);
        }
    }

    private sealed class Snapshot(DataGrid grid, ScrollViewer viewer, FrameworkElement viewport,
        (object Item, double Top)[] anchors, double vertical, double horizontal)
    {
        private readonly RequestBringIntoViewEventHandler _suppressBringIntoView = (_, args) => args.Handled = true;
        public static Snapshot? Create(DataGrid grid)
        {
            var viewer = grid.Template?.FindName("DG_ScrollViewer", grid) as ScrollViewer ?? Children<ScrollViewer>(grid).FirstOrDefault();
            if (viewer is null) return null;
            var viewport = viewer.Template?.FindName("PART_ScrollContentPresenter", viewer) as FrameworkElement ?? viewer;
            var anchors = Children<DataGridRow>(grid).Where(row => row.IsVisible && row.Item != CollectionView.NewItemPlaceholder)
                .Select(row => (Item: row.Item, Top: row.TranslatePoint(new Point(), viewport).Y, Height: row.ActualHeight))
                .Where(row => row.Top + row.Height > 0 && row.Top < viewport.ActualHeight).OrderBy(row => row.Top)
                .Select(row => (row.Item, row.Top)).ToArray();
            var result = new Snapshot(grid, viewer, viewport, anchors, viewer.VerticalOffset, viewer.HorizontalOffset);
            grid.RequestBringIntoView += result._suppressBringIntoView;
            return result;
        }

        public void Restore(int attempt)
        {
            if (grid.Dispatcher.HasShutdownStarted) { grid.RequestBringIntoView -= _suppressBringIntoView; return; }
            _ = grid.Dispatcher.BeginInvoke(attempt == 0 ? DispatcherPriority.Loaded : DispatcherPriority.ContextIdle, new Action(() =>
            {
                try
                {
                    if (!grid.IsLoaded) return;
                    var offset = vertical;
                    foreach (var anchor in anchors)
                        if (grid.Items.Contains(anchor.Item) && grid.ItemContainerGenerator.ContainerFromItem(anchor.Item) is DataGridRow row)
                        {
                            var delta = row.TranslatePoint(new Point(), viewport).Y - anchor.Top;
                            var logical = viewer.CanContentScroll && VirtualizingPanel.GetScrollUnit(grid) == ScrollUnit.Item;
                            offset = viewer.VerticalOffset + delta / (logical ? Math.Max(1, row.ActualHeight) : 1);
                            break;
                        }
                    if (double.IsFinite(offset)) viewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, Math.Max(0, viewer.ScrollableHeight)));
                    viewer.ScrollToHorizontalOffset(horizontal);
                }
                catch (InvalidOperationException) { }
                catch (ArgumentException) { }
                finally
                {
                    if (attempt == 0) Restore(1);
                    else grid.RequestBringIntoView -= _suppressBringIntoView;
                }
            }));
        }
    }

    private static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            else foreach (var descendant in Children<T>(child)) yield return descendant;
        }
    }
}
