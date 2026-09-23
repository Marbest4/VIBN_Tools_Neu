using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VIBN_Tools.Application.Behaviors;

/// <summary>Provides a bindable TreeView.SelectedItem for the visual plan.</summary>
public static class ContainerToFeeVisualTreeSelectionBehavior
{
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

    public static object? GetSelectedItem(DependencyObject element) => element.GetValue(SelectedItemProperty);

    public static void SetSelectedItem(DependencyObject element, object? value) =>
        element.SetValue(SelectedItemProperty, value);

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not TreeView treeView)
            return;

        if ((bool)args.NewValue)
            treeView.SelectedItemChanged += OnSelectedItemChanged;
        else
            treeView.SelectedItemChanged -= OnSelectedItemChanged;
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
}
