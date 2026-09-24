using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VIBN_Tools.Application.Behaviors;

/// <summary>Provides a bindable TreeView.SelectedItem for the visual plan.</summary>
public static class ContainerToFeeVisualTreeSelectionBehavior
{
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
            treeView.Loaded += OnLoaded;
            treeView.Unloaded += OnUnloaded;
            if (treeView.IsLoaded)
                AttachItemsSource(treeView);
        }
        else
        {
            treeView.SelectedItemChanged -= OnSelectedItemChanged;
            treeView.PreviewKeyDown -= OnPreviewKeyDown;
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
        state.HorizontalOffset = scrollViewer.HorizontalOffset;
        state.RestorePending = true;
        treeView.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            try
            {
                var current = FindVisualChild<ScrollViewer>(treeView);
                current?.ScrollToVerticalOffset(state.VerticalOffset);
                current?.ScrollToHorizontalOffset(state.HorizontalOffset);
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

    private static void OnBoundSelectedItemChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not TreeView treeView || args.NewValue is null)
            return;

        treeView.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var container = FindContainer(treeView, args.NewValue);
            if (container is null)
                return;
            container.IsSelected = true;
            // Rebuilding the immutable-facing plan tree after drag/drop must
            // not send the user back to its beginning. Keep the edited node
            // as the visual scroll anchor.
            container.BringIntoView();
        });
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
        public double VerticalOffset { get; set; }
        public double HorizontalOffset { get; set; }
    }
}
