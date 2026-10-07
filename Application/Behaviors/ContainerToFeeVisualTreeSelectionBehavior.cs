using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VIBN_Tools.Application.VM;

namespace VIBN_Tools.Application.Behaviors;

/// <summary>Provides a bindable TreeView.SelectedItem for the visual plan.</summary>
public static class ContainerToFeeVisualTreeSelectionBehavior
{
    static ContainerToFeeVisualTreeSelectionBehavior()
    {
        // Intercept at the item, before the enclosing ScrollViewer can enqueue
        // a horizontal MakeVisible request (restoring the offset later flickers).
        EventManager.RegisterClassHandler(typeof(TreeViewItem), FrameworkElement.RequestBringIntoViewEvent,
            new RequestBringIntoViewEventHandler(OnRequestBringIntoView), true);
    }

    private static readonly DependencyProperty SelectionRevealRevisionProperty = DependencyProperty.RegisterAttached(
        "SelectionRevealRevision",
        typeof(long),
        typeof(ContainerToFeeVisualTreeSelectionBehavior),
        new PropertyMetadata(0L));

    private static readonly ConditionalWeakTable<TreeView, ScrollState> ScrollStates = new();
    public static readonly DependencyProperty SelectedItemProperty =
        DependencyProperty.RegisterAttached(
            "SelectedItem",
            typeof(object),
            typeof(ContainerToFeeVisualTreeSelectionBehavior),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnBoundSelectedItemChanged));

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(ContainerToFeeVisualTreeSelectionBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty RevealEnabledProperty = DependencyProperty.RegisterAttached(
        "RevealEnabled", typeof(bool), typeof(ContainerToFeeVisualTreeSelectionBehavior), new PropertyMetadata(true));
    public static bool GetRevealEnabled(DependencyObject element) => (bool)element.GetValue(RevealEnabledProperty);
    public static void SetRevealEnabled(DependencyObject element, bool value) => element.SetValue(RevealEnabledProperty, value);

    public static readonly DependencyProperty DeleteCommandProperty =
        DependencyProperty.RegisterAttached(
            "DeleteCommand",
            typeof(ICommand),
            typeof(ContainerToFeeVisualTreeSelectionBehavior));

    public static object? GetSelectedItem(DependencyObject element) => element.GetValue(SelectedItemProperty);

    public static void SetSelectedItem(DependencyObject element, object? value) =>
        element.SetValue(SelectedItemProperty, value);

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static ICommand? GetDeleteCommand(DependencyObject element) =>
        (ICommand?)element.GetValue(DeleteCommandProperty);

    public static void SetDeleteCommand(DependencyObject element, ICommand? value) =>
        element.SetValue(DeleteCommandProperty, value);

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not TreeView treeView)
            return;

        if ((bool)args.NewValue)
        {
            treeView.SelectedItemChanged += OnSelectedItemChanged;
            treeView.PreviewKeyDown += OnPreviewKeyDown;
            treeView.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            treeView.Loaded += OnLoaded;
            treeView.Unloaded += OnUnloaded;
            if (treeView.IsLoaded)
                AttachItemsSource(treeView);
        }
        else
        {
            treeView.SelectedItemChanged -= OnSelectedItemChanged;
            treeView.PreviewKeyDown -= OnPreviewKeyDown;
            treeView.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
            treeView.Loaded -= OnLoaded;
            treeView.Unloaded -= OnUnloaded;
            DetachItemsSource(treeView);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is TreeView treeView)
            AttachItemsSource(treeView);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is TreeView treeView)
            DetachItemsSource(treeView);
    }

    private static void AttachItemsSource(TreeView treeView)
    {
        var state = ScrollStates.GetOrCreateValue(treeView);
        if (ReferenceEquals(state.Source, treeView.ItemsSource))
            return;
        DetachItemsSource(treeView);
        state.Source = treeView.ItemsSource as INotifyCollectionChanged;
        if (state.Source is not null)
            state.Source.CollectionChanged += state.OnCollectionChanged = (_, _) => PreserveScrollOffset(treeView, state);
    }

    private static void DetachItemsSource(TreeView treeView)
    {
        if (!ScrollStates.TryGetValue(treeView, out var state) || state.Source is null || state.OnCollectionChanged is null)
            return;
        state.Source.CollectionChanged -= state.OnCollectionChanged;
        state.Source = null;
        state.OnCollectionChanged = null;
        state.RestorePending = false;
    }

    private static void PreserveScrollOffset(TreeView treeView, ScrollState state)
    {
        if (state.RestorePending)
            return;
        var scrollViewer = FindVisualChild<ScrollViewer>(treeView);
        if (scrollViewer is null)
            return;
        state.VerticalOffset = scrollViewer.VerticalOffset;
        var revealRevision = (long)treeView.GetValue(SelectionRevealRevisionProperty);
        state.RestorePending = true;
        treeView.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            try
            {
                if ((long)treeView.GetValue(SelectionRevealRevisionProperty) != revealRevision)
                    return;
                var current = FindVisualChild<ScrollViewer>(treeView);
                current?.ScrollToVerticalOffset(state.VerticalOffset);
                current?.ScrollToLeftEnd();
            }
            finally
            {
                state.RestorePending = false;
            }
        });
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Delete || sender is not TreeView treeView || treeView.SelectedItem is null)
            return;
        var command = GetDeleteCommand(treeView);
        if (command?.CanExecute(treeView.SelectedItem) != true)
            return;
        command.Execute(treeView.SelectedItem);
        args.Handled = true;
    }

    private static void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> args)
    {
        if (sender is TreeView treeView)
            SetSelectedItem(treeView, args.NewValue);
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (sender is not TreeView tree ||
            ItemsControl.ContainerFromElement(tree, args.OriginalSource as DependencyObject) is not TreeViewItem ||
            FindVisualChild<ScrollViewer>(tree) is not { } viewer)
            return;
        var state = ScrollStates.GetOrCreateValue(tree);
        state.IsSelecting = true;
        var vertical = viewer.VerticalOffset;
        _ = tree.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            state.IsSelecting = false;
            viewer.ScrollToVerticalOffset(vertical);
            viewer.ScrollToLeftEnd();
        }));
    }

    private static void OnRequestBringIntoView(object sender, RequestBringIntoViewEventArgs args)
    {
        if (args.Handled) return;
        if (sender is not TreeViewItem item) return;
        DependencyObject? parent = item;
        while (parent is not null && parent is not TreeView)
            parent = VisualTreeHelper.GetParent(parent);
        if (parent is not TreeView tree || !GetIsEnabled(tree)) return;
        args.Handled = true;
        FindVisualChild<ScrollViewer>(tree)?.ScrollToLeftEnd();
        if (GetRevealEnabled(tree) && !(ScrollStates.TryGetValue(tree, out var state) && state.IsSelecting))
            CenterContainer(tree, item);
    }

    private static void OnBoundSelectedItemChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not TreeView treeView)
            return;

        var revision = (long)treeView.GetValue(SelectionRevealRevisionProperty) + 1;
        treeView.SetValue(SelectionRevealRevisionProperty, revision);
        if (args.NewValue is null || !GetRevealEnabled(treeView))
            return;

        // Keep the viewport stable when the user clicked the tree itself.
        // Selections originating in one of the related lists still center the
        // requested tree item because the keyboard focus then belongs to that
        // source list.
        if (ScrollStates.TryGetValue(treeView, out var state) && state.IsSelecting ||
            treeView.IsKeyboardFocusWithin && ReferenceEquals(treeView.SelectedItem, args.NewValue))
            return;

        treeView.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!GetRevealEnabled(treeView) || (long)treeView.GetValue(SelectionRevealRevisionProperty) != revision)
                return;
            try
            {
                treeView.UpdateLayout();
                var container = RealizeContainer(treeView, args.NewValue) ?? FindContainer(treeView, args.NewValue);
                if (container is null || (long)treeView.GetValue(SelectionRevealRevisionProperty) != revision)
                    return;
                container.IsSelected = true;
                treeView.UpdateLayout();
                CenterContainer(treeView, container);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            {
                // A deferred reveal can outlive the collection it was requested for.
            }
        });
    }

    private static void CenterContainer(TreeView treeView, FrameworkElement container)
    {
        var viewer = FindVisualChild<ScrollViewer>(treeView);
        if (viewer is null)
            return;
        try
        {
            var header = container is TreeViewItem treeItem
                ? treeItem.Template?.FindName("PART_Header", treeItem) as FrameworkElement ?? container
                : container;
            var position = header.TransformToAncestor(viewer).Transform(new Point(0, 0));
            var delta = position.Y - Math.Max(0d, (viewer.ViewportHeight - header.ActualHeight) / 2d);
            var scrollDelta = viewer.CanContentScroll && VirtualizingPanel.GetScrollUnit(treeView) == ScrollUnit.Item
                ? delta / Math.Max(1d, header.ActualHeight)
                : delta;
            viewer.ScrollToVerticalOffset(Math.Max(0d, viewer.VerticalOffset + scrollDelta));
            viewer.ScrollToLeftEnd();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // A freshly rebuilt tree can replace the container before centering.
        }
    }

    private static TreeViewItem? RealizeContainer(ItemsControl parent, object item)
    {
        // Only realize the branch containing the target. Expanded ancestors
        // can still be outside the viewport and have no generated containers.
        var childItem = parent.Items.Cast<object>().FirstOrDefault(candidate =>
            ReferenceEquals(candidate, item) || candidate is ContainerToFeeVisualTreeNodeVM node &&
            node.SelfAndDescendants().Any(descendant => ReferenceEquals(descendant, item)));
        if (childItem is null)
            return null;
        var index = parent.Items.IndexOf(childItem);
        if (parent.ItemContainerGenerator.ContainerFromItem(childItem) is not TreeViewItem)
        {
            FindVisualChild<VirtualizingStackPanel>(parent)?.BringIndexIntoViewPublic(index);
            parent.UpdateLayout();
        }
        if (parent.ItemContainerGenerator.ContainerFromItem(childItem) is not TreeViewItem child)
            return null;
        if (ReferenceEquals(childItem, item))
            return child;
        child.IsExpanded = true;
        child.UpdateLayout();
        return RealizeContainer(child, item);
    }

    private static TreeViewItem? FindContainer(ItemsControl parent, object item)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem direct)
            return direct;
        foreach (var childItem in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(childItem) is not TreeViewItem child)
                continue;
            var nested = FindContainer(child, item);
            if (nested is not null)
                return nested;
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;
            if (FindVisualChild<T>(child) is { } nested)
                return nested;
        }
        return null;
    }

    private sealed class ScrollState
    {
        public INotifyCollectionChanged? Source { get; set; }
        public NotifyCollectionChangedEventHandler? OnCollectionChanged { get; set; }
        public bool RestorePending { get; set; }
        public bool IsSelecting { get; set; }
        public double VerticalOffset { get; set; }
    }
}
