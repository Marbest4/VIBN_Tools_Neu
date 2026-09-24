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
        if (dependencyObject is not DataGrid grid || args.NewValue is null)
            return;

        _ = grid.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!grid.Items.Contains(args.NewValue))
                return;
            grid.ScrollIntoView(args.NewValue);
            grid.UpdateLayout();
            var viewer = FindVisualChild<ScrollViewer>(grid);
            var itemIndex = grid.Items.IndexOf(args.NewValue);
            if (viewer is not null && itemIndex >= 0)
            {
                var targetOffset = viewer.CanContentScroll
                    ? itemIndex - (viewer.ViewportHeight / 2d)
                    : (itemIndex * Math.Max(grid.RowHeight, 1d)) - (viewer.ViewportHeight / 2d);
                viewer.ScrollToVerticalOffset(Math.Max(0d, targetOffset));
                grid.UpdateLayout();
            }
            if (grid.ItemContainerGenerator.ContainerFromItem(args.NewValue) is DataGridRow row)
                row.BringIntoView();
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
