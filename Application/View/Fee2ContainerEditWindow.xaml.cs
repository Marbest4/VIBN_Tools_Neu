using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VIBN_Tools.Application.VM;

namespace VIBN_Tools.Application.View;

public partial class Fee2ContainerEditWindow : Window
{
    public Fee2ContainerEditWindow(Fee2ContainerEditVM viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
    private void CommitTables(object sender, RoutedEventArgs args)
    {
        foreach (var grid in FindGrids(this))
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);
        }
    }
    private void Apply_Click(object sender, RoutedEventArgs args)
    {
        CommitTables(sender, args);
        if (DataContext is Fee2ContainerEditVM vm && !vm.ApplyXmlIfNeeded()) return;
        DialogResult = true;
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
