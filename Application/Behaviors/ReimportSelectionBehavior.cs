using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VIBN_Tools.ContainerGeneration.Models;

namespace VIBN_Tools.Application.Behaviors;

/// <summary>Toggles all selected comparison rows with the Space key.</summary>
public static class ReimportSelectionBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(ReimportSelectionBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not DataGrid grid)
            return;

        grid.PreviewKeyDown -= OnPreviewKeyDown;
        if (e.NewValue is true)
            grid.PreviewKeyDown += OnPreviewKeyDown;
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || sender is not DataGrid grid)
            return;

        var selected = grid.SelectedItems.OfType<ReimportDifference>().ToArray();
        if (selected.Length == 0)
            return;

        var newValue = !selected.All(change => change.IsAccepted);
        foreach (var change in selected)
            change.IsAccepted = newValue;

        e.Handled = true;
    }
}
