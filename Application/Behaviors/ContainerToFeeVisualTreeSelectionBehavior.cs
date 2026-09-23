using System.Windows;
using System.Windows.Controls;

namespace VIBN_Tools.Application.Behaviors;

/// <summary>Provides a bindable TreeView.SelectedItem for the visual plan.</summary>
public static class ContainerToFeeVisualTreeSelectionBehavior
{
    public static readonly DependencyProperty SelectedItemProperty =
        DependencyProperty.RegisterAttached(
            "SelectedItem",
            typeof(object),
            typeof(ContainerToFeeVisualTreeSelectionBehavior),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

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
}
