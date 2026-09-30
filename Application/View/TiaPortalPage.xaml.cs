using System.Windows.Controls;
using VIBN_Tools.Application;
using VIBN_Tools.Application.VM;

namespace VIBN_Tools.Application.View;

public partial class TiaPortalPage : UserControl
{
    private readonly TiaPortalPageVM _viewModel;

    public TiaPortalPage()
    {
        InitializeComponent();
        _viewModel = ViCoFeatureBootstrapper.CreateTiaPortalViewModel();
        DataContext = _viewModel;
    }
}
