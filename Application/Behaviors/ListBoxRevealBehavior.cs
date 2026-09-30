using System.Windows;
using System.Windows.Controls;
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
            if (listBox.Items.Contains(item))
                listBox.ScrollIntoView(item);
        }));
    }
}
