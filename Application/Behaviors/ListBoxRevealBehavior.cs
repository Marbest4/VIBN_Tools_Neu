using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace VIBN_Tools.Application.Behaviors;

/// <summary>Reveals a programmatically selected item in a virtualized ListBox.</summary>
public static class ListBoxRevealBehavior
{
    public static readonly DependencyProperty RevealItemProperty = DependencyProperty.RegisterAttached(
        "RevealItem",
        typeof(object),
        typeof(ListBoxRevealBehavior),
        new PropertyMetadata(null, OnRevealItemChanged));

    public static object? GetRevealItem(DependencyObject element) => element.GetValue(RevealItemProperty);
    public static void SetRevealItem(DependencyObject element, object? value) => element.SetValue(RevealItemProperty, value);

    private static void OnRevealItemChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not ListBox listBox || args.NewValue is null)
            return;
        var item = args.NewValue;
        _ = listBox.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!listBox.Items.Contains(item))
                return;
            listBox.ScrollIntoView(item);
            listBox.UpdateLayout();
            if (listBox.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container ||
                FindVisualChild<ScrollViewer>(listBox) is not { } viewer)
                return;
            try
            {
                if (viewer.CanContentScroll)
                {
                    var itemIndex = listBox.Items.IndexOf(item);
                    if (itemIndex >= 0 && double.IsFinite(viewer.ViewportHeight))
                    {
                        viewer.ScrollToVerticalOffset(Math.Max(
                            0d,
                            itemIndex - Math.Floor(viewer.ViewportHeight / 2d)));
                    }
                    return;
                }

                var position = container.TransformToAncestor(viewer).Transform(new Point(0, 0));
                var delta = position.Y - Math.Max(0d, (viewer.ViewportHeight - container.ActualHeight) / 2d);
                viewer.ScrollToVerticalOffset(Math.Max(0d, viewer.VerticalOffset + delta));
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            {
                // The list may have been rebuilt again before the deferred centering ran.
            }
        }));
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;
            if (FindVisualChild<T>(child) is { } descendant)
                return descendant;
        }
        return null;
    }
}
