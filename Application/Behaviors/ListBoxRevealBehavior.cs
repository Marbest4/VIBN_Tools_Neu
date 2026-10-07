using System.Windows;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VIBN_Tools.Application.Behaviors;

/// <summary>Reveals a programmatically selected item in a virtualized ListBox.</summary>
public static class ListBoxRevealBehavior
{
    private static readonly ConditionalWeakTable<ListBox, InputState> InputStates = new();

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ListBoxRevealBehavior), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ListBox list)
            return;
        if ((bool)args.NewValue)
            list.PreviewMouseLeftButtonDown += PreserveInputViewport;
        else
            list.PreviewMouseLeftButtonDown -= PreserveInputViewport;
    }

    private static void PreserveInputViewport(object sender, MouseButtonEventArgs args)
    {
        if (sender is not ListBox list ||
            ItemsControl.ContainerFromElement(list, args.OriginalSource as DependencyObject) is not ListBoxItem ||
            FindVisualChild<ScrollViewer>(list) is not { } viewer)
            return;
        var state = InputStates.GetOrCreateValue(list);
        var revision = ++state.Revision;
        state.IsSelecting = true;
        var vertical = viewer.VerticalOffset;
        var horizontal = viewer.HorizontalOffset;
        _ = list.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (revision != state.Revision)
                return;
            state.IsSelecting = false;
            viewer.ScrollToVerticalOffset(vertical);
            viewer.ScrollToHorizontalOffset(horizontal);
        }));
    }
    private static readonly DependencyProperty RevealRevisionProperty = DependencyProperty.RegisterAttached(
        "RevealRevision",
        typeof(long),
        typeof(ListBoxRevealBehavior),
        new PropertyMetadata(0L));

    public static readonly DependencyProperty RevealItemProperty = DependencyProperty.RegisterAttached(
        "RevealItem",
        typeof(object),
        typeof(ListBoxRevealBehavior),
        new PropertyMetadata(null, OnRevealItemChanged));

    public static object? GetRevealItem(DependencyObject element) => element.GetValue(RevealItemProperty);
    public static void SetRevealItem(DependencyObject element, object? value) => element.SetValue(RevealItemProperty, value);

    private static void OnRevealItemChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not ListBox listBox)
            return;

        var revision = (long)listBox.GetValue(RevealRevisionProperty) + 1;
        listBox.SetValue(RevealRevisionProperty, revision);
        if (args.NewValue is null || !GetIsEnabled(listBox))
            return;

        // A direct mouse/keyboard selection is already visible by definition.
        // Revealing it again makes virtualized lists jump (usually to the top)
        // before the cross-list synchronization has finished. Only lists that
        // are not the active input source are programmatically centered.
        if (InputStates.TryGetValue(listBox, out var input) && input.IsSelecting ||
            listBox.IsKeyboardFocusWithin && ReferenceEquals(listBox.SelectedItem, args.NewValue))
            return;

        var item = args.NewValue;
        _ = listBox.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!GetIsEnabled(listBox) || (long)listBox.GetValue(RevealRevisionProperty) != revision)
                return;
            if (!listBox.Items.Contains(item))
                return;
            try
            {
                listBox.ScrollIntoView(item);
                listBox.UpdateLayout();
                if (listBox.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container ||
                    FindVisualChild<ScrollViewer>(listBox) is not { } viewer)
                    return;
                if (viewer.CanContentScroll && VirtualizingPanel.GetScrollUnit(listBox) == ScrollUnit.Item)
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

    private sealed class InputState
    {
        public long Revision { get; set; }
        public bool IsSelecting { get; set; }
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
