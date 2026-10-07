using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace VIBN_Tools.Application.Behaviors;

/// <summary>Restores the user's splitter width when a docked column is reopened.</summary>
public static class CollapsibleColumnBehavior
{
    private sealed record Size(GridLength Width, double Minimum);
    private static readonly ConditionalWeakTable<ColumnDefinition, Size> Sizes = new();
    public static readonly DependencyProperty IsVisibleProperty = DependencyProperty.RegisterAttached(
        "IsVisible", typeof(bool), typeof(CollapsibleColumnBehavior), new PropertyMetadata(true, OnChanged));
    public static bool GetIsVisible(DependencyObject element) => (bool)element.GetValue(IsVisibleProperty);
    public static void SetIsVisible(DependencyObject element, bool value) => element.SetValue(IsVisibleProperty, value);

    private static void OnChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ColumnDefinition column) return;
        if (!(bool)args.NewValue)
        {
            Sizes.Remove(column);
            Sizes.Add(column, new Size(column.Width, column.MinWidth));
            column.MinWidth = 0; column.Width = new GridLength(0);
        }
        else if (Sizes.TryGetValue(column, out var size))
        {
            column.Width = size.Width; column.MinWidth = size.Minimum;
            Sizes.Remove(column);
        }
    }
}
