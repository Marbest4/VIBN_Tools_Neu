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
