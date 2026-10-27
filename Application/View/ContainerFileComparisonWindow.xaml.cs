using System.Windows;
using VIBN_Tools.Application.VM;

namespace VIBN_Tools.Application.View;

public partial class ContainerFileComparisonWindow : Window
{
    public ContainerFileComparisonWindow(ContainerFileComparisonVM viewModel)
    {
        InitializeComponent(); DataContext = viewModel;
        Closing += (_, args) => { if (viewModel.IsBusy) args.Cancel = true; };
    }
}
