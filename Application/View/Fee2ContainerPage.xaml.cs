using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Controls.Primitives;

namespace VIBN_Tools.Application.View;

public partial class Fee2ContainerPage : UserControl
{
    public Fee2ContainerPage()
    {
        InitializeComponent();
        PreviewKeyDown += OnDeleteKey;
    }
    private void OnDeleteKey(object sender, KeyEventArgs args)
    {
        if (args.Handled || args.Key != Key.Delete || Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox ||
            DataContext is not VIBN_Tools.Application.VM.Fee2ContainerPageVM vm || vm.IsBusy) return;
        var grid = FindGrids(this).LastOrDefault(item => item.IsKeyboardFocusWithin);
        if (grid is null) return;
        var selected = grid.SelectedItems.OfType<VIBN_Tools.Application.VM.Fee2ContainerFoundSignalVM>().ToArray();
        foreach (var signal in selected) vm.RemoveSignalCommand.Execute(signal);
        if (selected.Length > 0) args.Handled = true;
    }
    private void CommitPendingEdits(object sender, RoutedEventArgs args)
    {
        foreach (var grid in FindGrids(this))
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);
        }
    }

    private void OnContainerSignalSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is DataGrid grid && args.OriginalSource == grid &&
            grid.SelectedItem is VIBN_Tools.Application.VM.Fee2ContainerFoundSignalVM signal &&
            DataContext is VIBN_Tools.Application.VM.Fee2ContainerPageVM vm)
        {
            vm.SelectedFoundSignal = signal;
            args.Handled = true;
        }
    }

    private void OnCabinetElementSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is DataGrid grid && args.OriginalSource == grid &&
            grid.SelectedItem is VIBN_Tools.Application.VM.Fee2ContainerFoundContainerVM element &&
            DataContext is VIBN_Tools.Application.VM.Fee2ContainerPageVM vm)
        { vm.SelectedFoundSignal = element.DataList.FirstOrDefault(); args.Handled = true; }
    }

    private void OpenSpecialDevices(object sender, RoutedEventArgs args)
        => TransferSpecialDevices(append: false);

    private void TransferSpecialDevices(bool append, VIBN_Tools.Application.VM.Fee2ContainerFoundContainerVM? container = null)
    {
        if (System.Windows.Application.Current?.MainWindow is not { } main) return;
        foreach (var tabs in FindControls<TabControl>(main))
            foreach (var tab in tabs.Items.OfType<TabItem>())
                if (string.Equals(tab.Header?.ToString(), "FEE2SpecialDevices", StringComparison.Ordinal) && tab.IsEnabled)
                {
                    tabs.SelectedItem = tab;
                    _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                    {
                        if (tab.Content is LazyPageHost host) host.EnsureContent();
                        var target = FindControls<Fee2SpecialDevicesPage>(main).FirstOrDefault();
                        if (target?.DataContext is not VIBN_Tools.Application.VM.Fee2SpecialDevicesPageVM devices) return;
                        var current = (DataContext as VIBN_Tools.Application.VM.Fee2ContainerPageVM)?.CreateSpecialDeviceCandidates(container);
                        if (current is { Count: > 0 })
                        {
                            if (append) devices.AddDevices(current);
                            else devices.LoadDevices(current);
                        }
                        else if (devices.RefreshCommand.CanExecute(null)) devices.RefreshCommand.Execute(null);
                    }));
                    return;
                }
    }

    private void OnAddToSpecialDevices(object sender, RoutedEventArgs args)
    {
        CommitPendingEdits(sender, args);
        // WPF executes the bound command after Click. Transfer only after its
        // classification and workspace history have been updated.
        var container = (sender as Button)?.CommandParameter as VIBN_Tools.Application.VM.Fee2ContainerFoundContainerVM;
        _ = Dispatcher.BeginInvoke(new Action(() => TransferSpecialDevices(append: true, container)));
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
            foreach (var nested in FindGrids(child)) yield return nested;
        }
    }
}
