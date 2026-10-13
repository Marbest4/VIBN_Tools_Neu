using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;

namespace VIBN_Tools.Application.View;

public partial class Fee2ContainerPage : UserControl
{
    public Fee2ContainerPage()
    {
        InitializeComponent();
    }
    private void CommitPendingEdits(object sender, RoutedEventArgs args)
    {
        foreach (var grid in FindGrids(this))
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);
        }
    }

    private void OpenSpecialDevices(object sender, RoutedEventArgs args)
    {
        if (System.Windows.Application.Current?.MainWindow is not { } main) return;
        foreach (var tabs in FindControls<TabControl>(main))
            foreach (var tab in tabs.Items.OfType<TabItem>())
                if (string.Equals(tab.Header?.ToString(), "FEE2SpecialDevices", StringComparison.Ordinal) && tab.IsEnabled)
                {
                    tabs.SelectedItem = tab;
                    return;
                }
    }

    private static IEnumerable<T> FindControls<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T control) yield return control;
            foreach (var descendant in FindControls<T>(child)) yield return descendant;
        }
    }
    private static IEnumerable<DataGrid> FindGrids(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is DataGrid grid) yield return grid;
            else foreach (var nested in FindGrids(child)) yield return nested;
        }
    }
}
